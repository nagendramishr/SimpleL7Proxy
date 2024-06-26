public interface IBackendService
{
    void Start();
    public List<BackendHost> GetActiveHosts();

}

public interface IEventHubClient
{
    void StartTimer();
    void StopTimer();
    void SendData(string? value);
}

public interface IBackendOptions {
    int Port { get; set; }
    int PollInterval { get; set; }
    int PollTimeout { get; set; }
    int SuccessRate { get; set; }
    int Timeout { get; set; }

    List<BackendHost>? Hosts { get; set; }
    HttpClient? Client { get; set; }
}