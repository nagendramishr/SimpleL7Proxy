using OS = System;
using System.Collections.Generic;


public class BackendHost
{
    public string host;
    public string? ipaddr;
    public int port;
    public string protocol;
    public string probe_path;

    string? _url = null;
    string? _probeurl = null;
    public string url => _url ??= new UriBuilder(protocol, ipaddr ?? host, port).Uri.AbsoluteUri;

    public string probeurl => _probeurl ??= System.Net.WebUtility.UrlDecode( new UriBuilder(protocol, ipaddr ?? host, port, probe_path).Uri.AbsoluteUri );

    private const int MaxData = 50;
    private readonly Queue<double> latencies = new Queue<double>();
    private readonly Queue<bool> callSuccess = new Queue<bool>();

    private readonly Queue<double> PxLatency = new Queue<double>();

    public BackendHost(string hostname, string? probepath, string? ipaddress)
    {


        // If host does not have a protocol, add one
        if (!hostname.StartsWith("http://") && !hostname.StartsWith("https://"))
        {
            hostname = "https://" + hostname;
        }

        // if host ends with a slash, remove it
        if (hostname.EndsWith("/"))
        {
            hostname = hostname.Substring(0, hostname.Length - 1);
        }

        // parse the host, prototol and port
        Uri uri = new Uri(hostname);
        protocol = uri.Scheme;
        port = uri.Port;
        host = uri.Host;

        probe_path = probepath ?? "echo/resource?param1=sample";
        if (probe_path.StartsWith("/"))
        {
            probe_path = probe_path.Substring(1);
        }

        // Uncomment UNTIL sslStream is implemented
        // if (ipaddress != null)
        // {
        //     // Valudate that the address is in the right format
        //     if (!System.Net.IPAddress.TryParse(ipaddress, out _))
        //     {
        //         throw new System.UriFormatException($"Invalid IP address: {ipaddress}");
        //     }
        //     ipaddr = ipaddress;
        // }


        Console.WriteLine($"Adding backend host: {this.host}  probe path: {this.probe_path}");
    }
    public override string ToString()
    {
        return $"{protocol}://{host}:{port}";
    }

    // Method to add a new latency
    public void AddLatency(double latency)
    {
        // If there are already 50 latencies in the queue, remove the oldest one
        if (latencies.Count == MaxData)
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
        // If there are already 50 call results in the queue, remove the oldest one
        if (callSuccess.Count == MaxData)
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