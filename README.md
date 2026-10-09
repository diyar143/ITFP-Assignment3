# ColdChainMonitor

Console app for processing cold-chain sensor readings (.NET 8 / C# 12).

## Run
```bash
cd ColdChainMonitor
dotnet run
```

The program reads `readings.txt`, creates `archive.json`, then verifies JSON serialization/deserialization through a `MemoryStream`.

## Run tests
From the repository root:
```bash
dotnet test
```

## Required explanations

1. **Stream ownership:** `Program` opens and disposes file streams. Helper methods receive streams from their caller, so they leave them open. `StreamReader` uses `leaveOpen: true`.
2. **Lazy sequence:** a lazy iterator may try to read from a `StreamReader` only when enumerated later. If the reader has already been disposed, enumeration fails. The importer reads everything and returns materialized collections.
3. **MemoryStream position:** writing advances `Position` to the end. Set `Position = 0` before deserializing so the reader starts at the first JSON byte.
4. **Grouping before pairs:** globally adjacent readings can belong to different sensors. Group by sensor first, sort each group by timestamp, then compare adjacent readings.
5. **Enum validation:** `Enum.TryParse` can parse the text `99` into an enum value even if it is not declared. Check `Enum.IsDefined` as well.
6. **Pure vs I/O:** `TryParseReading` and `Analyze` operate on supplied values and do not access files or the clock. `ReadReadings`, `WriteArchive`, and `ReadArchive` operate on streams. `Program` owns file access and supplies `CreatedAtUtc`.
7. **Value comparison:** JSON deserialization creates new objects, so references differ even when the data is equal. Compare property values and collection contents.
8. **Deterministic ordering and time:** stable, specified ordering makes output predictable. Passing `CreatedAtUtc` into the archive keeps time generation outside parsing/analysis and makes tests reproducible.

## Expected supplied-file result
- 6 valid readings
- 4 import errors: invalid date, undefined numeric enum, comma decimal, sensor class mismatch
- 2 alerts: S1 at 08:05 (8.7) and S2 at 08:06 (-12.0)
- 8.0 for Cold is a safe boundary

## Debugging explanations
- A sequence returned from a method must not depend on a reader that was disposed before enumeration.
- A newly written `MemoryStream` is positioned at the end; rewind it explicitly before reading.
- Adjacent comparisons are meaningful only inside the same sensor group.
