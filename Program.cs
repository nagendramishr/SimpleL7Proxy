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
                    var bh = new BackendHost(hostname, OS.Environment.GetEnvironmentVariable("Probe_path" + i));
                    hosts.Add( bh );
                } catch (System.UriFormatException e) {
                    Console.WriteLine($"Could not add {hostname} : {e.Message}");
                
                }
            }
        }

        // read the port number
        var port = OS.Environment.GetEnvironmentVariable("Port") ?? "443";
        // read the backend polling interval
        var interval = OS.Environment.GetEnvironmentVariable("PollInterval") ?? "15000";

        var aiConnectionString = OS.Environment.GetEnvironmentVariable("APPINSIGHTS_CONNECTIONSTRING");
        if (aiConnectionString != null)
        {
            // Initialize AppInsights and map Console.WriteLine to AppInsights
            TelemetryConfiguration configuration = TelemetryConfiguration.CreateDefault();
            configuration.ConnectionString = aiConnectionString;
            Program.telemetryClient = new TelemetryClient(configuration);

            Console.WriteLine("AppInsights initialized");
            Console.SetOut(new AppInsightsTextWriter(Program.telemetryClient));
        }

        // startup the backend poller
        var backends = new Backends(hosts, hc, int.Parse(interval));
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

        // Read http timeout from environment variable
        var timeout = OS.Environment.GetEnvironmentVariable("Timeout");
        if (timeout != null)
        {
            //update the timeout for the client
            hc.Timeout = TimeSpan.FromMilliseconds(int.Parse(timeout));
            Console.WriteLine($"Timeout set to {hc.Timeout}");
        }
    }
}