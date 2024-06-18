using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Collections.Generic;

public class Server
{
    private string _port;
    private Backends _backends;
    private HttpClient _client = new HttpClient();

    // Define the set of status codes that you want to allow
    public static HashSet<HttpStatusCode> allowedStatusCodes = new HashSet<HttpStatusCode>
    {
        HttpStatusCode.OK,
        HttpStatusCode.Unauthorized,
    };


    public Server(string port, Backends backends, HttpClient client)
    {
        _backends = backends;
        _port = port;
        //_thread = new Thread(new ThreadStart(Run));
        _client = client;
    }

    public async Task  Run()
    {
        var prefixes = new[] { $"http://+:{_port}/" };

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

                //Console.WriteLine($"Received {request.HttpMethod} request");
                //Console.WriteLine($"Received Headers: {string.Join(", ", request.Headers.AllKeys.Select(k => $"{k}: {request.Headers[k]}"))}");
                try {
                    await ProxyRequestAsync(request.HttpMethod, request.Url.PathAndQuery, (WebHeaderCollection) request.Headers, request.InputStream, response);
                } catch (Exception e) {
                    Program.telemetryClient?.TrackException(e);
                    Console.WriteLine($"Error: {e.StackTrace}");
                }
            }
        }
    }

    public async Task ProxyRequestAsync(string method, string path, WebHeaderCollection headers, Stream body, HttpListenerResponse response)
    {

        // Make a local copy of the active hosts
        var activeHosts = _backends.GetActiveHosts().ToList();

        // Console.WriteLine($"Active Host List: {string.Join(", ", activeHosts)}");

        foreach (var host in activeHosts)
        {
            // Try the request on each active host, stop if it worked
            try
            {               
                headers.Set("Host", host.host);
            
                // Make a call to https://host/path using the headers and body, read the response as a stream
                // Console.WriteLine($"Trying: {host.url}");
                // make sure there is a slash at the beginning of the path

                var urlWithPath = new UriBuilder(host.url){Path = path}.Uri.AbsoluteUri;
                var proxyRequest = new HttpRequestMessage(new HttpMethod(method), urlWithPath)
                {   
                    Content = new StreamContent(body)
                };

                //Console.WriteLine("Got the stream content");

                foreach (var key in headers.AllKeys)
                {
                    proxyRequest.Headers.TryAddWithoutValidation(key, headers[key]);
                }

                //Console.WriteLine("Added headers to the request");
                //Console.WriteLine($"Making a call to: {host.url} with headers: {string.Join(", ", proxyRequest.Headers.Select(k => $"{k.Key}: {string.Join(", ", k.Value)}"))}");

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_client.Timeout.TotalMilliseconds));
                var proxyResponse = await _client.SendAsync(proxyRequest, cts.Token);
                //Console.WriteLine("Got a response");

                // Check if the status code of the response is in the set of allowed status codes
                if (!allowedStatusCodes.Contains(proxyResponse.StatusCode))
                {
                    // rewind the stream
                    body.Position = 0;
                    Console.WriteLine($"Trying next host: Response: {proxyResponse.StatusCode}");
                    continue;
                }

                // Get a stream to the response body
                var responseBody = await proxyResponse.Content.ReadAsStreamAsync();
                //Console.WriteLine($"Got the response body: {responseBody.Length} bytes, with headers: {string.Join(", ", proxyRequest.Headers.Select(k => $"{k.Key}: {string.Join(", ", k.Value)}"))}");

                // Copy across all the response headers to the client
                foreach (var header in proxyResponse.Headers)
                {
                    response.Headers.Add(header.Key, string.Join(", ", header.Value));
                }

                response.StatusCode = (int)proxyResponse.StatusCode;

                //Console.WriteLine($"Response status code: {response.StatusCode}");

                using (var output = response.OutputStream)
                {
                    await responseBody.CopyToAsync(output);
                }
                
                if (Program.telemetryClient is null)
                    Console.WriteLine($"{DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff")} {urlWithPath}  {response.Headers["Content-Length"]} {response.StatusCode}");

                return;
            }
            catch (System.Threading.Tasks.TaskCanceledException e)
            {
                Program.telemetryClient?.TrackException(e);
                Console.WriteLine($"Request to {host.url} error:  {e.Message}");
                // rewind the stream
                body.Position = 0;
                continue;
            }
            catch (Exception e)
            {
                Program.telemetryClient?.TrackException(e);
                Console.WriteLine($"Error: {e.StackTrace}");
                Console.WriteLine($"Error: {e.Message}");
            }
        }
        Console.WriteLine($"{DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff")} {path}  - 503");

        // No active hosts were able to handle the request
        response.StatusCode = 503;
        using (var writer = new StreamWriter(response.OutputStream))
        {
            await writer.WriteAsync("No active hosts were able to handle the request.");
        }
        response.Close();   
    }
}