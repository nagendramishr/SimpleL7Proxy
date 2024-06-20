using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using OS = System;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;

public class Program
{
    private static List<BackendHost> hosts = new List<BackendHost>();
    private static HttpClient hc = new HttpClient();
    Program program = new Program();
    public static TelemetryClient? telemetryClient; 

    public static void Main(string[] args)
    {
        // read Environment Variables: host1, host2, host3, ...
        for (int i = 1; i < 10; i++)
        {
            var hostname = OS.Environment.GetEnvironmentVariable("Host" + i);
            if (hostname != null)
            {
                try {
                    // IP is currently not supported, it will be ignored.
                    var bh = new BackendHost(hostname, OS.Environment.GetEnvironmentVariable("Probe_path" + i), OS.Environment.GetEnvironmentVariable("IP" + i));
                    
                    hosts.Add( bh );
                } catch (System.UriFormatException e) {
                    Console.WriteLine($"Could not add {hostname} : {e.Message}");
                
                }
            }
        }

        // read the port number
        if (!int.TryParse(OS.Environment.GetEnvironmentVariable("Port"), out var port))
        {
            port = 443; // Default port
            Console.WriteLine($"Invalid or missing Port. Using default: {port}");
        }

        // read the backend polling interval
        if (!int.TryParse(OS.Environment.GetEnvironmentVariable("PollInterval"), out var interval))
        {
            interval = 15000; // Default interval
            Console.WriteLine($"Invalid or missing PollInterval. Using default: {interval}");
        }

        // read the success rate as an integer or default to 80 in case of error
        if (!int.TryParse(OS.Environment.GetEnvironmentVariable("SuccessRate"), out var successRate))
        {
            successRate = 80; // Default success rate
            Console.WriteLine($"Invalid or missing SuccessRate. Using default: {successRate}");
        }

        // Read http timeout for the HTTP client 
        if (!int.TryParse(OS.Environment.GetEnvironmentVariable("Timeout"), out var timeout))
        {
            timeout=3000;
            hc.Timeout = TimeSpan.FromMilliseconds(timeout);
            Console.WriteLine($"Invalid or missing SuccessRate. Using default: {hc.Timeout}");
        }

        var aiConnectionString = OS.Environment.GetEnvironmentVariable("APPINSIGHTS_CONNECTIONSTRING");
        if (aiConnectionString != null)
        {
            // Initialize AppInsights and map Console.WriteLine to AppInsights
            TelemetryConfiguration configuration = TelemetryConfiguration.CreateDefault();
            configuration.ConnectionString = aiConnectionString;
            Program.telemetryClient = new TelemetryClient(configuration);

            Console.WriteLine("AppInsights initialized");
            Console.SetOut(new AppInsightsTextWriter(Program.telemetryClient, Console.Out));
        }

        Console.WriteLine($"Starting SimpleL7Proxy: Port: {port}, PollInterval: {interval}, SuccessRate: {successRate} Timeout: {hc.Timeout}"); 

        // startup the backend poller
        var backends = new Backends(hosts, hc, interval, successRate);
        backends.Start();

        var server = new Server(port, backends, hc);
        try
        {
            server.Run().Wait();
        }
        catch (Exception e)
        {
            telemetryClient?.TrackException(e);
            Console.WriteLine($"Error: {e.Message}");
            Console.WriteLine($"Stack Trace: {e.StackTrace}");
        }

    }
}