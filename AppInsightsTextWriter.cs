using Microsoft.ApplicationInsights;
using System;
using System.IO;
using System.Text;

public class AppInsightsTextWriter : TextWriter
{
    private readonly TelemetryClient _telemetryClient;

    public AppInsightsTextWriter(TelemetryClient telemetryClient)
    {
        _telemetryClient = telemetryClient;
    }

    public override void WriteLine(string? value)
    {
        _telemetryClient.TrackTrace(value);
        base.WriteLine(value);
    }

    public override Encoding Encoding => Encoding.UTF8;
}