using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class EventHubClient : IEventHubClient
{

    private EventHubProducerClient _producerClient;
    private EventDataBatch _batchData;
    private CancellationTokenSource _cancellationTokenSource;
    private Queue<string>  EHLogBuffer = new Queue<string>();
    private object _lockObject = new object();

    public EventHubClient(string connectionString, string eventHubName)
    {
        _producerClient = new EventHubProducerClient(connectionString, eventHubName);
        _cancellationTokenSource = new CancellationTokenSource();

        _batchData = _producerClient.CreateBatchAsync().Result;
    }

    public void StartTimer()
    {
        Task.Run(() => WriterTask());
    }

    public async Task WriterTask()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        try {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var buffer = EHLogBuffer;

                while (buffer.Count > 0) {
                    // swap buffers
                    lock (_lockObject)
                    {
                        EHLogBuffer = new Queue<string>();
                    }

                    // pull 100 items at a time from the buffer and send them to the event hub
                    for (int itemCount = 0; itemCount < 100 && buffer.Count > 0; itemCount++)
                    {
                        var line = buffer.Dequeue();
                        if (line != null)
                            _batchData.TryAdd(new EventData(Encoding.UTF8.GetBytes(line)));
                    }
                    if (_batchData.Count > 0)
                    {
                        await _producerClient.SendAsync(_batchData);
                        _batchData = await _producerClient.CreateBatchAsync();
                    }
                }
                await Task.Delay(1000, _cancellationTokenSource.Token); // Wait for 1 second
            }

        } catch (TaskCanceledException) {
            // Ignore
        }
    }

    public void StopTimer()
    {
        _cancellationTokenSource?.Cancel();
    }

    public void SendData(string? value)
    {
        if (value == null) return;

        if (value.StartsWith("\n\n")) 
            value = value.Substring(2);
        
        lock (_lockObject)
        {
            EHLogBuffer.Enqueue(value);
        }
    }
}