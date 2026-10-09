using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColdChainMonitor;

public enum StorageClass { Cold, Frozen }

public sealed record Reading(
    string SensorId,
    DateTimeOffset Timestamp,
    StorageClass StorageClass,
    decimal Temperature);

public sealed record ImportError(int LineNumber, string RawLine, string Message);

public sealed record Alert(
    Reading Reading,
    bool OutsideRange,
    bool AbruptChange)
{
    public string[] Reasons =>
        (OutsideRange, AbruptChange) switch
        {
            (true, true) => ["Outside range", "Abrupt change"],
            (true, false) => ["Outside range"],
            (false, true) => ["Abrupt change"],
            _ => []
        };
}

public sealed record ImportResult(
    IReadOnlyList<Reading> Readings,
    IReadOnlyList<ImportError> Errors);

public sealed record MonitoringArchive(
    IReadOnlyList<Reading> Readings,
    IReadOnlyList<ImportError> Errors,
    IReadOnlyList<Alert> Alerts,
    DateTimeOffset CreatedAtUtc);

public static class ReadingParser
{
    public static bool TryParseReading(string rawLine, out Reading? reading, out string error)
    {
        reading = null;
        error = "";

        string[] fields = rawLine.Split('|');
        if (fields.Length != 4)
        {
            error = "Expected exactly four pipe-separated fields.";
            return false;
        }

        string sensorId = fields[0].Trim();
        string timestampText = fields[1].Trim();
        string classText = fields[2].Trim();
        string temperatureText = fields[3].Trim();

        if (sensorId.Length == 0)
        {
            error = "SensorId must not be empty.";
            return false;
        }

        const string format = "yyyy-MM-dd'T'HH:mm:ss'Z'";
        if (!DateTimeOffset.TryParseExact(
                timestampText, format, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset timestamp))
        {
            error = "Timestamp must match yyyy-MM-dd'T'HH:mm:ss'Z'.";
            return false;
        }

        if (!Enum.TryParse(classText, ignoreCase: true, out StorageClass storageClass)
            || !Enum.IsDefined(typeof(StorageClass), storageClass))
        {
            error = "StorageClass must be Cold or Frozen; numeric/undefined values are not allowed.";
            return false;
        }

        const NumberStyles numberStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        if (!decimal.TryParse(temperatureText, numberStyles, CultureInfo.InvariantCulture, out decimal temperature))
        {
            error = "Temperature must be a decimal number using a dot, not a comma.";
            return false;
        }

        reading = new Reading(sensorId, timestamp, storageClass, temperature);
        return true;
    }
}

public static class MonitoringPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static ImportResult ReadReadings(Stream input)
    {
        var readings = new List<Reading>();
        var errors = new List<ImportError>();
        var sensorClasses = new Dictionary<string, StorageClass>(StringComparer.OrdinalIgnoreCase);
        var sensorSpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StreamReader(input, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024, leaveOpen: true);

        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (!ReadingParser.TryParseReading(line, out Reading? reading, out string message))
            {
                errors.Add(new ImportError(lineNumber, line, message));
                continue;
            }

            if (sensorClasses.TryGetValue(reading!.SensorId, out StorageClass firstClass)
                && firstClass != reading.StorageClass)
            {
                errors.Add(new ImportError(lineNumber, line, "StorageClass differs from this sensor's first valid reading."));
                continue;
            }

            if (!sensorClasses.ContainsKey(reading.SensorId))
            {
                sensorClasses[reading.SensorId] = reading.StorageClass;
                sensorSpellings[reading.SensorId] = reading.SensorId;
            }

            // Keep the original reading unchanged; the first spelling is used for alert grouping/display.
            readings.Add(reading);
        }

        return new ImportResult(Array.AsReadOnly(readings.ToArray()), Array.AsReadOnly(errors.ToArray()));
    }

    public static IReadOnlyList<Alert> Analyze(IReadOnlyList<Reading> readings)
    {
        var alerts = new List<(Alert Alert, int InputIndex)>();

        var groups = readings
            .Select((reading, index) => (reading, index))
            .GroupBy(x => x.reading.SensorId, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(x => x.reading.Timestamp).ThenBy(x => x.index).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                Reading current = ordered[i].reading;
                bool outside = IsOutsideRange(current);
                bool abrupt = false;

                if (i > 0)
                    abrupt = Math.Abs(current.Temperature - ordered[i - 1].reading.Temperature) > 4.0m;

                if (outside || abrupt)
                    alerts.Add((new Alert(current, outside, abrupt), ordered[i].index));
            }
        }

        var firstSpellings = readings
            .GroupBy(r => r.SensorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().SensorId, StringComparer.OrdinalIgnoreCase);

        var result = alerts
            .OrderBy(x => firstSpellings[x.Alert.Reading.SensorId], StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Alert.Reading.Timestamp)
            .ThenBy(x => x.InputIndex)
            .Select(x =>
            {
                var r = x.Alert.Reading;
                string spelling = firstSpellings[r.SensorId];
                return x.Alert with { Reading = r with { SensorId = spelling } };
            })
            .ToArray();

        return Array.AsReadOnly(result);
    }

    private static bool IsOutsideRange(Reading reading) => reading.StorageClass switch
    {
        StorageClass.Cold => reading.Temperature < 2.0m || reading.Temperature > 8.0m,
        StorageClass.Frozen => reading.Temperature < -22.0m || reading.Temperature > -15.0m,
        _ => true
    };

    public static void WriteArchive(Stream output, MonitoringArchive archive)
    {
        JsonSerializer.Serialize(output, archive, JsonOptions);
    }

    public static MonitoringArchive ReadArchive(Stream input)
    {
        MonitoringArchive? archive = JsonSerializer.Deserialize<MonitoringArchive>(input, JsonOptions);
        return archive ?? throw new JsonException("The JSON archive was null.");
    }

    public static bool ArchivesEqual(MonitoringArchive a, MonitoringArchive b)
    {
        return a.CreatedAtUtc == b.CreatedAtUtc
            && a.Readings.SequenceEqual(b.Readings)
            && a.Errors.SequenceEqual(b.Errors)
            && a.Alerts.Count == b.Alerts.Count
            && a.Alerts.Zip(b.Alerts).All(pair =>
                pair.First.Reading == pair.Second.Reading
                && pair.First.OutsideRange == pair.Second.OutsideRange
                && pair.First.AbruptChange == pair.Second.AbruptChange);
    }
}

public static class Program
{
    public static void Main()
    {
        const string inputPath = "readings.txt";
        const string archivePath = "archive.json";

        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"Could not find {inputPath}. Run the app from the project folder.");
            return;
        }

        ImportResult imported;
        using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            imported = MonitoringPipeline.ReadReadings(input);

        var archive = new MonitoringArchive(
            imported.Readings,
            imported.Errors,
            MonitoringPipeline.Analyze(imported.Readings),
            DateTimeOffset.UtcNow);

        using (var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
            MonitoringPipeline.WriteArchive(output, archive);

        MonitoringArchive restored;
        using (var memory = new MemoryStream())
        {
            MonitoringPipeline.WriteArchive(memory, archive);
            memory.Position = 0;
            restored = MonitoringPipeline.ReadArchive(memory);
        }

        if (!MonitoringPipeline.ArchivesEqual(archive, restored))
            throw new InvalidOperationException("JSON round-trip verification failed.");

        Console.WriteLine("ColdChainMonitor finished.");
        Console.WriteLine($"Valid readings: {archive.Readings.Count}");
        Console.WriteLine($"Import errors: {archive.Errors.Count}");
        Console.WriteLine($"Alerts: {archive.Alerts.Count}");
        foreach (Alert alert in archive.Alerts)
        {
            Console.WriteLine($"{alert.Reading.SensorId} | {alert.Reading.Timestamp:HH:mm} | " +
                $"{alert.Reading.Temperature} | {string.Join("; ", alert.Reasons)}");
        }
        Console.WriteLine($"JSON archive written to {Path.GetFullPath(archivePath)}");
        Console.WriteLine("JSON round-trip: OK");
    }
}
