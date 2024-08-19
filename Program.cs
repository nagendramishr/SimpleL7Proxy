
using System.Net;
using System.Text;
using OS = System;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Core;


public class Program
{
    private static HttpClient hc = new HttpClient();
    Program program = new Program();
    public static TelemetryClient? telemetryClient;

    static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
    public string OAuthAudience { get; set; } ="";  


    public static async Task Main(string[] args)
    {
        var cancellationToken = cancellationTokenSource.Token;
        var backendOptions = LoadBackendOptions();

        // Set up logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.AddFilter("Azure.Identity", LogLevel.Debug);
        });

        var logger = loggerFactory.CreateLogger<Program>();

        Console.CancelKeyPress += (sender, e) =>
            {
                Console.WriteLine("Shutdown signal received. Initiating shutdown...");
                e.Cancel = true; // Prevent the process from terminating immediately.
                cancellationTokenSource.Cancel(); // Signal the application to shut down.
            };

        var hostBuilder = Host.CreateDefaultBuilder(args).ConfigureServices((hostContext, services) =>
            {        
                // Register the configured BackendOptions instance with DI
                services.Configure<BackendOptions>(options =>
                {
                    options.Timeout = backendOptions.Timeout;
                    options.Port = backendOptions.Port;
                    options.PollInterval = backendOptions.PollInterval;
                    options.PollTimeout = backendOptions.PollTimeout;
                    options.SuccessRate = backendOptions.SuccessRate;
                    options.Hosts = backendOptions.Hosts;
                    options.Client = backendOptions.Client;
                    options.Workers = backendOptions.Workers;
                    options.OAuthAudience = backendOptions.OAuthAudience;
                    options.UseOAuth = backendOptions.UseOAuth;
                    options.PriorityKey1 = backendOptions.PriorityKey1;
                    options.PriorityKey2 = backendOptions.PriorityKey2;
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
                var eventHubClient = new EventHubClient(eventHubConnectionString, eventHubName);
                eventHubClient.StartTimer();

                services.AddSingleton<IEventHubClient>(provider => eventHubClient);
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

        backends.Start(cancellationToken);

        var server = serviceProvider.GetRequiredService<IServer>();
        var eventHubClient = serviceProvider.GetRequiredService<IEventHubClient>();
        var tasks = new List<Task>();
        try
        {
            await backends.waitForStartup(20); // wait for up to 20 seconds for startup
            var queue = server.Start(cancellationToken);

            // startup Worker # of tasks
            for (int i = 0; i < backendOptions.Workers; i++)
            {
                var pw = new ProxyWorker(cancellationToken, queue, backendOptions, backends, eventHubClient, telemetryClient);
                tasks.Add( Task.Run(() => pw.TaskRunner(), cancellationToken));
            }

        } catch (Exception e)
        {
            Console.WriteLine($"Exiting: {e.Message}"); ;
            System.Environment.Exit(1);
        }

        try {        
            await server.Run();
            await Task.WhenAll(tasks); // Wait for all tasks to complete
        }
        catch (Exception e)
        {
            telemetryClient?.TrackException(e);
            Console.WriteLine($"Error: {e.Message}");
            Console.WriteLine($"Stack Trace: {e.StackTrace}");
        }

        try
        {
            // Pass the CancellationToken to RunAsync
            await frameworkHost.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Operation was canceled.");
        }
        catch (Exception e)
        {
            // Handle other exceptions that might occur
            Console.WriteLine($"An unexpected error occurred: {e.Message}");
        }
    }

    private static int ReadEnvironmentVariableOrDefault(string variableName, int defaultValue)
    {
        if (!int.TryParse(OS.Environment.GetEnvironmentVariable(variableName), out var value))
        {
            Console.WriteLine($"Using default:   {variableName}: {defaultValue}");
            return defaultValue;
        }
        return value;
    }
    private static string ReadEnvironmentVariableOrDefault(string variableName, string defaultValue)
    {
        var envValue = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(envValue))
        {
            Console.WriteLine($"Using default: {variableName}: {defaultValue}");
            return defaultValue;
        }
        return envValue.Trim();
    }

     private static BackendOptions LoadBackendOptions()
    {
        var DNSTimeout= ReadEnvironmentVariableOrDefault("DnsRefreshTimeout", 120000);
        ServicePointManager.DnsRefreshTimeout = DNSTimeout;

        HttpClient _client = new HttpClient();
        if (Environment.GetEnvironmentVariable("IgnoreSSLCert")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true) {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
            _client = new HttpClient(handler);
        }


        var backendOptions = new BackendOptions
        {
            Port = ReadEnvironmentVariableOrDefault("Port", 443),
            PollInterval = ReadEnvironmentVariableOrDefault("PollInterval", 15000),
            SuccessRate = ReadEnvironmentVariableOrDefault("SuccessRate", 80),
            Timeout = ReadEnvironmentVariableOrDefault("Timeout", 3000),
            PollTimeout = ReadEnvironmentVariableOrDefault("PollTimeout", 3000),
            Workers = ReadEnvironmentVariableOrDefault("Workers", 10),
            OAuthAudience = ReadEnvironmentVariableOrDefault("OAuthAudience", ""),
            UseOAuth = Environment.GetEnvironmentVariable("UseOAuth")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true,
            PriorityKey1 = ReadEnvironmentVariableOrDefault("PriorityKey1", "12345"),
            PriorityKey2 = ReadEnvironmentVariableOrDefault("PriorityKey2", "67890"),
            Client = _client, 
            Hosts = new List<BackendHost>()
        };

        backendOptions.Client.Timeout = TimeSpan.FromMilliseconds(backendOptions.Timeout);

        int i = 1;
        StringBuilder sb = new StringBuilder();
        while (true)
        {

            var hostname = Environment.GetEnvironmentVariable($"Host{i}")?.Trim();
            if (hostname == null) break;

            try
            {
                var probePath = Environment.GetEnvironmentVariable($"Probe_path{i}")?.Trim();
                var ip = Environment.GetEnvironmentVariable($"IP{i}")?.Trim();
                var bh = new BackendHost(hostname, probePath, ip);
                backendOptions.Hosts.Add(bh);

                if (ip != null)
                {
                    sb.Append(ip);
                    sb.Append(" ");
                    sb.Append(bh.host + Environment.NewLine);
                }
            }
            catch (UriFormatException e)
            {
                Console.WriteLine($"Could not add {hostname}: {e.Message}");
            }

            i++;
        }

        if (Environment.GetEnvironmentVariable("APPENDHOSTSFILE")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true ||
            Environment.GetEnvironmentVariable("AppendHostsFile")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true ) {
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