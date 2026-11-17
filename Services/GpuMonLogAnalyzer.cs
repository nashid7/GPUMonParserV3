using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace par.Services;

public sealed record MetricStats(decimal? Minimum, decimal? Average, decimal? Maximum, int Count)
{
    public static MetricStats From(IReadOnlyCollection<decimal> values)
        => values.Count == 0 ? new(null, null, null, 0) : new(values.Min(), values.Average(), values.Max(), values.Count);
}

public sealed record GpuMonSample(
    int Index,
    DateTime? Timestamp,
    double ElapsedSeconds,
    decimal Utilization,
    decimal? GpuTemperature,
    decimal? MemoryTemperature,
    decimal? TgpWatts,
    decimal? GpcClock,
    decimal? MemoryClock,
    string PState,
    IReadOnlyList<string> CappingReasons);

public sealed record ActivitySegment(string Label, double StartSeconds, double EndSeconds, string StartTime, string EndTime, double DurationSeconds);

public sealed record ActivitySummary(string Label, int InRangeSamples, int InRangePeriods, int TotalSamples);

public sealed record TgpCheckResult(decimal ExpectedWatts, decimal RequiredPercent, int SamplesAtOrAbove, int SampleCount)
{
    public decimal? PercentAtOrAbove => SampleCount == 0 ? null : SamplesAtOrAbove * 100m / SampleCount;

    // Same rule as the Excel check: COUNTIF(range, ">=" & expected) / COUNT(range) >= required %.
    public bool? Passed => SampleCount == 0 ? null : SamplesAtOrAbove * 100m >= RequiredPercent * SampleCount;

    public string Verdict => Passed switch { true => "PASS", false => "FAIL", _ => "N/A" };

    /// <summary>Minimum number of samples that must be at or above the expected TGP to pass.</summary>
    public int RequiredSamples => SampleCount == 0 ? 0 : (int)Math.Clamp(Math.Ceiling(RequiredPercent * SampleCount / 100m), 0, SampleCount);

    /// <summary>How many more samples would need to reach the expected TGP for a PASS.</summary>
    public int SamplesShort => Math.Max(0, RequiredSamples - SamplesAtOrAbove);

    /// <summary>Highest expected TGP this run would still pass with (the TGP met by the required share of samples).</summary>
    public decimal? PassingTgpWatts { get; init; }
}

/// <summary>Raw header text of each column the parser resolved by name.</summary>
public sealed record GpuMonColumns(
    string? Date,
    string? Time,
    string Utilization,
    string? GpuTemperature,
    string? MemoryTemperature,
    string? Tgp,
    string? GpcClock,
    string? MemoryClock,
    string? PState,
    string? CappingReason);

public sealed class GpuMonLog
{
    public string ToolVersion { get; init; } = "n/a";
    public string BrandName { get; init; } = "n/a";
    public string VbiosVersion { get; init; } = "n/a";
    public string DriverVersion { get; init; } = "n/a";
    public required GpuMonColumns Columns { get; init; }
    public required IReadOnlyList<GpuMonSample> Samples { get; init; }
    public DateTime? StartTime { get; init; }
    public double SampleIntervalSeconds { get; init; }
    public double DurationSeconds => Samples.Count == 0 ? 0 : Samples[^1].ElapsedSeconds + SampleIntervalSeconds;
    public decimal? MaxUtilization => Samples.Count == 0 ? null : Samples.Max(sample => sample.Utilization);
}

public sealed class GpuMonAnalysis
{
    public required GpuMonLog Log { get; init; }
    public int UtilizationMin { get; init; }
    public int UtilizationMax { get; init; }
    public string UtilizationRange => $"{UtilizationMin}-{UtilizationMax}%";
    public required IReadOnlyList<GpuMonSample> InRangeSamples { get; init; }
    public required MetricStats GpuTemperature { get; init; }
    public required MetricStats MemoryTemperature { get; init; }
    public required MetricStats Tgp { get; init; }
    public required MetricStats GpcClock { get; init; }
    public required MetricStats MemoryClock { get; init; }
    public required TgpCheckResult TgpCheck { get; set; }
    public required IReadOnlyList<ActivitySegment> PStateTimeline { get; init; }
    public required IReadOnlyList<ActivitySegment> CappingTimeline { get; init; }
    public required IReadOnlyList<ActivitySegment> InRangeWindows { get; init; }
    public required IReadOnlyList<ActivitySegment> InRangeCappingSegments { get; init; }
    public required IReadOnlyList<ActivitySummary> PStateSummary { get; init; }
    public required IReadOnlyList<ActivitySummary> CappingSummary { get; init; }
    public DateTime? FirstInRangeTimestamp => InRangeSamples.Select(sample => sample.Timestamp).FirstOrDefault(timestamp => timestamp.HasValue);
    public DateTime? LastInRangeTimestamp => InRangeSamples.Select(sample => sample.Timestamp).LastOrDefault(timestamp => timestamp.HasValue);
    public TimeSpan InRangeSpan => FirstInRangeTimestamp.HasValue && LastInRangeTimestamp.HasValue
        ? LastInRangeTimestamp.Value - FirstInRangeTimestamp.Value
        : TimeSpan.Zero;
    public decimal CoveragePercent => Log.Samples.Count == 0 ? 0 : InRangeSamples.Count * 100m / Log.Samples.Count;

    /// <summary>Warns when too few samples are inside the utilization range for the results to be trusted.</summary>
    public string? ReliabilityWarning => InRangeSamples.Count switch
    {
        0 => $"No samples are within {UtilizationRange} GPU utilization. Try a wider range.",
        < GpuMonLogAnalyzer.MinimumReliableSamples => $"Only {InRangeSamples.Count} samples are within {UtilizationRange} GPU utilization, so this result may not be reliable. Try a wider range.",
        _ => null
    };
}

/// <summary>
/// Parses NVIDIA GPUMON CSV logs. Columns are located only by their header names, never by position,
/// because GPUMON changes column order depending on the options chosen when recording starts.
/// </summary>
public sealed class GpuMonLogAnalyzer
{
    public const decimal DefaultExpectedTgpWatts = 70m;
    public const decimal DefaultRequiredTgpPercent = 98m;
    public const int MinimumReliableSamples = 100;

    // Normalised header names (GPU prefix and unit removed, lower case, '_' and '-' treated as spaces).
    // GPUMon 7.x uses the descriptive names; GPUMon 6.x uses the short snake_case names.
    private static readonly string[] DateNames = ["date"];
    private static readonly string[] TimeNames = ["timestamp", "time"];
    private static readonly string[] UtilizationNames = ["gpu utilization", "gpc util", "gpu util"];
    private static readonly string[] GpuTemperatureNames = ["temperature gpu", "t gpu", "gpu temp", "gpu tj"];
    private static readonly string[] MemoryTemperatureNames = ["temperature memory", "mem temp", "memory temp", "mem tj"];
    private static readonly string[] GpcClockNames = ["gpc clock", "gpc clk"];
    private static readonly string[] MemoryClockNames = ["memory clock", "clk mem", "mem clock", "memory clk"];
    private static readonly string[] TgpNames = ["tgp", "pwr tgp"];
    private static readonly string[] PStateNames = ["p state", "pstate"];
    private static readonly string[] CappingNames = ["capping reason"];

    private static readonly string[] DateFormats = ["yyyy/MM/dd", "yyyy-MM-dd", "yyyy/M/d", "yyyy-M-d", "MM/dd/yyyy", "M/d/yyyy", "dd.MM.yyyy"];
    private static readonly Regex TimePattern = new(@"^(\d{1,2}):(\d{2}):(\d{2})(?:[:.](\d{1,7}))?$", RegexOptions.Compiled);

    private sealed record HeaderInfo(int Index, string Raw, int? GpuIndex, string Name, string Unit);

    public GpuMonLog Parse(string content)
    {
        var lines = content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        var (headerIndex, delimiter, headers) = FindHeader(lines);
        if (headerIndex < 0)
            throw new InvalidOperationException("This doesn't look like a GPU Monitor (GPUMON) log: no data header with a Date column and a GPU Utilization column " +
                "(e.g. '1:GPU Utilization (%)' or '1:gpc_util') was found. Make sure you selected the GPUMON .csv, not a PicoLog or other export.");

        var utilizationCandidates = headers.Where(header => UtilizationNames.Contains(header.Name)).ToArray();
        var gpuIndex = utilizationCandidates.Select(header => header.GpuIndex).Where(index => index.HasValue).Min();

        var date = Resolve(headers, DateNames, null);
        var time = Resolve(headers, TimeNames, null);
        var utilization = Resolve(headers, UtilizationNames, gpuIndex)!;
        var gpuTemperature = Resolve(headers, GpuTemperatureNames, gpuIndex);
        var memoryTemperature = Resolve(headers, MemoryTemperatureNames, gpuIndex);
        var gpcClock = Resolve(headers, GpcClockNames, gpuIndex);
        var memoryClock = Resolve(headers, MemoryClockNames, gpuIndex);
        var tgp = Resolve(headers, TgpNames, gpuIndex);
        var pState = Resolve(headers, PStateNames, gpuIndex);
        var capping = Resolve(headers, CappingNames, gpuIndex);
        // GPUMon 6.x 'pwr_tgp' has no unit in its header but is logged in milliwatts.
        var tgpScale = tgp is not null && (tgp.Unit == "mw" || tgp.Name == "pwr tgp") ? 0.001m : 1m;

        var samples = new List<GpuMonSample>();
        DateTime? start = null;
        double previousElapsed = -1;
        for (var lineIndex = headerIndex + 1; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = SplitDelimitedLine(line, delimiter);
            var util = ParseDecimal(Cell(cells, utilization));
            if (!util.HasValue) continue;

            var timestamp = ParseTimestamp(Cell(cells, date), Cell(cells, time));
            start ??= timestamp;
            var elapsed = timestamp.HasValue && start.HasValue
                ? Math.Max(0, (timestamp.Value - start.Value).TotalSeconds)
                : previousElapsed + 1;
            previousElapsed = elapsed;

            var tgpValue = ParseDecimal(Cell(cells, tgp));
            samples.Add(new GpuMonSample(
                samples.Count,
                timestamp,
                elapsed,
                util.Value,
                ParseDecimal(Cell(cells, gpuTemperature)),
                ParseDecimal(Cell(cells, memoryTemperature)),
                tgpValue.HasValue ? tgpValue.Value * tgpScale : null,
                ParseDecimal(Cell(cells, gpcClock)),
                ParseDecimal(Cell(cells, memoryClock)),
                NormalizePState(Cell(cells, pState)),
                ParseCappingReasons(Cell(cells, capping))));
        }

        if (samples.Count == 0)
            throw new InvalidOperationException($"No data rows with a numeric '{utilization.Raw}' value were found.");

        var intervals = samples.Zip(samples.Skip(1), (left, right) => right.ElapsedSeconds - left.ElapsedSeconds)
            .Where(interval => interval > 0)
            .OrderBy(interval => interval)
            .ToArray();

        var metadata = ParseMetadata(lines, headerIndex);
        return new GpuMonLog
        {
            ToolVersion = metadata.GetValueOrDefault("tool") ?? "n/a",
            BrandName = metadata.GetValueOrDefault("brand") ?? "n/a",
            VbiosVersion = metadata.GetValueOrDefault("vbios") ?? "n/a",
            DriverVersion = metadata.GetValueOrDefault("driver") ?? "n/a",
            Columns = new GpuMonColumns(date?.Raw, time?.Raw, utilization.Raw, gpuTemperature?.Raw, memoryTemperature?.Raw,
                tgp?.Raw, gpcClock?.Raw, memoryClock?.Raw, pState?.Raw, capping?.Raw),
            Samples = samples,
            StartTime = start,
            SampleIntervalSeconds = intervals.Length == 0 ? 1d : intervals[intervals.Length / 2],
        };
    }

    public GpuMonAnalysis Analyze(GpuMonLog log, int utilizationMin, int utilizationMax, decimal expectedTgpWatts, decimal requiredTgpPercent)
    {
        var minimum = Math.Clamp(Math.Min(utilizationMin, utilizationMax), 0, 100);
        var maximum = Math.Clamp(Math.Max(utilizationMin, utilizationMax), 0, 100);
        var samples = log.Samples;
        var inRange = samples.Select(sample => sample.Utilization >= minimum && sample.Utilization <= maximum).ToArray();
        var inRangeSamples = samples.Where((_, index) => inRange[index]).ToArray();

        MetricStats Stats(Func<GpuMonSample, decimal?> select)
            => MetricStats.From(inRangeSamples.Select(select).Where(value => value.HasValue).Select(value => value!.Value).ToArray());

        var pStates = samples.Select(sample => sample.PState).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase).ToArray();
        var reasons = samples.SelectMany(sample => sample.CappingReasons).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(CappingReasonOrder).ThenBy(reason => reason, StringComparer.OrdinalIgnoreCase).ToArray();

        bool HasState(int index, string label) => string.Equals(samples[index].PState, label, StringComparison.OrdinalIgnoreCase);
        bool HasReason(int index, string reason) => samples[index].CappingReasons.Contains(reason, StringComparer.OrdinalIgnoreCase);

        var pStateTimeline = pStates.SelectMany(label => BuildSegments(log, label, index => HasState(index, label)))
            .OrderBy(segment => segment.StartSeconds).ToArray();
        var cappingTimeline = reasons.SelectMany(reason => BuildSegments(log, reason, index => HasReason(index, reason)))
            .OrderBy(segment => segment.StartSeconds).ThenBy(segment => CappingReasonOrder(segment.Label)).ToArray();
        var inRangeCapping = reasons.SelectMany(reason => BuildSegments(log, reason, index => inRange[index] && HasReason(index, reason)))
            .OrderBy(segment => segment.StartSeconds).ThenBy(segment => CappingReasonOrder(segment.Label)).ToArray();

        return new GpuMonAnalysis
        {
            Log = log,
            UtilizationMin = minimum,
            UtilizationMax = maximum,
            InRangeSamples = inRangeSamples,
            GpuTemperature = Stats(sample => sample.GpuTemperature),
            MemoryTemperature = Stats(sample => sample.MemoryTemperature),
            Tgp = Stats(sample => sample.TgpWatts),
            GpcClock = Stats(sample => sample.GpcClock),
            MemoryClock = Stats(sample => sample.MemoryClock),
            TgpCheck = EvaluateTgp(inRangeSamples, expectedTgpWatts, requiredTgpPercent),
            PStateTimeline = pStateTimeline,
            CappingTimeline = cappingTimeline,
            InRangeWindows = BuildSegments(log, "In range", index => inRange[index]),
            InRangeCappingSegments = inRangeCapping,
            PStateSummary = pStates.Select(label => Summarize(log, label, index => HasState(index, label), inRange)).ToArray(),
            CappingSummary = reasons.Select(reason => Summarize(log, reason, index => HasReason(index, reason), inRange)).ToArray(),
        };
    }

    /// <summary>TGP PASS/FAIL over the in-range samples that have a numeric TGP value.</summary>
    public static TgpCheckResult EvaluateTgp(IEnumerable<GpuMonSample> inRangeSamples, decimal expectedWatts, decimal requiredPercent)
    {
        var values = inRangeSamples.Where(sample => sample.TgpWatts.HasValue).Select(sample => sample.TgpWatts!.Value)
            .OrderByDescending(value => value).ToArray();
        var result = new TgpCheckResult(expectedWatts, requiredPercent, values.Count(value => value >= expectedWatts), values.Length);
        return result.RequiredSamples > 0 ? result with { PassingTgpWatts = values[result.RequiredSamples - 1] } : result;
    }

    public static string CappingReasonDisplay(string reason) => reason.ToLowerInvariant() switch
    {
        "pwr" => "Power",
        "thml" => "Thermal",
        "vrel" => "VRel",
        "vop" => "VOp",
        "util" => "Utilization",
        _ => reason
    };

    public static int CappingReasonOrder(string reason) => reason.ToLowerInvariant() switch
    {
        "pwr" => 0,
        "thml" => 1,
        "vrel" => 2,
        "vop" => 3,
        "util" => 4,
        _ => 100
    };

    private static ActivitySummary Summarize(GpuMonLog log, string label, Func<int, bool> active, bool[] inRange)
    {
        var total = 0;
        var inRangeCount = 0;
        for (var index = 0; index < log.Samples.Count; index++)
        {
            if (!active(index)) continue;
            total++;
            if (inRange[index]) inRangeCount++;
        }
        var periods = BuildSegments(log, label, index => inRange[index] && active(index)).Count;
        return new ActivitySummary(label, inRangeCount, periods, total);
    }

    private static IReadOnlyList<ActivitySegment> BuildSegments(GpuMonLog log, string label, Func<int, bool> active)
    {
        var samples = log.Samples;
        var segments = new List<ActivitySegment>();
        int? startIndex = null;
        for (var index = 0; index <= samples.Count; index++)
        {
            var isActive = index < samples.Count && active(index);
            if (isActive && !startIndex.HasValue) startIndex = index;
            if (isActive || !startIndex.HasValue) continue;

            var startSeconds = samples[startIndex.Value].ElapsedSeconds;
            var endSeconds = index < samples.Count ? samples[index].ElapsedSeconds : samples[^1].ElapsedSeconds + log.SampleIntervalSeconds;
            if (endSeconds <= startSeconds) endSeconds = startSeconds + 0.001;
            segments.Add(new ActivitySegment(label, startSeconds, endSeconds, ClockLabel(log, startSeconds), ClockLabel(log, endSeconds), endSeconds - startSeconds));
            startIndex = null;
        }
        return segments;
    }

    private static string ClockLabel(GpuMonLog log, double seconds)
        => log.StartTime.HasValue
            ? log.StartTime.Value.AddSeconds(seconds).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
            : $"+{TimeSpan.FromSeconds(seconds):mm\\:ss}";

    private static (int Index, char Delimiter, HeaderInfo[] Headers) FindHeader(string[] lines)
    {
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || (!line.Contains(',') && !line.Contains('\t'))) continue;
            var delimiter = line.Contains('\t') ? '\t' : ',';
            var headers = SplitDelimitedLine(line, delimiter).Select(ParseHeader).ToArray();
            if (headers.Any(header => DateNames.Contains(header.Name)) && headers.Any(header => UtilizationNames.Contains(header.Name)))
                return (index, delimiter, headers);
        }
        return (-1, ',', []);
    }

    private static HeaderInfo ParseHeader(string raw, int index)
    {
        var value = raw.Trim().Trim('"').Trim();
        var display = value;
        int? gpuIndex = null;
        var prefix = Regex.Match(value, @"^(\d+)\s*:\s*(.*)$");
        if (prefix.Success)
        {
            gpuIndex = int.Parse(prefix.Groups[1].Value, CultureInfo.InvariantCulture);
            value = prefix.Groups[2].Value;
        }

        var unit = string.Empty;
        var unitMatch = Regex.Match(value, @"\(([^)]*)\)\s*$");
        if (unitMatch.Success)
        {
            unit = unitMatch.Groups[1].Value.Trim().ToLowerInvariant();
            value = value[..unitMatch.Index];
        }

        var name = Regex.Replace(value.ToLowerInvariant(), @"[\s_\-]+", " ").Trim();
        return new HeaderInfo(index, display, gpuIndex, name, unit);
    }

    private static HeaderInfo? Resolve(IReadOnlyList<HeaderInfo> headers, string[] names, int? gpuIndex)
    {
        foreach (var name in names)
        {
            var matches = headers.Where(header => header.Name == name).ToArray();
            var match = matches.FirstOrDefault(header => header.GpuIndex == gpuIndex)
                        ?? matches.FirstOrDefault(header => header.GpuIndex is null);
            if (match is not null) return match;
        }
        return null;
    }

    private static Dictionary<string, string> ParseMetadata(string[] lines, int headerIndex)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Set(string key, string? value)
        {
            value = value?.Trim();
            if (!string.IsNullOrWhiteSpace(value) && !value.Equals("n/a", StringComparison.OrdinalIgnoreCase) && !result.ContainsKey(key))
                result[key] = value;
        }

        // GPUMon 7.x: "*** Section ***" followed by a header row and a value row.
        Dictionary<string, string> Section(string title)
        {
            var values = new Dictionary<string, string>();
            var start = Array.FindIndex(lines, 0, headerIndex, line => line.Contains(title, StringComparison.OrdinalIgnoreCase) && line.TrimStart().StartsWith("***"));
            if (start < 0) return values;
            var rows = lines.Skip(start + 1).Take(headerIndex - start - 1)
                .TakeWhile(line => !line.TrimStart().StartsWith("***"))
                .Where(line => !string.IsNullOrWhiteSpace(line.Trim(',')))
                .Take(2)
                .ToArray();
            if (rows.Length < 2) return values;
            var keys = SplitDelimitedLine(rows[0], ',').Select(ParseHeader).ToArray();
            var cells = SplitDelimitedLine(rows[1], ',');
            for (var index = 0; index < Math.Min(keys.Length, cells.Length); index++)
                values.TryAdd(keys[index].Name, cells[index].Trim());
            return values;
        }

        var tool = Section("Tool Information");
        var gpu = Section("GPU Information");
        Set("tool", tool.GetValueOrDefault("tool version"));
        Set("brand", gpu.GetValueOrDefault("brand name"));
        Set("vbios", gpu.GetValueOrDefault("vbios version"));
        Set("driver", gpu.GetValueOrDefault("driver version"));

        // GPUMon 6.x: "Tool Version: GPUMon v6.5", "GPU#1 - <brand>", "- VBIOS: <version>", "- Driver: <version>".
        foreach (var raw in lines.Take(headerIndex))
        {
            var line = raw.Trim().TrimEnd(',').Trim();
            var match = Regex.Match(line, @"^Tool Version\s*:\s*(.+)$", RegexOptions.IgnoreCase);
            if (match.Success) Set("tool", match.Groups[1].Value);
            match = Regex.Match(line, @"^GPU#\d+\s*-\s*(.+)$", RegexOptions.IgnoreCase);
            if (match.Success) Set("brand", match.Groups[1].Value);
            match = Regex.Match(line, @"^-\s*VBIOS\s*:\s*(.+)$", RegexOptions.IgnoreCase);
            if (match.Success) Set("vbios", match.Groups[1].Value);
            match = Regex.Match(line, @"^-\s*Driver\s*:\s*(.+)$", RegexOptions.IgnoreCase);
            if (match.Success) Set("driver", match.Groups[1].Value);
        }
        return result;
    }

    private static string Cell(string[] cells, HeaderInfo? header)
        => header is not null && header.Index < cells.Length ? cells[header.Index].Trim().Trim('"').Trim() : string.Empty;

    private static decimal? ParseDecimal(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static DateTime? ParseTimestamp(string date, string time)
    {
        if (string.IsNullOrWhiteSpace(time)) return null;
        var timeMatch = TimePattern.Match(time);
        if (timeMatch.Success)
        {
            var day = DateTime.MinValue.Date;
            if (!string.IsNullOrWhiteSpace(date))
            {
                if (!DateTime.TryParseExact(date, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out day) &&
                    !DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
                    return null;
            }
            var fraction = timeMatch.Groups[4].Success ? timeMatch.Groups[4].Value : "0";
            var milliseconds = int.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
            return day.Date.Add(new TimeSpan(0,
                int.Parse(timeMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(timeMatch.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(timeMatch.Groups[3].Value, CultureInfo.InvariantCulture),
                milliseconds));
        }

        var combined = string.IsNullOrWhiteSpace(date) ? time : $"{date} {time}";
        return DateTime.TryParse(combined, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ? parsed : null;
    }

    private static string NormalizePState(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("n/a", StringComparison.OrdinalIgnoreCase)) return "Unknown";
        value = value.Trim().ToUpperInvariant();
        return value.All(char.IsDigit) ? $"P{value}" : value;
    }

    private static IReadOnlyList<string> ParseCappingReasons(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split([' ', ',', ';', '|', '+', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(reason => !reason.Equals("none", StringComparison.OrdinalIgnoreCase) && !reason.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            .Select(reason => reason.ToLowerInvariant() switch
            {
                "pwr" or "power" => "Pwr",
                "thml" or "thermal" => "Thml",
                "vrel" => "VRel",
                "vop" => "VOp",
                "util" or "utilization" => "Util",
                _ => reason
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

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
}
