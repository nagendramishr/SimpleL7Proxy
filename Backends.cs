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
    Dictionary<string, bool> currentHostStatus = new Dictionary<string, bool>();
    private async Task Run(CancellationToken cancellationToken) {

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
            bool statusChanged = false;

            try {
                await UpdateHostStatus(_client, cancellationToken);
                FilterActiveHosts();            

                if ( statusChanged || (DateTime.Now - _lastStatusDisplay).TotalSeconds > 60)
                {
                    DisplayHostStatus();
                }

            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Operation was canceled. Stopping the server.");
                break;
            }
            catch (Exception e)
            {
                Console.WriteLine($"An unexpected error occurred: {e.Message}");
            }

            await Task.Delay(_options.PollInterval, cancellationToken);
        }
    }

    
    private async Task<bool> UpdateHostStatus(HttpClient _client, CancellationToken cancellationToken)
    {
        var _statusChanged = false;

        if (_hosts == null)
        {
            return _statusChanged;
        }   

        foreach (var host in _hosts )
        {
            var currentStatus = await GetHostStatus(host, _client, cancellationToken);
            bool statusChanged = !currentHostStatus.ContainsKey(host.host) || currentHostStatus[host.host] != currentStatus;

            currentHostStatus[host.host] = currentStatus;
            host.AddCallSuccess(currentStatus);

            if (statusChanged)
            {
                _statusChanged = true;
            }
        }

        return _statusChanged;
    }

    private async Task<bool> GetHostStatus(BackendHost host, HttpClient client, CancellationToken cancellationToken)
    {
        if (_debug)
            Console.WriteLine($"Checking host {host.url + host.probe_path}");

        var request = new HttpRequestMessage(HttpMethod.Get, host.probeurl);
        var stopwatch = Stopwatch.StartNew();

        try
        {

            var response = await client.SendAsync(request, cancellationToken);
            stopwatch.Stop();
            var latency = stopwatch.Elapsed.TotalMilliseconds;

            // Update the host with the new latency
            host.AddLatency(latency);

            response.EnsureSuccessStatusCode();

            _isRunning = true;

            // If the response is successful, add the host to the active hosts
            return response.IsSuccessStatusCode;
        }
        catch (UriFormatException e) {
            Program.telemetryClient?.TrackException(e);
            Console.WriteLine($"Could not check probe: {e.Message}");
        }
        catch (System.Threading.Tasks.TaskCanceledException) {
            Console.WriteLine($"Host Timeout: {host.host}");
        }
        catch (HttpRequestException e) {
            Program.telemetryClient?.TrackException(e);
            Console.WriteLine($"Host {host.host} is down with exception: {e.Message}");
        }
        catch (OperationCanceledException) {
            // Handle the cancellation request (e.g., break the loop, log the cancellation, etc.)
            Console.WriteLine("Operation was canceled. Stopping the server.");
            throw; // Exit the loop
        }
        catch (System.Net.Sockets.SocketException e) {
            Console.WriteLine($"Host {host.host} is down:  {e.Message}");
        }
        catch (Exception e) {
            Program.telemetryClient?.TrackException(e);
            Console.WriteLine($"Error: {e.Message}");
        }

        return false;
    }

    // Filter the active hosts based on the success rate
    private void FilterActiveHosts()
    {
        _activeHosts = _hosts
            .Where(h => h.SuccessRate() > _successRate)
            .OrderBy(h => h.AverageLatency())
            .ToList();
    }

    // Display the status of the hosts
    private void DisplayHostStatus()
    {
        Console.WriteLine("\n\n");
        Console.WriteLine("\n\n============ Host Status =========");

        if (_hosts != null )
            foreach (var host in _hosts )
            {
                string statusIndicator = host.SuccessRate() > _successRate ? "Good  " : "Errors";
                double roundedLatency = Math.Round(host.AverageLatency(), 3);
                double successRatePercentage = Math.Round(host.SuccessRate() * 100, 2);

                Console.WriteLine($"{statusIndicator} Host: {host.url} Latency: {roundedLatency}ms Success Rate: {successRatePercentage}%");
            }

        _lastStatusDisplay = DateTime.Now;
    }

}