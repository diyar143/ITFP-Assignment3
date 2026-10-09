using System.Text;
using System.Text.Json;
using ColdChainMonitor;
using Xunit;

public class PipelineTests
{
    private static Reading R(string sensor, string time, StorageClass cls, decimal temp) =>
        new(sensor, DateTimeOffset.Parse(time), cls, temp);

    private static ImportResult Import(string text)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return MonitoringPipeline.ReadReadings(ms);
    }

    [Fact]
    public void SuppliedInputHasExpectedCounts()
    {
        string input = """
S1|2026-09-22T08:10:00Z|Cold|5.2
S2|2026-09-22T08:01:00Z|Frozen|-18.0
S1|2026-09-22T08:00:00Z|Cold|4.0
S1|2026-09-22T08:05:00Z|Cold|8.7
S2|2026-09-22T08:06:00Z|Frozen|-12.0
S1|2026-09-22T08:15:00Z|Cold|8.0
S3|bad-date|Cold|3.5
S2|2026-09-22T08:11:00Z|99|-17.0
S1|2026-09-22T08:20:00Z|Cold|5,5
S1|2026-09-22T08:25:00Z|Frozen|-17.0
""";
        var result = Import(input);
        Assert.Equal(6, result.Readings.Count);
        Assert.Equal(4, result.Errors.Count);
        Assert.Equal(2, MonitoringPipeline.Analyze(result.Readings).Count);
    }

    [Fact] public void NumericEnumIsRejected() =>
        Assert.False(ReadingParser.TryParseReading("S1|2026-09-22T08:00:00Z|99|4.0", out _, out _));

    [Fact] public void CommaDecimalIsRejected() =>
        Assert.False(ReadingParser.TryParseReading("S1|2026-09-22T08:00:00Z|Cold|5,5", out _, out _));

    [Fact] public void StorageClassMismatchIsImportError()
    {
        var result = Import("S1|2026-09-22T08:00:00Z|Cold|4.0\nS1|2026-09-22T08:01:00Z|Frozen|-18.0");
        Assert.Single(result.Readings);
        Assert.Single(result.Errors);
    }

    [Theory]
    [InlineData("Cold", "8.0")]
    [InlineData("Frozen", "-15.0")]
    public void BoundaryValuesAreSafe(string cls, string temp)
    {
        Assert.True(ReadingParser.TryParseReading($"S1|2026-09-22T08:00:00Z|{cls}|{temp}", out var r, out _));
        Assert.Empty(MonitoringPipeline.Analyze(new[] { r! }));
    }

    [Fact] public void ComparisonsDoNotCrossSensorBoundaries()
    {
        var readings = new[] { R("A","2026-09-22T08:00:00Z",StorageClass.Cold,4m), R("B","2026-09-22T08:01:00Z",StorageClass.Cold,8m) };
        Assert.Empty(MonitoringPipeline.Analyze(readings));
    }

    [Fact] public void InputOrderCanDifferFromTimestampOrder()
    {
        var readings = new[] { R("S1","2026-09-22T08:05:00Z",StorageClass.Cold,8.7m), R("S1","2026-09-22T08:00:00Z",StorageClass.Cold,4m) };
        var alerts = MonitoringPipeline.Analyze(readings);
        Assert.Single(alerts);
        Assert.True(alerts[0].AbruptChange);
    }

    [Fact] public void ReadReadingsLeavesStreamOpen()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("S1|2026-09-22T08:00:00Z|Cold|4.0"));
        MonitoringPipeline.ReadReadings(ms);
        Assert.True(ms.CanRead);
    }

    [Fact] public void WriteArchiveLeavesStreamOpen()
    {
        using var ms = new MemoryStream();
        MonitoringPipeline.WriteArchive(ms, EmptyArchive());
        Assert.True(ms.CanWrite);
    }

    [Fact] public void RoundTripPreservesValuesAndEnumStrings()
    {
        var archive = new MonitoringArchive(
            new[] { R("S1","2026-09-22T08:00:00Z",StorageClass.Cold,4m) },
            Array.Empty<ImportError>(), Array.Empty<Alert>(), DateTimeOffset.Parse("2026-09-22T00:00:00Z"));
        using var ms = new MemoryStream();
        MonitoringPipeline.WriteArchive(ms, archive);
        string json = Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("\"cold\"", json);
        ms.Position = 0;
        Assert.True(MonitoringPipeline.ArchivesEqual(archive, MonitoringPipeline.ReadArchive(ms)));
    }

    [Fact] public void StoredResultsWorkAfterSourceStreamDisposed()
    {
        ImportResult result;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("S1|2026-09-22T08:00:00Z|Cold|4.0")))
            result = MonitoringPipeline.ReadReadings(ms);
        Assert.Single(result.Readings);
        Assert.Equal("S1", result.Readings[0].SensorId);
    }

    [Fact] public void ReadArchiveLeavesStreamOpen()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("{\"readings\":[],\"errors\":[],\"alerts\":[],\"createdAtUtc\":\"2026-09-22T00:00:00+00:00\"}"));
        MonitoringPipeline.ReadArchive(ms);
        Assert.True(ms.CanRead);
    }

    [Fact] public void ReadReadingsStartsAtCurrentPosition()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("skip\nS1|2026-09-22T08:00:00Z|Cold|4.0\n");
        using var ms = new MemoryStream(bytes);
        ms.Position = Encoding.UTF8.GetByteCount("skip\n");
        var result = MonitoringPipeline.ReadReadings(ms);
        Assert.Single(result.Readings);
        Assert.Equal(1, result.Errors.Count == 0 ? 0 : result.Errors[0].LineNumber);
    }

    [Fact] public void BlankPhysicalLinesStillCountForErrorNumbers()
    {
        var result = Import("\n\nS1|bad-date|Cold|3.0");
        Assert.Equal(3, result.Errors[0].LineNumber);
    }

    [Fact] public void MalformedJsonFailsExplicitly()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("{ definitely not json"));
        Assert.Throws<JsonException>(() => MonitoringPipeline.ReadArchive(ms));
    }

    private static MonitoringArchive EmptyArchive() =>
        new(Array.Empty<Reading>(), Array.Empty<ImportError>(), Array.Empty<Alert>(), DateTimeOffset.UtcNow);
}
