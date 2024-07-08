using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using OS = System;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

public class Program
{
    private static HttpClient hc = new HttpClient();
    Program program = new Program();
    public static TelemetryClient? telemetryClient;


    public static async Task Main(string[] args)
    {
        var backendOptions = LoadBackendOptions();

        var hostBuilder = Host.CreateDefaultBuilder(args).ConfigureServices((hostContext, services) =>
            {        
            // Register the configured BackendOptions instance with DI
                services.Configure<BackendOptions>(options =>
                {
                    options.Timeout = backendOptions.Timeout;
                    options.Port =  backendOptions.Port;
                    options.PollInterval = backendOptions.PollInterval;
                    options.PollTimeout = backendOptions.PollTimeout;
                    options.SuccessRate = backendOptions.SuccessRate;
                    options.Hosts = backendOptions.Hosts;
                    options.Client = backendOptions.Client;
                });

                services.AddLogging(loggingBuilder => loggingBuilder.AddFilter<Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider>("Category", LogLevel.Information));
                var aiConnectionString = OS.Environment.GetEnvironmentVariable("APPINSIGHTS_CONNECTIONSTRING") ?? "";
                if (aiConnectionString != null)
                {
                    services.AddApplicationInsightsTelemetryWorkerService((ApplicationInsightsServiceOptions options) => options.ConnectionString = aiConnectionString);
                    services.AddApplicationInsightsTelemetry(options => 
                    { 
                        options.EnableRequestTrackingTelemetryModule = true; 
                    });
                    if (aiConnectionString != "")
                        Console.WriteLine("AppInsights initialized");
                }

                var eventHubConnectionString = OS.Environment.GetEnvironmentVariable("EVENTHUB_CONNECTIONSTRING") ?? "";
                var eventHubName = OS.Environment.GetEnvironmentVariable("EVENTHUB_NAME") ?? "";

                services.AddSingleton<IEventHubClient>(provider => 
                    {
                        var eventHubClient = new EventHubClient(eventHubConnectionString, eventHubName);
                        eventHubClient.StartTimer(); 
                        return eventHubClient; 
                    });

                //services.AddHttpLogging(o => { });
                services.AddSingleton<IBackendOptions>(backendOptions);
                services.AddSingleton<IBackendService, Backends>();
                services.AddSingleton<IServer, Server>();
                
                // Add other necessary service registrations here
            });
    

        var frameworkHost = hostBuilder.Build();
        var serviceProvider =frameworkHost.Services;

        var backends = serviceProvider.GetRequiredService<IBackendService>();
        //ILogger<Program> logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        try {
            Program.telemetryClient = serviceProvider.GetRequiredService<TelemetryClient>();
            if ( Program.telemetryClient != null)
                Console.SetOut(new AppInsightsTextWriter(Program.telemetryClient, Console.Out));
        } catch (System.InvalidOperationException ) {
        }

        backends.Start();

        var server = serviceProvider.GetRequiredService<IServer>();
        try
        {
            await backends.waitForStartup(20); // wait for up to 20 seconds for startup
            server.Start();
        } catch (Exception)
        {
            Console.WriteLine($"Exiting");
            System.Environment.Exit(1);
        }

        try {        
            await server.Run();
        }
        catch (Exception e)
        {
            telemetryClient?.TrackException(e);
            Console.WriteLine($"Error: {e.Message}");
            Console.WriteLine($"Stack Trace: {e.StackTrace}");
        }

        await frameworkHost.RunAsync();
    }

    private static int ReadEnvironmentVariableOrDefault(string variableName, int defaultValue)
    {
        if (!int.TryParse(OS.Environment.GetEnvironmentVariable(variableName), out var value))
        {
            Console.WriteLine($"Invalid or missing {variableName}. Using default: {defaultValue}");
            return defaultValue;
        }
        return value;
    }

     private static BackendOptions LoadBackendOptions()
    {
        var backendOptions = new BackendOptions
        {
            Port = ReadEnvironmentVariableOrDefault("Port", 443),
            PollInterval = ReadEnvironmentVariableOrDefault("PollInterval", 15000),
            SuccessRate = ReadEnvironmentVariableOrDefault("SuccessRate", 80),
            Timeout = ReadEnvironmentVariableOrDefault("Timeout", 3000),
            PollTimeout = ReadEnvironmentVariableOrDefault("PollTimeout", 3000),
            Client = new HttpClient(), // Assuming hc is HttpClient
            Hosts = new List<BackendHost>()
        };

        backendOptions.Client.Timeout = TimeSpan.FromMilliseconds(backendOptions.Timeout);

        int i = 1;
        StringBuilder sb = new StringBuilder();
        while (true)
        {

            var hostname = Environment.GetEnvironmentVariable($"Host{i}");
            if (hostname == null) break;

            try
            {
                var probePath = Environment.GetEnvironmentVariable($"Probe_path{i}");
                var ip = Environment.GetEnvironmentVariable($"IP{i}");
                var bh = new BackendHost(hostname, probePath, ip);
                backendOptions.Hosts.Add(bh);

                if (ip != null)
                {
                    sb.Append(ip);
                    sb.Append(" ");
                    sb.Append(hostname + Environment.NewLine);
                }
            }
            catch (UriFormatException e)
            {
                Console.WriteLine($"Could not add {hostname}: {e.Message}");
            }

            i++;
        }

        if (Environment.GetEnvironmentVariable("APPENDHOSTSFILE")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true) {
            Console.WriteLine($"Adding {sb.ToString()} to /etc/hosts");
            using (StreamWriter sw = File.AppendText("/etc/hosts"))
            {
                sw.WriteLine(sb.ToString());
            }
        }

        Console.WriteLine ("=======================================================================================");
        Console.WriteLine(" #####                                 #       ####### ");
        Console.WriteLine("#     #  # #    # #####  #      ###### #       #    #  #####  #####   ####  #    # #   #");
        Console.WriteLine("#        # ##  ## #    # #      #      #           #   #    # #    # #    #  #  #   # #");
        Console.WriteLine(" #####   # # ## # #    # #      #####  #          #    #    # #    # #    #   ##     #");
        Console.WriteLine("      #  # #    # #####  #      #      #         #     #####  #####  #    #   ##     #");
        Console.WriteLine("#     #  # #    # #      #      #      #         #     #      #   #  #    #  #  #    #");
        Console.WriteLine(" #####   # #    # #      ###### ###### #######   #     #      #    #  ####  #    #   #");
        Console.WriteLine ("=======================================================================================");
    
        return backendOptions;
    }
}