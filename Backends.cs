using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Options;

public class Backends : IBackendService
{
    private List<BackendHost> _hosts;
    private List<BackendHost> _activeHosts;

    private BackendOptions _options;
    private static bool _debug=false;

    private static double _successRate;
    private static DateTime _lastStatusDisplay = DateTime.Now;
    private static bool _isRunning = false;

    //public Backends(List<BackendHost> hosts, HttpClient client, int interval, int successRate)
    public Backends(IOptions<BackendOptions> options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.Value == null) throw new ArgumentNullException(nameof(options.Value));
        if (options.Value.Hosts == null) throw new ArgumentNullException(nameof(options.Value.Hosts));
        if (options.Value.Client == null) throw new ArgumentNullException(nameof(options.Value.Client));

        var bo = options.Value; // Access the IBackendOptions instance

        _hosts = bo.Hosts;
        _options = bo;
        _activeHosts = new List<BackendHost>();
        _successRate = bo.SuccessRate / 100.0;
    }

    public void Start(CancellationToken cancellationToken)
    {
        Task.Run(() => Run(cancellationToken));
    }   

    public List<BackendHost> GetActiveHosts()
    {
        return _activeHosts;
    }

    public async Task waitForStartup(int timeout)
    {
        var start = DateTime.Now;
        for (int i=0; i < 10; i++ ) 
        {
            var startTimer = DateTime.Now;
            while (!_isRunning && (DateTime.Now - startTimer).TotalSeconds < timeout)
            {
                await Task.Delay(1000); // Use Task.Delay for asynchronous wait
            }
            if (!_isRunning)
            {
                Console.WriteLine($"Backend Poller did not start in the last {timeout} seconds.");
            }
            else
            {
                Console.WriteLine($"Backend Poller started in {(DateTime.Now - start).TotalSeconds} seconds.");
                return;
            }
        }
        throw new Exception("Backend Poller did not start in time.");
    }
    private async Task Run(CancellationToken cancellationToken) {

        Dictionary<string, bool> currentHostStatus = new Dictionary<string, bool>();

        HttpClient _client = new HttpClient();
        if (Environment.GetEnvironmentVariable("IgnoreSSLCert")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true) {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
            _client = new HttpClient(handler);
        }

        var intervalTime = TimeSpan.FromMilliseconds(_options.PollInterval).ToString(@"hh\:mm\:ss");
        var timeoutTime = TimeSpan.FromMilliseconds(_options.PollTimeout).ToString(@"hh\:mm\:ss\.fff");
        Console.WriteLine($"Starting Backend Poller: Interval: {intervalTime}, SuccessRate: {_successRate.ToString()}, Timeout: {timeoutTime}");

        _client.Timeout = TimeSpan.FromMilliseconds(_options.PollTimeout);
        while (!cancellationToken.IsCancellationRequested) 
        {
            //var activeHosts = new List<BackendHost>();
            bool statusChanged = false;
            bool currentStatus=false;

            foreach (var host in _hosts)
            {
                if (_debug)
                    Console.WriteLine($"Checking host {host.url + host.probe_path}");

                currentStatus = false;
                try {
                    // Start the stopwatch
                    var stopwatch = Stopwatch.StartNew();
                    currentStatus = false;

                    var response = await _client.GetAsync(host.probeurl, cancellationToken);

                    // Stop the stopwatch and calculate the latency
                    stopwatch.Stop();
                    var latency = stopwatch.Elapsed.TotalMilliseconds;
                    //activeHosts.Add(host);

                    // Update the host with the new latency
                    host.AddLatency(latency);

                    // If the response is successful, add the host to the active hosts
                    currentStatus= response.IsSuccessStatusCode;
                    response.EnsureSuccessStatusCode();

                    _isRunning = true;

                } catch (UriFormatException e) {
                    Program.telemetryClient?.TrackException(e);
                    Console.WriteLine($"Could not check probe: {e.Message}");
                } catch (System.Threading.Tasks.TaskCanceledException) {
                    Console.WriteLine($"Host Timeout: {host.host}");
                }
                catch (HttpRequestException e) {
                    Program.telemetryClient?.TrackException(e);
                    Console.WriteLine($"Host {host} is down with exception: {e.Message}");
                }
                catch (OperationCanceledException)
                {
                    // Handle the cancellation request (e.g., break the loop, log the cancellation, etc.)
                    Console.WriteLine("Operation was canceled. Stopping the server.");
                    break; // Exit the loop
                }
                catch (System.Net.Sockets.SocketException)
                {}
                catch (Exception e)
                {
                    Program.telemetryClient?.TrackException(e);
                    Console.WriteLine($"Error: {e.Message}");
                }
                
                if (!currentHostStatus.ContainsKey(host.host) || currentHostStatus[host.host] != currentStatus)
                {
                    statusChanged = true;
                }

                currentHostStatus[host.host] = currentStatus;
                host.AddCallSuccess(currentStatus);
            }

            // Find hosts that have a success rate over 80%
            _activeHosts = _hosts.Where(h => h.SuccessRate() > _successRate).ToList();
            // Sort the active hosts based on low latency  
            _activeHosts.Sort((a, b) => a.AverageLatency().CompareTo(b.AverageLatency()));
            //_activeHosts.AddRange(activeHosts);

            if ( statusChanged || (DateTime.Now - _lastStatusDisplay).TotalSeconds > 60)
            {
                Console.WriteLine("\n\n");
                Console.WriteLine($"\n\n============ Host Status =========");

                // Loop through the active hosts and output latency
                foreach (var host in _activeHosts)
                {
                    Console.WriteLine($"Host: {host.url} Latency: {Math.Round(host.AverageLatency(), 3)}ms  Success Rate: {Math.Round(host.SuccessRate() * 100, 2)}%");
                }

                _lastStatusDisplay = DateTime.Now;
            }

            await Task.Delay(_options.PollInterval, cancellationToken);
        }
    }

}