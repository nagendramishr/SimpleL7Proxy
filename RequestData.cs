using System.Net;

public class RequestData : IDisposable
{
    public string Method { get; set; }
    public string Path { get; set; }
    public WebHeaderCollection Headers { get; set; }
    public Stream Body { get; set; }
    public bool debug = false;
    public HttpListenerContext? context { get; set; }
    public DateTime currentDate { get; set; }
    public string FullURL { get; set; }

    public RequestData(HttpListenerContext context) {

        if (context.Request.Url?.PathAndQuery == null ) {
            throw new ArgumentNullException("RequestData");
        }

        Path = context.Request.Url.PathAndQuery;
        Method = context.Request.HttpMethod;
        Headers = (WebHeaderCollection) context.Request.Headers;
        Body = context.Request.InputStream;
        this.context = context;
        currentDate = DateTime.UtcNow;
        FullURL="";
    }

        // Implement IDisposable
    public void Dispose()
    {
        try {
            Dispose(true);
        } catch (Exception e) {
        //    Console.WriteLine("Error disposing RequestData: " + e.Message);
        }        
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Dispose managed resources
            Body?.Dispose();
            if (context != null)
            {
                context.Response?.Close();
                context.Request?.InputStream?.Dispose();
                context.Response?.OutputStream?.Dispose();
            }
        }
    }

    // Destructor to ensure resources are released
    ~RequestData()
    {
        Dispose(false);
    }
}