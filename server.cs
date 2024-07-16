using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data.Common; // Add this for TelemetryClient
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;

public class Server : IServer
{
    private IBackendService? _backends;
    private IBackendOptions? _options;
    private IEventHubClient? _eventHubClient;
    private static bool _debug = false;
    private readonly TelemetryClient? _telemetryClient; // Add this line
    private HttpListener httpListener;

    private string _url;

    // Define the set of status codes that you want to allow
    public static HashSet<HttpStatusCode> allowedStatusCodes = new HashSet<HttpStatusCode>
    {
        HttpStatusCode.OK,
        HttpStatusCode.Unauthorized,
        HttpStatusCode.Forbidden,
        HttpStatusCode.NotFound,
    };

    public Server(IOptions<BackendOptions> backendOptions, IBackendService backends, TelemetryClient? telemetryClient, IEventHubClient? eventHubClient)
    {

        if (backendOptions == null) throw new ArgumentNullException(nameof(backendOptions));
        if (backends == null) throw new ArgumentNullException(nameof(backends));
        if (backendOptions.Value == null) throw new ArgumentNullException(nameof(backendOptions.Value));

        _options = backendOptions.Value;
        _backends = backends;
        _telemetryClient = telemetryClient;
        _eventHubClient = eventHubClient;

        _url = $"http://+:{_options.Port}/";

        httpListener = new HttpListener();
        httpListener.Prefixes.Add(_url);

        var timeoutTime = TimeSpan.FromMilliseconds(_options.Timeout).ToString(@"hh\:mm\:ss\.fff");
        Console.WriteLine($"Server configuration:  Port: {_options.Port} Timeout: {timeoutTime}");
    }

    public void Start()
    {
        try
        {
            httpListener.Start();
            Console.WriteLine($"Listening on {_url}");
            // Additional setup or async start operations can be performed here
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

    public async Task Run(CancellationToken cancellationToken)
    {
        if (_options == null) throw new ArgumentNullException(nameof(_options));
        if (_backends == null) throw new ArgumentNullException(nameof(_backends));
        SemaphoreSlim semaphore = new SemaphoreSlim(50); // Limit to 100 concurrent tasks

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerRequest? request = null;
            HttpListenerResponse? response = null;

            try
            {
                // Use the CancellationToken to asynchronously wait for an HTTP request.
                var getContextTask = httpListener.GetContextAsync();
                await Task.WhenAny(getContextTask, Task.Delay(Timeout.Infinite, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested(); // This will throw if the token is cancelled while waiting for a request.
                
                var context = await getContextTask; 
                var currentDate = DateTime.UtcNow;

                try {
                    //var context = httpListener.GetContext();
                    request = context.Request;
                    response = context.Response;

                    if (request?.Url != null)
                    {
                        await semaphore.WaitAsync(cancellationToken); // Wait for an available slot
                        var proxyTask = Task.Run(async () => 
                        {
                            try
                            {                              
                                await ProxyRequestAsync(currentDate, request.HttpMethod, request.Url.PathAndQuery, (WebHeaderCollection)request.Headers, request.InputStream, response);
                            }
                            finally
                            {
                                semaphore.Release(); // Release the slot after the task completes
                            }
                        }, cancellationToken);
                        await proxyTask;
                    }
                    else
                    {
                        response.StatusCode = 400;
                        using (var writer = new StreamWriter(response.OutputStream))
                        {
                            await writer.WriteAsync("Bad Request");
                        }
                    }
                } finally {

                    if (request?.Url != null)
                    {
                        _telemetryClient?.TrackRequest($"{request.HttpMethod} {request.Url.PathAndQuery}", DateTimeOffset.UtcNow, new TimeSpan(0, 0, 0), $"{response?.StatusCode}", true);
                    }
                    context.Response.Close(); // Ensure the response is closed
                    context.Request.InputStream.Dispose(); // Dispose of the request input stream
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
                Console.WriteLine($"Error: {e.StackTrace}");
            }
        }
    }

    public async Task ProxyRequestAsync(DateTime requestDate, string method, string path, WebHeaderCollection headers, Stream body, HttpListenerResponse response)
    {

        if (_backends == null) throw new ArgumentNullException(nameof(_backends));
        if (_options == null) throw new ArgumentNullException(nameof(_options));
        if (_options.Client == null) throw new ArgumentNullException(nameof(_options.Client));

        // Make a local copy of the active hosts
        var activeHosts = _backends.GetActiveHosts().ToList();
        MemoryStream? ms = null;

        try
        {
            // check if rewind the stream is possible, if not, then we'll cache the body for later
            if (!body.CanSeek)
            {
                ms = new MemoryStream();
                await body.CopyToAsync(ms);
                ms.Position = 0;
                body = ms;
            }

            // set ldebug to true if the "S7pDebug" header is set to "true"
            bool _ldebug = headers["S7PDEBUG"] == "true" || _debug;

            HttpStatusCode lastStatusCode = HttpStatusCode.ServiceUnavailable;
            var urlWithPath = "";
            foreach (var host in activeHosts)
            {
                // Try the request on each active host, stop if it worked
                try
                {
                    headers.Set("Host", host.host);
                    urlWithPath = new UriBuilder(host.url) { Path = path }.Uri.AbsoluteUri;
                    urlWithPath = System.Net.WebUtility.UrlDecode(urlWithPath);

                    //var proxyRequest = new HttpRequestMessage(new HttpMethod(method), urlWithPath)
                    using (var bodyContent = new StreamContent(body))
                    {
                        using (var proxyRequest = new HttpRequestMessage(new HttpMethod(method), urlWithPath)
                        {
                            Content = bodyContent
                        })
                        {
                            foreach (var key in headers.AllKeys)
                            {
                                if (_ldebug) Console.WriteLine($" > {key} : {headers[key]}");

                                if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                                    proxyRequest.Content.Headers.TryAddWithoutValidation(key, headers[key]);
                                else
                                    proxyRequest.Headers.TryAddWithoutValidation(key, headers[key]);
                            }
                            proxyRequest.Headers.ConnectionClose = true;

                            // Log request headers if debugging is enabled
                            if (_ldebug) LogRequestHeaders(proxyRequest);

                            // Send the request and get the response
                            using (var proxyResponse = await _options.Client.SendAsync(proxyRequest))
                            {
                                //var proxyResponse = await _options.Client.SendAsync(proxyRequest);
                                var responseDate = DateTime.UtcNow;
                                lastStatusCode = proxyResponse.StatusCode;

                                // Check if the status code of the response is in the set of allowed status codes, else try the next host
                                if (!allowedStatusCodes.Contains(proxyResponse.StatusCode))
                                {
                                    body.Position = 0;
                                    if (_debug)
                                        Console.WriteLine($"Trying next host: Response: {proxyResponse.StatusCode}");
                                    continue;
                                }

                                await ReturnResponseAsync(proxyResponse, response, urlWithPath, requestDate, responseDate, _ldebug);
                                // // Get a stream to the response body
                                // using (var responseBody = await proxyResponse.Content.ReadAsStreamAsync())
                                // {

                                //     if (_ldebug) LogResponseHeaders(proxyResponse);

                                //     // Copy across all the response headers to the client
                                //     CopyResponseHeaders(proxyResponse, response);

                                //     response.StatusCode = (int)proxyResponse.StatusCode;

                                //     using (var output = response.OutputStream)
                                //     {
                                //         await responseBody.CopyToAsync(output);
                                //     }

                                // Console.WriteLine($"{urlWithPath}  {response.Headers["Content-Length"]} {response.StatusCode}");

                                if (_eventHubClient != null)
                                    SendEventData(urlWithPath, response, requestDate, responseDate);

                                return;
    //                            }
                            }
                        }
                    }
                }

                catch (TaskCanceledException)
                {
                    lastStatusCode = HandleError(null, requestDate, urlWithPath, HttpStatusCode.RequestTimeout, "Request to " + host.url + " timed out");
                    continue;
                }
                catch (OperationCanceledException)
                {
                    lastStatusCode = HandleError(null, requestDate, urlWithPath, HttpStatusCode.BadGateway, "Request to " + host.url + " was cancelled");
                    continue;
                }
                catch (HttpRequestException e)
                {
                    lastStatusCode = HandleError(e, requestDate, urlWithPath, HttpStatusCode.BadRequest);
                    continue;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error: {e.StackTrace}");
                    Console.WriteLine($"Error: {e.Message}");
                    lastStatusCode = HandleError(e, requestDate, urlWithPath, HttpStatusCode.InternalServerError);
                }
                finally
                {
                    // rewind the stream
                    body.Position = 0;
                }
            }

            // If we get here, then no hosts were able to handle the request
            Console.WriteLine($"{path}  - {lastStatusCode}");
            response.StatusCode = (int)lastStatusCode;
            using (var writer = new StreamWriter(response.OutputStream))
            {
                await writer.WriteAsync("No active hosts were able to handle the request.");
            }
            response.Close();

        }
        finally
        {
            ms?.Dispose();
            body?.Dispose();
            activeHosts.Clear();
        }
    }

   

    private async Task ReturnResponseAsync(HttpResponseMessage proxyResponse, HttpListenerResponse response, string urlWithPath, DateTime requestDate, DateTime responseDate, bool debug)
    {
        // Get a stream to the response body
        using (var responseBody = await proxyResponse.Content.ReadAsStreamAsync())
        {
            if (debug) LogResponseHeaders(proxyResponse);

            // Copy across all the response headers to the client
            CopyResponseHeaders(proxyResponse, response);

            response.StatusCode = (int)proxyResponse.StatusCode;

            using (var output = response.OutputStream)
            {
                await responseBody.CopyToAsync(output);
            }
        }
    }
    private void CopyResponseHeaders(HttpResponseMessage proxyResponse, HttpListenerResponse response)
    {
        foreach (var header in proxyResponse.Headers)
        {
            response.Headers.Add(header.Key, string.Join(", ", header.Value));
        }
    }

    private void SendEventData(string urlWithPath, HttpListenerResponse response, DateTime requestDate, DateTime responseDate)
    {
        string date = responseDate.ToString("o");
        var delta = (responseDate - requestDate).ToString(@"ss\:fff");
        _eventHubClient?.SendData($"{{\"Date\":\"{date}\", \"Url\":\"{urlWithPath}\", \"Status\":\"{response.StatusCode}\", \"Latency\":\"{delta}\"}}");
    }

    private void LogRequestHeaders(HttpRequestMessage proxyRequest)
    {
        foreach (var header in proxyRequest.Headers)
            Console.WriteLine($"  | {header.Key} : {string.Join(", ", header.Value)}");

        if (proxyRequest.Content != null)
        {
            foreach (var header in proxyRequest.Content.Headers)
                Console.WriteLine($"  | {header.Key} : {string.Join(", ", header.Value)}");
        }
    }

    private void LogResponseHeaders(HttpResponseMessage proxyResponse)
    {
        foreach (var header in proxyResponse.Headers)
        {
            Console.WriteLine($"< {header.Key} : {string.Join(", ", header.Value)}");
        }
        foreach (var header in proxyResponse.Content.Headers)
        {
            Console.WriteLine($"< {header.Key} : {string.Join(", ", header.Value)}");
        }
    }

    private HttpStatusCode HandleError(Exception? e, DateTime requestDate, string url, HttpStatusCode statusCode, string? customMessage = null)
    {
        // Common operations for all exceptions
        if (e != null)
            _telemetryClient?.TrackException(e);

        if (!string.IsNullOrEmpty(customMessage))
        {
            Console.WriteLine($"{e?.Message ?? customMessage}");
        }
        var date = requestDate.ToString("o");
        _eventHubClient?.SendData($"{{\"Date\":\"{date}\", \"Url\":\"{url}\", \"Error\":\"{e?.Message ?? customMessage}\"}}");
        return statusCode;
    }
}