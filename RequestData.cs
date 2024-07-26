using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;

public class RequestData : IDisposable, IAsyncDisposable
{
    public HttpListenerContext? Context { get; private set; }
    public Stream? Body { get; private set; }
    public DateTime Timestamp { get; private set; }
    public string Path { get; private set; }
    public string Method { get; private set; }
    public WebHeaderCollection Headers { get; private set; }
    public string FullURL { get;  set; }
    public bool Debug { get; set; } 

    public RequestData(HttpListenerContext context)
    {
        if (context.Request.Url?.PathAndQuery == null)
        {
            throw new ArgumentNullException("RequestData");
        }

        Path = context.Request.Url.PathAndQuery;
        Method = context.Request.HttpMethod;
        Headers = (WebHeaderCollection)context.Request.Headers;
        Body = context.Request.InputStream;
        Context = context;
        Timestamp = DateTime.UtcNow;
        FullURL = "";
        Debug = false;
    }

    // Implement IDisposable
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

protected virtual void Dispose(bool disposing)
{
    if (disposing)
    {
        // Dispose managed resources
        FullURL = Path = Method = null;

        Body?.Dispose();
        Body = null;
        Context?.Response?.Close();
        Context?.Response?.OutputStream?.Dispose();
        Context?.Request?.InputStream.Dispose();
        Context = null;
    }
}

    // Implement IAsyncDisposable
    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore();

        // Dispose of unmanaged resources
        Dispose(false);
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (Body != null)
        {
            await Body.DisposeAsync();
            Body = null;
        }

        if (Context != null)
        {
            if (Context.Request?.InputStream != null)
            {
                await Context.Request.InputStream.DisposeAsync();
            }

            if (Context.Response?.OutputStream != null)
            {
                await Context.Response.OutputStream.DisposeAsync();
            }

            Context.Response?.Close();
            Context = null;
        }
    }

    // Destructor to ensure resources are released
    ~RequestData()
    {
        Dispose(false);
    }
}