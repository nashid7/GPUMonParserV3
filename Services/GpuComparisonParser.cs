using System.Globalization;
using System.Text;

namespace par.Services;

public sealed record ComparisonMetric(decimal? Minimum, decimal? Average, decimal? Maximum);
public sealed record ComparisonSample(
    double ElapsedSeconds,
    string TimeLabel,
    decimal? GpuTemperature,
    decimal? MemoryTemperature,
    decimal? Power,
    decimal? GpcClock,
    decimal? MemoryClock);
public sealed record ComparisonCappingSegment(string Reason, double StartSeconds, double EndSeconds, double DurationSeconds);

public sealed class ComparisonRunResult
{
    public required string Name { get; init; }
    public required string SourceFile { get; init; }
    public string BrandName { get; init; } = "n/a";
    public string VbiosVersion { get; init; } = "n/a";
    public string DriverVersion { get; init; } = "n/a";
    public int TotalSamples { get; init; }
    public int FilteredSamples { get; init; }
    public double DurationSeconds { get; init; }
    public required ComparisonMetric GpuTemperature { get; init; }
    public required ComparisonMetric MemoryTemperature { get; init; }
    public required ComparisonMetric Power { get; init; }
    public required ComparisonMetric GpcClock { get; init; }
    public required ComparisonMetric MemoryClock { get; init; }
    public required IReadOnlyList<ComparisonSample> Samples { get; init; }
    public required IReadOnlyList<ComparisonCappingSegment> CappingSegments { get; init; }
}

public sealed class GpuComparisonParser
{
    private sealed record RawSample(
        int Index,
        DateTime? Timestamp,
        decimal Utilization,
        decimal? GpuTemperature,
        decimal? MemoryTemperature,
        decimal? Power,
        decimal? GpcClock,
        decimal? MemoryClock,
        IReadOnlyList<string> CappingReasons);

    public ComparisonRunResult Parse(string content, string sourceFile, string runName, int utilizationMin, int utilizationMax)
    {
        var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var headerIndex = FindHeader(lines);
        if (headerIndex < 0) throw new InvalidOperationException("No supported CSV header was found.");

        var delimiter = lines[headerIndex].Contains('\t') ? '\t' : ',';
        var headers = SplitDelimitedLine(lines[headerIndex], delimiter);
        var cleaned = headers.Select(NormalizeHeader).ToArray();
        var dateIndex = FindColumn(cleaned, "date");
        var timeIndex = FindColumn(cleaned, "timestamp", "time", "datetime");
        var utilizationIndex = FindColumn(cleaned, "gpu_utilization", "gpc_util", "gpu_util", "utilization");
        var gpuTemperatureIndex = FindColumn(cleaned, "temperature_gpu", "gpu_tj", "t_gpu", "gpu_temp");
        var memoryTemperatureIndex = FindColumn(cleaned, "temperature_memory", "mem_tj", "mem_temp", "memory_temp");
        var powerIndex = FindColumn(cleaned, "tgp", "graphics_tgp", "gpu_power");
        var gpcClockIndex = FindColumn(cleaned, "gpc_clock", "graphics_clock");
        var memoryClockIndex = FindColumn(cleaned, "memory_clock", "mem_clock");
        var cappingIndex = FindColumn(cleaned, "capping_reason", "throttle_reason");
        if (utilizationIndex < 0) throw new InvalidOperationException("GPU utilization column was not found.");
        if (gpuTemperatureIndex < 0 && memoryTemperatureIndex < 0)
            throw new InvalidOperationException("GPU and memory temperature columns were not found.");

        var rawSamples = new List<RawSample>();
        for (var lineIndex = headerIndex + 1; lineIndex < lines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex])) continue;
            var columns = SplitDelimitedLine(lines[lineIndex], delimiter);
            var utilization = ParseDecimal(columns, utilizationIndex);
            if (!utilization.HasValue) continue;
            var timestamp = ParseTimestamp(columns, dateIndex, timeIndex);
            var capping = Safe(columns, cappingIndex);
            rawSamples.Add(new RawSample(
                rawSamples.Count,
                timestamp,
                utilization.Value,
                ParseDecimal(columns, gpuTemperatureIndex),
                ParseDecimal(columns, memoryTemperatureIndex),
                ParseDecimal(columns, powerIndex),
                ParseDecimal(columns, gpcClockIndex),
                ParseDecimal(columns, memoryClockIndex),
                ParseCappingReasons(capping)));
        }
        if (rawSamples.Count == 0) throw new InvalidOperationException("No data rows could be parsed.");

        var minimum = Math.Clamp(Math.Min(utilizationMin, utilizationMax), 0, 100);
        var maximum = Math.Clamp(Math.Max(utilizationMin, utilizationMax), 0, 100);
        var firstTimestamp = rawSamples.Select(sample => sample.Timestamp).FirstOrDefault(timestamp => timestamp.HasValue);
        var positions = rawSamples.Select(sample => firstTimestamp.HasValue && sample.Timestamp.HasValue
                ? Math.Max(0, (sample.Timestamp.Value - firstTimestamp.Value).TotalSeconds)
                : (double)sample.Index)
            .ToArray();
        var intervals = positions.Zip(positions.Skip(1), (left, right) => right - left)
            .Where(interval => interval > 0)
            .OrderBy(interval => interval)
            .ToArray();
        var sampleInterval = intervals.Length == 0 ? 1d : intervals[intervals.Length / 2];
        var duration = positions[^1] + sampleInterval;

        var filtered = rawSamples
            .Select((sample, index) => new { sample, position = positions[index] })
            .Where(item => item.sample.Utilization >= minimum && item.sample.Utilization <= maximum)
            .ToArray();
        if (filtered.Length == 0)
            throw new InvalidOperationException($"No samples matched the {minimum}-{maximum}% utilization range.");

        var gpuValues = filtered.Where(item => item.sample.GpuTemperature.HasValue).Select(item => item.sample.GpuTemperature!.Value).ToArray();
        var memoryValues = filtered.Where(item => item.sample.MemoryTemperature.HasValue).Select(item => item.sample.MemoryTemperature!.Value).ToArray();
        var powerValues = filtered.Where(item => item.sample.Power.HasValue).Select(item => item.sample.Power!.Value).ToArray();
        var gpcClockValues = filtered.Where(item => item.sample.GpcClock.HasValue).Select(item => item.sample.GpcClock!.Value).ToArray();
        var memoryClockValues = filtered.Where(item => item.sample.MemoryClock.HasValue).Select(item => item.sample.MemoryClock!.Value).ToArray();
        var cappingSegments = BuildCappingSegments(rawSamples, positions, sampleInterval);
        var metadata = ParseMetadata(lines);

        return new ComparisonRunResult
        {
            Name = runName,
            SourceFile = sourceFile,
            BrandName = Metadata(metadata, "brand_name"),
            VbiosVersion = Metadata(metadata, "vbios_version", "vbios"),
            DriverVersion = Metadata(metadata, "driver_version"),
            TotalSamples = rawSamples.Count,
            FilteredSamples = filtered.Length,
            DurationSeconds = duration,
            GpuTemperature = Metric(gpuValues),
            MemoryTemperature = Metric(memoryValues),
            Power = Metric(powerValues),
            GpcClock = Metric(gpcClockValues),
            MemoryClock = Metric(memoryClockValues),
            Samples = filtered.Select(item => new ComparisonSample(
                    item.position,
                    item.sample.Timestamp?.ToString("HH:mm:ss") ?? FormatElapsed(item.position),
                    item.sample.GpuTemperature,
                    item.sample.MemoryTemperature,
                    item.sample.Power,
                    item.sample.GpcClock,
                    item.sample.MemoryClock))
                .ToArray(),
            CappingSegments = cappingSegments,
        };
    }

    private static ComparisonMetric Metric(decimal[] values)
        => values.Length == 0 ? new(null, null, null) : new(values.Min(), values.Average(), values.Max());

    private static IReadOnlyList<ComparisonCappingSegment> BuildCappingSegments(IReadOnlyList<RawSample> samples, IReadOnlyList<double> positions, double sampleInterval)
    {
        var segments = new List<ComparisonCappingSegment>();
        var reasons = samples.SelectMany(sample => sample.CappingReasons).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var reason in reasons)
        {
            double? start = null;
            for (var index = 0; index < samples.Count; index++)
            {
                var active = samples[index].CappingReasons.Contains(reason, StringComparer.OrdinalIgnoreCase);
                if (active && !start.HasValue) start = positions[index];
                if (!active && start.HasValue)
                {
                    segments.Add(new(reason, start.Value, positions[index], positions[index] - start.Value));
                    start = null;
                }
            }
            if (start.HasValue)
            {
                var end = positions[^1] + sampleInterval;
                segments.Add(new(reason, start.Value, end, end - start.Value));
            }
        }
        return segments.OrderBy(segment => segment.StartSeconds).ThenBy(segment => segment.Reason).ToArray();
    }

    private static IReadOnlyList<string> ParseCappingReasons(string value)
        => value.Split(new[] { ' ', ',', ';', '|', '+', '[', ']' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(reason => !reason.Equals("none", StringComparison.OrdinalIgnoreCase) && !reason.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int FindHeader(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if ((line.Contains(',') || line.Contains('\t')) &&
                line.Contains("util", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("date", StringComparison.OrdinalIgnoreCase) || line.Contains("timestamp", StringComparison.OrdinalIgnoreCase)))
                return index;
        }
        return -1;
    }

    private static int FindColumn(IReadOnlyList<string> headers, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            for (var index = 0; index < headers.Count; index++)
                if (headers[index].Equals(alias, StringComparison.OrdinalIgnoreCase)) return index;
        }
        foreach (var alias in aliases)
        {
            for (var index = 0; index < headers.Count; index++)
                if (headers[index].Contains(alias, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
    }

    private static DateTime? ParseTimestamp(string[] columns, int dateIndex, int timeIndex)
    {
        var date = Safe(columns, dateIndex);
        var time = Safe(columns, timeIndex);
        var combined = dateIndex >= 0 && timeIndex >= 0 && dateIndex != timeIndex ? $"{date} {time}" : time;
        return DateTime.TryParse(combined, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var invariant)
            ? invariant
            : DateTime.TryParse(combined, out var current) ? current : null;
    }

    private static decimal? ParseDecimal(string[] columns, int index)
        => decimal.TryParse(Safe(columns, index), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Safe(string[] columns, int index)
        => index >= 0 && index < columns.Length ? columns[index].Trim().Trim('"') : string.Empty;

    private static string[] SplitDelimitedLine(string line, char delimiter)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') { current.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (character == delimiter && !quoted) { values.Add(current.ToString()); current.Clear(); }
            else current.Append(character);
        }
        values.Add(current.ToString());
        return values.ToArray();
    }

    private static Dictionary<string, string> ParseMetadata(IReadOnlyList<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = Enumerable.Range(0, lines.Count).FirstOrDefault(index => lines[index].Contains("GPU Information", StringComparison.OrdinalIgnoreCase), -1);
        if (section < 0) return result;
        var populated = lines.Skip(section + 1).Where(line => !string.IsNullOrWhiteSpace(line.Trim(','))).Take(2).ToArray();
        if (populated.Length < 2) return result;
        var headers = SplitDelimitedLine(populated[0], ',');
        var values = SplitDelimitedLine(populated[1], ',');
        for (var index = 0; index < Math.Min(headers.Length, values.Length); index++) result[NormalizeHeader(headers[index])] = values[index].Trim();
        return result;
    }

    private static string Metadata(Dictionary<string, string> metadata, params string[] aliases)
        => aliases.Select(alias => metadata.GetValueOrDefault(alias)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "n/a";

    private static string NormalizeHeader(string header)
    {
        var value = header.Trim().Trim('"').ToLowerInvariant();
        if (value.Length > 1 && char.IsDigit(value[0]) && value.Contains(':')) value = value[(value.IndexOf(':') + 1)..];
        var parenthesis = value.IndexOf('(');
        if (parenthesis >= 0) value = value[..parenthesis];
        value = value.Replace("%", "").Replace("/", " ").Replace("-", " ").Replace(".", " ");
        return string.Join('_', value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim('_');
    }

    private static string FormatElapsed(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }
}
