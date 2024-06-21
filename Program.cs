using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using OS = System;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
                    options.SuccessRate = backendOptions.SuccessRate;
                    options.Timeout = backendOptions.Timeout;
                    options.Hosts = backendOptions.Hosts;
                    options.Client = backendOptions.Client;
                });

                services.AddSingleton<IBackendOptions>(backendOptions);
                services.AddSingleton<IBackendService, Backends>();
                services.AddSingleton<IServer, Server>();
                // Add other necessary service registrations here
            });
    
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

        var frameworkHost = hostBuilder.Build();
        var backends = frameworkHost.Services.GetRequiredService<IBackendService>();
        backends.Start();

        var server = frameworkHost.Services.GetRequiredService<IServer>();
        try
        {
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
            Client = new HttpClient(), // Assuming hc is HttpClient
            Hosts = new List<BackendHost>()
        };

        int i = 1;
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
            }
            catch (UriFormatException e)
            {
                Console.WriteLine($"Could not add {hostname}: {e.Message}");
            }

            i++;
        }

        Console.WriteLine($"Starting SimpleL7Proxy: Port: {backendOptions.Port}, PollInterval: {backendOptions.PollInterval}, SuccessRate: {backendOptions.SuccessRate} Timeout: {backendOptions.Client.Timeout}");

        return backendOptions;
    }
}