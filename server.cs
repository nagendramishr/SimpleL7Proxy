using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using Microsoft.ApplicationInsights; // Add this for TelemetryClient

public class Server : IServer
{
    private IBackendService? _backends;
    private IBackendOptions? _options;
    private IEventHubClient? _eventHubClient;
    private static bool _debug=false;
    private readonly TelemetryClient? _telemetryClient; // Add this line


    // Define the set of status codes that you want to allow
    public static HashSet<HttpStatusCode> allowedStatusCodes = new HashSet<HttpStatusCode>
    {
        HttpStatusCode.OK,
        HttpStatusCode.Unauthorized,
        HttpStatusCode.Forbidden,
        HttpStatusCode.NotFound,
    };

    public Server(IOptions<BackendOptions>  backendOptions, IBackendService backends, TelemetryClient? telemetryClient, IEventHubClient? eventHubClient)
    {

        if (backendOptions == null) throw new ArgumentNullException(nameof(backendOptions));
        if (backends == null) throw new ArgumentNullException(nameof(backends));
        if (backendOptions.Value == null) throw new ArgumentNullException(nameof(backendOptions.Value));

        _options = backendOptions.Value;
        _backends = backends;
        _telemetryClient = telemetryClient; 
        _eventHubClient = eventHubClient;

        var timeoutTime = TimeSpan.FromMilliseconds(_options.Timeout).ToString(@"hh\:mm\:ss\.fff");
        Console.WriteLine($"Server created:  Port: {_options.Port} Timeout: {timeoutTime}");
    }

    public async Task  Run()
    {
        if (_options == null) throw new ArgumentNullException(nameof(_options));
        if (_backends == null) throw new ArgumentNullException(nameof(_backends));

        var prefixes = new[] { $"http://+:{_options.Port}/" };

        using (var httpListener = new HttpListener())
        {
            foreach (var prefix in prefixes)
            {
                httpListener.Prefixes.Add(prefix);
            }

            httpListener.Start();
            Console.WriteLine($"Listening on {string.Join(", ", prefixes)}");

            while (true)
            {
                var context = httpListener.GetContext();
                var request = context.Request;
                var response = context.Response;


                try {

                    if (request.Url != null)
                    {   
                        await ProxyRequestAsync(request.HttpMethod, request.Url.PathAndQuery, (WebHeaderCollection) request.Headers, request.InputStream, response);
                    }
                    else
                    {
                        response.StatusCode = 400;
                        using (var writer = new StreamWriter(response.OutputStream))
                        {
                            await writer.WriteAsync("Bad Request");
                        }
                    } 
                } catch (Exception e) {
                    _telemetryClient?.TrackException(e);
                    Console.WriteLine($"Error: {e.StackTrace}");
                }
                finally {
                    if (request.Url != null)
                    {
                        _telemetryClient?.TrackRequest($"{request.HttpMethod} {request.Url.PathAndQuery}", DateTimeOffset.UtcNow, new TimeSpan(0, 0, 0), $"{response.StatusCode}", true);  
                    }
                }
            }
        }
    }

    public async Task ProxyRequestAsync(string method, string path, WebHeaderCollection headers, Stream body, HttpListenerResponse response)
    {

        bool _ldebug = _debug;

        if (_backends == null) throw new ArgumentNullException(nameof(_backends));
        if (_options == null) throw new ArgumentNullException(nameof(_options));
        if (_options.Client == null) throw new ArgumentNullException(nameof(_options.Client));

        // Make a local copy of the active hosts
        var activeHosts = _backends.GetActiveHosts().ToList();
        MemoryStream? ms;

        // check if rewind the stream is possible, if not, then we'll cache the body for later
        if (!body.CanSeek)
        {
            ms = new MemoryStream();
            await body.CopyToAsync(ms);
            ms.Position = 0;
            body = ms;
        }

        // set ldebug to true if the "S7pDebug" header is set to "true"
        if (headers["S7PDEBUG"] == "true")
        {
            _ldebug = true;
        }

        HttpStatusCode lastStatusCode = HttpStatusCode.OK; 
        foreach (var host in activeHosts)
        {
            // Try the request on each active host, stop if it worked
            try
            {               
                headers.Set("Host", host.host);
            
                // Make a call to https://host/path using the headers and body, read the response as a stream
                // make sure there is a slash at the beginning of the path

                var urlWithPath = new UriBuilder(host.url){Path = path}.Uri.AbsoluteUri;
                urlWithPath = System.Net.WebUtility.UrlDecode(urlWithPath);

                var proxyRequest = new HttpRequestMessage(new HttpMethod(method), urlWithPath)
                {   
                    Content = new StreamContent(body)
                };

                foreach (var key in headers.AllKeys)
                {
                    if (_ldebug) Console.WriteLine($" > {key} : {headers[key]}");

                    if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    {
                        proxyRequest.Content.Headers.TryAddWithoutValidation(key, headers[key]);
                    }
                    else
                    {
                        proxyRequest.Headers.TryAddWithoutValidation(key, headers[key]);
                    }
                }
                proxyRequest.Headers.ConnectionClose = true;
                if (_ldebug) {
                    foreach (var header in proxyRequest.Headers)
                    {
                        Console.WriteLine($"  | {header.Key} : {string.Join(", ", header.Value)}");
                    }
                    foreach (var header in proxyRequest.Content.Headers)
                    {
                        Console.WriteLine($"  | {header.Key} : {string.Join(", ", header.Value)}");
                    }
                }

                //using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_options.Client.Timeout.TotalMilliseconds));
                var proxyResponse = await _options.Client.SendAsync(proxyRequest);//, cts.Token);

                lastStatusCode = proxyResponse.StatusCode;

                // Check if the status code of the response is in the set of allowed status codes, else try the next host
                if (!allowedStatusCodes.Contains(proxyResponse.StatusCode))
                {
  
                    body.Position = 0;
                    if (_debug)
                        Console.WriteLine($"Trying next host: Response: {proxyResponse.StatusCode}");
                    continue;
                }

                // Get a stream to the response body
                var responseBody = await proxyResponse.Content.ReadAsStreamAsync();

                if (_ldebug) {
                    foreach (var header in proxyResponse.Headers)
                    {
                        Console.WriteLine($"< {header.Key} : {string.Join(", ", header.Value)}");
                    }
                    foreach (var header in proxyResponse.Content.Headers)
                    {
                        Console.WriteLine($"< {header.Key} : {string.Join(", ", header.Value)}");
                    }
                }

                // Copy across all the response headers to the client
                foreach (var header in proxyResponse.Headers)
                {
                    response.Headers.Add(header.Key, string.Join(", ", header.Value));
                }

                response.StatusCode = (int)proxyResponse.StatusCode;

                using (var output = response.OutputStream)
                {
                    await responseBody.CopyToAsync(output);
                }
                
                Console.WriteLine($"{urlWithPath}  {response.Headers["Content-Length"]} {response.StatusCode}");
                _eventHubClient?.SendData($"{urlWithPath} {response.StatusCode}");                  

                return;
            }
            
            catch (TaskCanceledException)
            {
                // rewind the stream
                body.Position = 0;
                lastStatusCode = HttpStatusCode.RequestTimeout;
                continue;
            }
            catch (OperationCanceledException)
            {
                body.Position = 0;
                lastStatusCode = HttpStatusCode.BadGateway;
                continue;
            }
            catch (HttpRequestException e)
            {
                _telemetryClient?.TrackException(e);
                Console.WriteLine($"Request to {host.url} error:  {e.Message}");
                // rewind the stream
                body.Position = 0;
                lastStatusCode = HttpStatusCode.BadRequest;
                continue;
            }
            catch (Exception e)
            {
                _telemetryClient?.TrackException(e);
                Console.WriteLine($"Error: {e.StackTrace}");
                Console.WriteLine($"Error: {e.Message}");

                lastStatusCode = HttpStatusCode.InternalServerError;
            }
        }


        Console.WriteLine($"{path}  - {lastStatusCode}");
        response.StatusCode = (int)lastStatusCode;
        using (var writer = new StreamWriter(response.OutputStream))
        {
            await writer.WriteAsync("No active hosts were able to handle the request.");
        }
        response.Close();   
    }
}