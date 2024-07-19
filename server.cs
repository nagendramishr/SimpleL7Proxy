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
using Microsoft.ApplicationInsights.DataContracts;
using System.IO;
using System.Text;
using System.Net.Http.Headers;

public class proxyData
{
    public HttpStatusCode StatusCode { get; set; }
    public WebHeaderCollection Headers { get; set; }
    public WebHeaderCollection ContentHeaders { get; set; }
    public byte[]? Body { get; set; }

    public string FullURL { get; set; }

    public DateTime ResponseDate { get; set; }

    public proxyData()
    {
        Headers = new WebHeaderCollection();
        ContentHeaders = new WebHeaderCollection();
    }
}

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
        HttpStatusCode.LengthRequired,
        HttpStatusCode.BadRequest,
        HttpStatusCode.Unauthorized,
        HttpStatusCode.PaymentRequired,
        HttpStatusCode.Forbidden,
        HttpStatusCode.NotFound,
        HttpStatusCode.MethodNotAllowed,
        HttpStatusCode.NotAcceptable,
        HttpStatusCode.ProxyAuthenticationRequired,
        HttpStatusCode.RequestTimeout ,
        HttpStatusCode.Conflict ,
        HttpStatusCode.Gone ,
        HttpStatusCode.LengthRequired,
        HttpStatusCode.PreconditionFailed,
        HttpStatusCode.RequestEntityTooLarge,
        HttpStatusCode.RequestUriTooLong ,
        HttpStatusCode.UnsupportedMediaType,
        HttpStatusCode.RequestedRangeNotSatisfiable,
        HttpStatusCode.ExpectationFailed ,
        HttpStatusCode.MisdirectedRequest,
        HttpStatusCode.UnprocessableEntity,
        HttpStatusCode.UnprocessableContent,
        HttpStatusCode.Locked,
        HttpStatusCode.FailedDependency,
        HttpStatusCode.UpgradeRequired ,
        HttpStatusCode.PreconditionRequired,
        HttpStatusCode.TooManyRequests ,
        HttpStatusCode.RequestHeaderFieldsTooLarge,
        HttpStatusCode.UnavailableForLegalReasons
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
        var tasks = new List<Task>();

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

                try
                {
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
                                var pr = await ReadProxyAsync(currentDate, request.HttpMethod, request.Url.PathAndQuery,
                                                                (WebHeaderCollection)request.Headers, request.InputStream);

                                await WriteResponseAsync(context, pr);

                                Console.WriteLine($"URL: {pr.FullURL} Length: {pr.ContentHeaders["Content-Length"]} Status: {(int)pr.StatusCode}");

                                if (_eventHubClient != null)
                                    SendEventData(pr.FullURL, pr.StatusCode, currentDate, pr.ResponseDate);
                            }
                            catch (Exception ex)
                            {
                                // Log the exception
                                Console.WriteLine($"Exception: {ex.Message}");
                                Console.WriteLine($"Stack Trace: {ex.StackTrace}");

                                // Set an appropriate status code for the error
                                context.Response.StatusCode = 500;
                                var errorMessage = Encoding.UTF8.GetBytes("Internal Server Error");
                                await context.Response.OutputStream.WriteAsync(errorMessage, 0, errorMessage.Length);
                            }
                            finally
                            {
                                context.Response.Close(); // Ensure the response is closed
                                context.Request.InputStream.Dispose(); // Dispose of the request input stream
                                semaphore.Release(); // Release the slot after the task completes
                            }
                        }, cancellationToken);

                        tasks.Add(proxyTask);
                    }
                    else
                    {
                        Console.WriteLine("Bad Request due to URL being null");
                        response.StatusCode = 400;
                        await using (var writer = new StreamWriter(response.OutputStream))
                        {
                            await writer.WriteAsync("Bad Request");
                        }
                    }
                }
                finally
                {

                    if (request?.Url != null)
                    {
                        _telemetryClient?.TrackRequest($"{request.HttpMethod} {request.Url.PathAndQuery}", DateTimeOffset.UtcNow, new TimeSpan(0, 0, 0), $"{response?.StatusCode}", true);
                    }
                }

                // Remove completed tasks from the list
                tasks = tasks.Where(t => !t.IsCompleted).ToList();
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

        await Task.WhenAll(tasks); // Wait for all tasks to complete
    }


    private async Task WriteResponseAsync(HttpListenerContext context, proxyData pr)
    {
        // Set the response status code
        context.Response.StatusCode = (int)pr.StatusCode;

        // Copy headers to the response
        CopyHeaders(pr.Headers, context.Response.Headers);

        // Set content-specific headers
        if (pr.ContentHeaders != null)
        {
            foreach (var key in pr.ContentHeaders.AllKeys)
            {
                switch (key.ToLowerInvariant())
                {
                    case "content-length":
                        var length = pr.ContentHeaders[key];
                        if (long.TryParse(length, out var contentLength))
                        {
                            context.Response.ContentLength64 = contentLength;
                        }
                        else
                        {
                            Console.WriteLine($"Invalid Content-Length: {length}");
                        }
                        break;

                    case "content-type":
                        context.Response.ContentType = pr.ContentHeaders[key];
                        break;

                    default:
                        context.Response.Headers[key] = pr.ContentHeaders[key];
                        break;
                }
            }
        }
        context.Response.KeepAlive = false;

        // Write the response body to the client as a byte array
        if (pr.Body != null)
        {
            await using (var memoryStream = new MemoryStream(pr.Body))
            {
                await memoryStream.CopyToAsync(context.Response.OutputStream);
                await context.Response.OutputStream.FlushAsync();
            }
        }
    }

    public async Task<proxyData> ReadProxyAsync(DateTime requestDate, string method, string path, WebHeaderCollection headers, Stream body)//HttpListenerResponse downStreamResponse)
    {

        if (_backends == null) throw new ArgumentNullException(nameof(_backends));
        if (_options == null) throw new ArgumentNullException(nameof(_options));
        if (_options.Client == null) throw new ArgumentNullException(nameof(_options.Client));

        // Make a local copy of the active hosts
        var activeHosts = _backends.GetActiveHosts().ToList();

        try
        {
            // set ldebug to true if the "S7pDebug" header is set to "true"
            bool _ldebug = headers["S7PDEBUG"] == "true" || _debug;
            HttpStatusCode lastStatusCode = HttpStatusCode.ServiceUnavailable;

            // Read the body stream once and reuse it
            byte[] bodyBytes;
            using (MemoryStream ms = new MemoryStream())
            {
                await body.CopyToAsync(ms);
                bodyBytes = ms.ToArray();
            }
        
            var urlWithPath = "";

            foreach (var host in activeHosts)
            {
                // Try the request on each active host, stop if it worked
                try
                {
                    headers.Set("Host", host.host);
                    urlWithPath = new UriBuilder(host.url) { Path = path }.Uri.AbsoluteUri;
                    urlWithPath = System.Net.WebUtility.UrlDecode(urlWithPath);

                    using (var bodyContent = new ByteArrayContent(bodyBytes))
                    using (var proxyRequest = new HttpRequestMessage(new HttpMethod(method), urlWithPath))
                    {
                        proxyRequest.Content = bodyContent;
                        AddHeadersToRequest(proxyRequest, headers);

                        proxyRequest.Headers.ConnectionClose = true;

                        // Log request headers if debugging is enabled
                        if (_ldebug)
                        {
                            LogHeaders(proxyRequest.Headers, ">");
                            LogHeaders(proxyRequest.Content.Headers, "  >");
                        }

                        // Send the request and get the response
                        var ProxtStartDate = DateTime.UtcNow;

                        using (var proxyResponse = await _options.Client.SendAsync(proxyRequest))
                        {
                            var responseDate = DateTime.UtcNow;
                            lastStatusCode = proxyResponse.StatusCode;

                            // Check if the status code of the response is in the set of allowed status codes, else try the next host
                            if (!allowedStatusCodes.Contains(proxyResponse.StatusCode))
                            {
                                if (_debug)
                                    Console.WriteLine($"Trying next host: Response: {proxyResponse.StatusCode}");
                                continue;
                            }

                            // read the response
                            var proxyResponseData = await ReturnResponseAsync(proxyResponse, urlWithPath, requestDate, responseDate, _ldebug);
                            host.AddPxLatency((responseDate - ProxtStartDate).TotalMilliseconds);

                            proxyResponseData.FullURL = urlWithPath;
                            proxyResponseData.ResponseDate = responseDate;

                            return proxyResponseData;
                        }
                    }
                }

                catch (TaskCanceledException)
                {
                    lastStatusCode = HandleError(host, null, requestDate, urlWithPath, HttpStatusCode.RequestTimeout, "Request to " + host.url + " timed out");
                    continue;
                }
                catch (OperationCanceledException e)
                {
                    Console.WriteLine("bllla");
                    lastStatusCode = HandleError(host, e, requestDate, urlWithPath, HttpStatusCode.BadGateway, "Request to " + host.url + " was cancelled");
                    continue;
                }
                catch (HttpRequestException e)
                {
                    lastStatusCode = HandleError(host, e, requestDate, urlWithPath, HttpStatusCode.BadRequest);
                    continue;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error: {e.StackTrace}");
                    Console.WriteLine($"Error: {e.Message}");
                    lastStatusCode = HandleError(host, e, requestDate, urlWithPath, HttpStatusCode.InternalServerError);
                }
            }

            var pr = new proxyData
            {
                StatusCode = (HttpStatusCode)lastStatusCode,
                Body = Encoding.UTF8.GetBytes("No active hosts were able to handle the request.")
            };

            // If we get here, then no hosts were able to handle the request
            Console.WriteLine($"{path}  - {lastStatusCode}");

            return pr;
        }
        finally
        {
            activeHosts.Clear();
        }
    }

    private void AddHeadersToRequest(HttpRequestMessage? proxyRequest, NameValueCollection headers)
    {
        foreach (string? key in headers.AllKeys)
        {
            if (key == null) continue;
            if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                if (!(key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
                {
                    proxyRequest?.Content?.Headers.TryAddWithoutValidation(key, headers[key]);
                }
            }
            else if (!key.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
            {
                proxyRequest?.Headers.TryAddWithoutValidation(key, headers[key]);
            }
        }
    }
    private async Task<proxyData> ReturnResponseAsync(HttpResponseMessage proxyResponse, string urlWithPath, DateTime requestDate, DateTime responseDate, bool debug)
    {

        var pr = new proxyData
        {
            StatusCode = proxyResponse.StatusCode
        };

        // Get a stream to the response body
        await using (var responseBody = await proxyResponse.Content.ReadAsStreamAsync())
        {
            if (debug)
            {
                LogHeaders(proxyResponse.Headers, "<");
                LogHeaders(proxyResponse.Content.Headers, "  <");
            }
            // Copy across all the response headers to the client
            CopyHeaders(proxyResponse, pr.Headers, pr.ContentHeaders);

            // Determine the encoding from the Content-Type header
            MediaTypeHeaderValue? contentType = proxyResponse.Content.Headers.ContentType;
            var encoding = GetEncodingFromContentType(contentType, debug, requestDate, urlWithPath);

            using (var reader = new StreamReader(responseBody, encoding))
            {
                pr.Body = encoding.GetBytes(await reader.ReadToEndAsync());
            }
        }

        return pr;
    }

    private Encoding GetEncodingFromContentType(MediaTypeHeaderValue? contentType, bool debug, DateTime requestDate, string urlWithPath)
    {
        if (contentType == null || string.IsNullOrEmpty(contentType.CharSet))
        {
            if (debug)
            {
                Console.WriteLine("No charset specified, using default UTF-8");
            }
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(contentType.CharSet);
        }
        catch (ArgumentException)
        {
            HandleError(null, null, requestDate, urlWithPath, HttpStatusCode.UnsupportedMediaType,
                $"Unsupported charset: {contentType.CharSet}");
            return Encoding.UTF8; // Fallback to UTF-8 in case of error
        }
    }

    private void CopyHeaders(WebHeaderCollection sourceHeaders, WebHeaderCollection targetHeaders)
    {
        foreach (var key in sourceHeaders.AllKeys)
        {
            targetHeaders.Add(key, sourceHeaders[key]);
        }
    }
    private void CopyHeaders(HttpResponseMessage message, WebHeaderCollection Headers, WebHeaderCollection ContentHeaders)
    {
        foreach (var header in message.Headers)
        {
            Headers.Add(header.Key, string.Join(", ", header.Value));
        }
        foreach (var header in message.Content.Headers)
        {
            ContentHeaders.Add(header.Key, string.Join(", ", header.Value));
        }
    }

    private void SendEventData(string urlWithPath, HttpStatusCode statusCode, DateTime requestDate, DateTime responseDate)
    {
        string date = responseDate.ToString("o");
        var delta = (responseDate - requestDate).ToString(@"ss\:fff");
        _eventHubClient?.SendData($"{{\"Date\":\"{date}\", \"Url\":\"{urlWithPath}\", \"Status\":\"{statusCode}\", \"Latency\":\"{delta}\"}}");
    }

    private void LogHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, string prefix)
    {
        foreach (var header in headers)
        {
            Console.WriteLine($"{prefix} {header.Key} : {string.Join(", ", header.Value)}");
        }
    }

    private HttpStatusCode HandleError(BackendHost host, Exception? e, DateTime requestDate, string url, HttpStatusCode statusCode, string? customMessage = null)
    {
        // Common operations for all exceptions

        if (_telemetryClient != null)
        {
            if (e != null)
                _telemetryClient.TrackException(e);

            var telemetry = new EventTelemetry("ProxyRequest");
            telemetry.Properties.Add("URL", url);
            telemetry.Properties.Add("RequestDate", requestDate.ToString("o"));
            telemetry.Properties.Add("ResponseDate", DateTime.Now.ToString("o"));
            telemetry.Properties.Add("StatusCode", statusCode.ToString());
            _telemetryClient.TrackEvent(telemetry);
        }


        if (!string.IsNullOrEmpty(customMessage))
        {
            Console.WriteLine($"{e?.Message ?? customMessage}");
        }
        var date = requestDate.ToString("o");
        _eventHubClient?.SendData($"{{\"Date\":\"{date}\", \"Url\":\"{url}\", \"Error\":\"{e?.Message ?? customMessage}\"}}");

        host.AddError();
        return statusCode;
    }
}