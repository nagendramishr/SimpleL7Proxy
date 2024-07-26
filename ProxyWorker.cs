
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;

public class ProxyWorker  {

    private static bool _debug = false;
    private  CancellationToken _cancellationToken;
    private  BlockingCollection<RequestData>? _requestsQueue; 
    private readonly TelemetryClient? _telemetryClient;
    private readonly IBackendService _backends;
    private readonly IEventHubClient? _eventHubClient;
    private readonly BackendOptions _options;


    public ProxyWorker( CancellationToken cancellationToken, BlockingCollection<RequestData> requestsQueue, BackendOptions backendOptions, IBackendService? backends, IEventHubClient? eventHubClient, TelemetryClient? telemetryClient) {
        _cancellationToken = cancellationToken;
        _requestsQueue = requestsQueue ?? throw new ArgumentNullException(nameof(requestsQueue));
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _eventHubClient = eventHubClient;
        _telemetryClient = telemetryClient;
        _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
    }

    public async Task TaskRunner() {
        if (_requestsQueue == null) throw new ArgumentNullException(nameof(_requestsQueue));

        while (!_cancellationToken.IsCancellationRequested)
        {
            RequestData incomingRequest;
            try
            {
                incomingRequest = _requestsQueue.Take(_cancellationToken); // This will block until an item is available or the token is cancelled
            }
            catch (OperationCanceledException)
            {
                break; // Exit the loop if the operation is cancelled
            }

            await using (incomingRequest ) {

                var lcontext = incomingRequest?.Context;

                if (lcontext == null || incomingRequest == null) {
                    continue;
                }

                try
                {   
                    if (incomingRequest.Path == "/health")
                    {
                        lcontext.Response.StatusCode = 200;
                        lcontext.Response.ContentType = "text/plain";
                        lcontext.Response.Headers.Add("Cache-Control", "no-cache");
                        lcontext.Response.KeepAlive = false;

                        Byte[]? healthMessage = Encoding.UTF8.GetBytes(_backends?.HostStatus() ?? "OK");
                        lcontext.Response.ContentLength64 = healthMessage.Length;

                        await lcontext.Response.OutputStream.WriteAsync(healthMessage, 0, healthMessage.Length);
                        continue;
                    }

                    var pr = await ReadProxyAsync(incomingRequest).ConfigureAwait(false);
                    await WriteResponseAsync(lcontext, pr).ConfigureAwait(false);

                    Console.WriteLine($"URL: {pr.FullURL} Length: {pr.ContentHeaders["Content-Length"]} Status: {(int)pr.StatusCode}");

                    if (_eventHubClient != null)
                        SendEventData(pr.FullURL, pr.StatusCode, incomingRequest.Timestamp, pr.ResponseDate);

                    pr.Body=[];
                    pr.ContentHeaders.Clear();
                    pr.FullURL="";
                }
                catch (Exception ex)
                {
                    // Log the exception
                    Console.WriteLine($"Exception: {ex.Message}");
                    Console.WriteLine($"Stack Trace: {ex.StackTrace}");

                    // Set an appropriate status code for the error
                    lcontext.Response.StatusCode = 500;
                    var errorMessage = Encoding.UTF8.GetBytes("Internal Server Error");
                    await lcontext.Response.OutputStream.WriteAsync(errorMessage, 0, errorMessage.Length);
                }
                finally
                {
                    _telemetryClient?.TrackRequest($"{incomingRequest.Method} {incomingRequest.Path}", 
                    DateTimeOffset.UtcNow, new TimeSpan(0, 0, 0), $"{lcontext.Response.StatusCode}", true);
                }
            }
        }
    }

    private async Task WriteResponseAsync(HttpListenerContext context, ProxyData pr)
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

public async Task<ProxyData> ReadProxyAsync(RequestData request) //DateTime requestDate, string method, string path, WebHeaderCollection headers, Stream body)//HttpListenerResponse downStreamResponse)
    {

        if (_backends == null) throw new ArgumentNullException(nameof(_backends));
        if (_options == null) throw new ArgumentNullException(nameof(_options));
        if (_options.Client == null) throw new ArgumentNullException(nameof(_options.Client));
        if (request == null || request.Body == null || request.Headers == null || request.Method == null) throw new ArgumentNullException(nameof(request));

        // Make a local copy of the active hosts
        var activeHosts = _backends.GetActiveHosts();
        var method = request.Method;
        var path = request.Path;
        var headers = request.Headers;
        var body = request.Body;

        request.Debug = headers["S7PDEBUG"] == "true" || _debug;
        HttpStatusCode lastStatusCode = HttpStatusCode.ServiceUnavailable;

        // Read the body stream once and reuse it
        byte[] bodyBytes;
        using (MemoryStream ms = new MemoryStream())
        {
            await body.CopyToAsync(ms);
            bodyBytes = ms.ToArray();
        }
        
        foreach (var host in activeHosts)
        {
            // Try the request on each active host, stop if it worked
            try
            {
                headers.Set("Host", host.host);
                var urlWithPath = new UriBuilder(host.url) { Path = path }.Uri.AbsoluteUri;
                request.FullURL = System.Net.WebUtility.UrlDecode(urlWithPath);

                using (var bodyContent = new ByteArrayContent(bodyBytes))
                using (var proxyRequest = new HttpRequestMessage(new HttpMethod(method), request.FullURL))
                {
                    proxyRequest.Content = bodyContent;
                    AddHeadersToRequest(proxyRequest, headers);
                    if (bodyBytes.Length > 0)
                    {
                        proxyRequest.Content.Headers.ContentLength = bodyBytes.Length;
                    }
                    
                    proxyRequest.Headers.ConnectionClose = true;

                    // Log request headers if debugging is enabled
                    if (request.Debug)
                    {
                        LogHeaders(proxyRequest.Headers, ">");
                        LogHeaders(proxyRequest.Content.Headers, "  >");
                    }

                    // Send the request and get the response
                    var ProxyStartDate = DateTime.UtcNow;
                    using (var proxyResponse = await _options.Client.SendAsync(proxyRequest))
                    {
                        var responseDate = DateTime.UtcNow;
                        lastStatusCode = proxyResponse.StatusCode;

                        // Check if the status code of the response is in the set of allowed status codes, else try the next host
                        if ((int)proxyResponse.StatusCode < 400 || (int)proxyResponse.StatusCode >= 500)
                        {
                            if (request.Debug)
                                Console.WriteLine($"Trying next host: Response: {proxyResponse.StatusCode}");
                            continue;
                        }

                        host.AddPxLatency((responseDate - ProxyStartDate).TotalMilliseconds);

                        // Read the response
                        var pr = new ProxyData()
                        {
                            ResponseDate = responseDate,
                            StatusCode = proxyResponse.StatusCode,
                            FullURL = request.FullURL,
                        };
                        bodyBytes = [];
                        await GetProxyResponseAsync(proxyResponse, request, pr);
                        return pr;
                    }
                }
            }

            catch (TaskCanceledException)
            {
                lastStatusCode = HandleError(host, null, request.Timestamp, request.FullURL, HttpStatusCode.RequestTimeout, "Request to " + host.url + " timed out");
                continue;
            }
            catch (OperationCanceledException e)
            {
                lastStatusCode = HandleError(host, e, request.Timestamp, request.FullURL, HttpStatusCode.BadGateway, "Request to " + host.url + " was cancelled");
                continue;
            }
            catch (HttpRequestException e)
            {
                lastStatusCode = HandleError(host, e, request.Timestamp, request.FullURL, HttpStatusCode.BadRequest);
                continue;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: {e.StackTrace}");
                Console.WriteLine($"Error: {e.Message}");
                lastStatusCode = HandleError(host, e, request.Timestamp, request.FullURL, HttpStatusCode.InternalServerError);
            }
        }

        // If we get here, then no hosts were able to handle the request
        //Console.WriteLine($"{path}  - {lastStatusCode}");

        return new ProxyData
        {
            StatusCode = (HttpStatusCode)lastStatusCode,
            Body = Encoding.UTF8.GetBytes("No active hosts were able to handle the request.")
        };


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
    private async Task GetProxyResponseAsync(HttpResponseMessage proxyResponse, RequestData request, ProxyData pr)
    {       
        // Get a stream to the response body
        await using (var responseBody = await proxyResponse.Content.ReadAsStreamAsync())
        {
            if (request.Debug)
            {
                LogHeaders(proxyResponse.Headers, "<");
                LogHeaders(proxyResponse.Content.Headers, "  <");
            }

            // Copy across all the response headers to the client
            CopyHeaders(proxyResponse, pr.Headers, pr.ContentHeaders);

            // Determine the encoding from the Content-Type header
            MediaTypeHeaderValue? contentType = proxyResponse.Content.Headers.ContentType;
            var encoding = GetEncodingFromContentType(contentType, request);

            using (var reader = new StreamReader(responseBody, encoding))
            {
                pr.Body= encoding.GetBytes(await reader.ReadToEndAsync());
            }
        }
    }

    private Encoding GetEncodingFromContentType(MediaTypeHeaderValue? contentType, RequestData request)
    {
        if (string.IsNullOrWhiteSpace(contentType?.CharSet))
        {
            if (request.Debug)
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
            HandleError(null, null, request.Timestamp, request.FullURL, HttpStatusCode.UnsupportedMediaType,
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

    private HttpStatusCode HandleError(BackendHost? host, Exception? e, DateTime requestDate, string url, HttpStatusCode statusCode, string? customMessage = null)
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

        host?.AddError();
        return statusCode;
    }

}