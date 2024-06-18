using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;

public class Backends
{
    private List<BackendHost> _hosts;
    private List<BackendHost> _activeHosts;

    private HttpClient _client = new HttpClient();
    private int _interval;

    public Backends(List<BackendHost> hosts, HttpClient client, int interval)
    {
        _hosts = hosts;
        _client = client;
        _activeHosts = new List<BackendHost>();
        _interval = interval;
    }

    public void Start()
    {
//        _thread.Start();
        Task.Run(() => Run());

    }   

    public List<BackendHost> GetActiveHosts()
    {
        return _activeHosts;
    }

    private async Task Run() {

        Dictionary<string, bool> currentHostStatus = new Dictionary<string, bool>();

        while (true)
        {
            var activeHosts = new List<BackendHost>();
            bool statusChanged = false;
            bool currentStatus=false;

            foreach (var host in _hosts)
            {
                //Console.WriteLine($"Checking host {host.url + host.probe_path}");

                try {
                    // Start the stopwatch
                    var stopwatch = Stopwatch.StartNew();
                    currentStatus = false;

                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_client.Timeout.TotalMilliseconds));
                    var response = await _client.GetAsync(host.url + host.probe_path, cts.Token);

                    // Stop the stopwatch and calculate the latency
                    stopwatch.Stop();
                    var latency = stopwatch.Elapsed.TotalMilliseconds;
                    activeHosts.Add(host);

                    // Update the host with the new latency
                    host.AddLatency(latency);

                    // If the response is successful, add the host to the active hosts
                    currentStatus= response.IsSuccessStatusCode;
                    response.EnsureSuccessStatusCode();

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


            // update the active hosts
            _activeHosts.Clear();


            // Sort the active hosts based on low latency and high success rate 
            activeHosts.Sort((a, b) => a.AverageLatency().CompareTo(b.AverageLatency()));
            _activeHosts.AddRange(activeHosts);

            if ( statusChanged )
            {
                if (Program.telemetryClient is null)
                {
                    Console.WriteLine($"\n\n{DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff")} ============ Host Status =========");
                }
                        // Loop through the active hosts and output latency
                foreach (var host in activeHosts)
                {
                    Console.WriteLine($"Host: {host.url} Latency: {Math.Round(host.AverageLatency(), 3)}ms");
                }
            }

            Thread.Sleep(_interval);
        }
    }

}