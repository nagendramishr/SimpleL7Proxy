
public interface IServer
{
    Task Run(CancellationToken cancellationToken);
    void Start();
}