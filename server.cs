using System.Net;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;


public class Server : IServer
{
    private IBackendOptions? _options;
    private readonly TelemetryClient? _telemetryClient; // Add this line
    private HttpListener httpListener;
    private CancellationToken _cancellationToken;
    private BlockingCollection<RequestData> _requestsQueue = new BlockingCollection<RequestData>();

    public Server(IOptions<BackendOptions> backendOptions, TelemetryClient? telemetryClient)
    {
        if (backendOptions == null) throw new ArgumentNullException(nameof(backendOptions));
        if (backendOptions.Value == null) throw new ArgumentNullException(nameof(backendOptions.Value));

        _options = backendOptions.Value;
        _telemetryClient = telemetryClient;

        var _listeningUrl = $"http://+:{_options.Port}/";

        httpListener = new HttpListener();
        httpListener.Prefixes.Add(_listeningUrl);

        var timeoutTime = TimeSpan.FromMilliseconds(_options.Timeout).ToString(@"hh\:mm\:ss\.fff");
        Console.WriteLine($"Server configuration:  Port: {_options.Port} Timeout: {timeoutTime} Workers: {_options.Workers}");
    }

    public BlockingCollection<RequestData> Start(CancellationToken cancellationToken)
    {
        try
        {
            _cancellationToken = cancellationToken;
            httpListener.Start();
            Console.WriteLine($"Listening on {_options?.Port}");
            // Additional setup or async start operations can be performed here

            return _requestsQueue;
        }
        catch (HttpListenerException ex)
        {
            // Handle specific errors, e.g., port already in use
            Console.WriteLine($"Failed to start HttpListener: {ex.Message}");
            // Consider rethrowing, logging the error, or handling it as needed
            throw new Exception("Failed to start the server due to an HttpListener exception.", ex);
        }
        catch (Exception ex)
        {
            // Handle other potential errors
            Console.WriteLine($"An error occurred: {ex.Message}");
            throw new Exception("An error occurred while starting the server.", ex);
        }
    }

    public async Task Run()
    {
        if (_options == null) throw new ArgumentNullException(nameof(_options));

        while (!_cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Use the CancellationToken to asynchronously wait for an HTTP request.
                var getContextTask = httpListener.GetContextAsync();
                var completedTask = await Task.WhenAny(getContextTask, Task.Delay(Timeout.Infinite, _cancellationToken)).ConfigureAwait(false);
                _cancellationToken.ThrowIfCancellationRequested(); // This will throw if the token is cancelled while waiting for a request.

                if (completedTask == getContextTask)
                {
                    _requestsQueue.Add(new RequestData(await getContextTask.ConfigureAwait(false))); // Enqueue the request for processing
                }
            }
            catch (OperationCanceledException)
            {
                // Handle the cancellation request (e.g., break the loop, log the cancellation, etc.)
                Console.WriteLine("Operation was canceled. Stopping the server.");
                break; // Exit the loop
            }
            catch (Exception e)
            {
                _telemetryClient?.TrackException(e);
                Console.WriteLine($"Error: {e.Message}\n{e.StackTrace}");
            }
        }
    }
}