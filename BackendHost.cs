using OS = System;
using System.Collections.Generic;


public class BackendHost
{
    public string host;
    public int port;
    public string protocol;
    public string probe_path;

    string? _url = null;
    public string url => _url ??= new UriBuilder(protocol, host, port).Uri.AbsoluteUri;

    // Queue to store the latencies of the last 5 calls
    public Queue<double> latencies = new Queue<double>(5);

    // Queue to store the success of the last 5 calls
    public Queue<bool> callSuccess = new Queue<bool>(5);

    public BackendHost(string hostname, string? probepath)
    {

        // If host does not have a protocol, add one
        if (!hostname.StartsWith("http://") && !hostname.StartsWith("https://"))
        {
            hostname = "http://" + host;
        }

        // parse the host, prototol and port
        Uri uri = new Uri(hostname);
        protocol = uri.Scheme;
        port = uri.Port;
        host = uri.Host;

        probe_path = probe_path ?? "echo/resource?param1=sample";
        if (probe_path.StartsWith("/"))
        {
            probe_path = probe_path.Substring(1);
        }
    }
    public override string ToString()
    {
        return $"{protocol}://{host}:{port}";
    }

    // Method to add a new latency
    public void AddLatency(double latency)
    {
        // If there are already 5 latencies in the queue, remove the oldest one
        if (latencies.Count == 5)
            latencies.Dequeue();

        // Add the new latency to the queue
        latencies.Enqueue(latency);
    }

    // Method to calculate the average latency
    public double AverageLatency()
    {
        // If there are no latencies, return 0.0
        if (latencies.Count == 0)
            return 0.0;

        // Otherwise, return the average latency + penalty for each failed call
        return latencies.Average() + (1 - SuccessRate()) * 100;
    }

    // Method to track the success of a call
    public void AddCallSuccess(bool success)
    {
        // If there are already 5 call results in the queue, remove the oldest one
        if (callSuccess.Count == 5)
            callSuccess.Dequeue();

        // Add the new call result to the queue
        callSuccess.Enqueue(success);
    }

    // Method to calculate the success rate
    public double SuccessRate()
    {
        // If there are no call results, return 0.0
        if (callSuccess.Count == 0)
            return 0.0;

        // Otherwise, return the success rate
        return (double)callSuccess.Count(x => x) / callSuccess.Count;
    }
}