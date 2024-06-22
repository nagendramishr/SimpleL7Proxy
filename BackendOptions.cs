public class BackendOptions : IBackendOptions
{
    public int Port { get; set; }
    public int PollInterval { get; set; }
    public int SuccessRate { get; set; }
    public int Timeout { get; set; }

    public List<BackendHost>? Hosts { get; set; }
    public HttpClient? Client { get; set; }

}