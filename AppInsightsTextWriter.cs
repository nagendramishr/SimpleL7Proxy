using Microsoft.ApplicationInsights;
using System;
using System.IO;
using System.Text;

public class AppInsightsTextWriter : TextWriter
{
    private readonly TelemetryClient _telemetryClient;
    private readonly TextWriter _innerTextWriter;

    public AppInsightsTextWriter(TelemetryClient telemetryClient, TextWriter innerTextWriter)
    {
        _telemetryClient = telemetryClient;
        _innerTextWriter = innerTextWriter;
    }

    public override void WriteLine(string? value)
    {
        base.WriteLine(value);

        if (value =="\n\n") {
            _innerTextWriter.WriteLine($"{value}");
            return;
        }

        _telemetryClient.TrackTrace(value);
        _innerTextWriter.WriteLine($"{DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff")} {value}");
    }

    public override Encoding Encoding => Encoding.UTF8;
}