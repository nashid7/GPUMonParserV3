using System.Globalization;

namespace par.Services;

/// <summary>
/// When the GPUMON log was recorded, when the GPU was under load (utilization inside the selected range, e.g. FurMark
/// running) and what the PicoLog covered, so every result can be traced back to the test timeline.
/// </summary>
public sealed class TestTimeline
{
    public DateTime? LogStart { get; private init; }
    public DateTime? LogEnd { get; private init; }
    public DateTime? LoadStart { get; private init; }
    public DateTime? LoadEnd { get; private init; }

    /// <summary>Total time with utilization inside the range; less than the load span when utilization dipped.</summary>
    public TimeSpan TimeInRange { get; private init; }

    /// <summary>Number of continuous periods with utilization inside the range.</summary>
    public int LoadPeriods { get; private init; }
    public string UtilizationRange { get; private init; } = "";
    public DateTime? PicoStart { get; private init; }
    public DateTime? PicoEnd { get; private init; }

    /// <summary>Load periods and PicoLog coverage as fractions (0–1) of the GPUMON log span, for drawing a bar.</summary>
    public IReadOnlyList<(double Start, double End)> LoadBars { get; private init; } = [];
    public (double Start, double End)? PicoBar { get; private init; }

    public TimeSpan? LogDuration => LogEnd - LogStart;
    public TimeSpan? LoadDuration => LoadEnd - LoadStart;
    public bool HasDips => LoadDuration is { } span && span - TimeInRange > TimeSpan.FromSeconds(Math.Max(5, LoadPeriods));

    public static TestTimeline From(GpuMonAnalysis analysis, ThermocoupleResult? thermocouples)
    {
        var log = analysis.Log;
        var logEnd = log.Samples.Select(sample => sample.Timestamp).LastOrDefault(time => time.HasValue);
        var total = Math.Max(log.DurationSeconds, 1);
        (double, double) Bar(double start, double end) => (Math.Clamp(start / total, 0, 1), Math.Clamp(end / total, 0, 1));

        (double, double)? picoBar = null;
        if (thermocouples is not null && log.StartTime is { } logStart)
        {
            var bar = Bar((thermocouples.Log.Start - logStart).TotalSeconds, (thermocouples.Log.End - logStart).TotalSeconds);
            if (bar.Item2 > bar.Item1) picoBar = bar;
        }

        return new TestTimeline
        {
            LogStart = log.StartTime,
            LogEnd = logEnd,
            LoadStart = analysis.FirstInRangeTimestamp,
            LoadEnd = analysis.LastInRangeTimestamp,
            TimeInRange = TimeSpan.FromSeconds(analysis.InRangeWindows.Sum(window => window.DurationSeconds)),
            LoadPeriods = analysis.InRangeWindows.Count,
            UtilizationRange = analysis.UtilizationRange,
            PicoStart = thermocouples?.Log.Start,
            PicoEnd = thermocouples?.Log.End,
            LoadBars = analysis.InRangeWindows.Select(window => Bar(window.StartSeconds, window.EndSeconds)).ToArray(),
            PicoBar = picoBar,
        };
    }

    public static string Clock(DateTime? time) => time?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "–";

    public static string Span(TimeSpan? span) => span is not { } value ? "–"
        : value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    public static string Range(DateTime? start, DateTime? end) => $"{Clock(start)} – {Clock(end)}";

    /// <summary>One-line plain-text description for summaries, PDF and CSV.</summary>
    public string LoadText => LoadStart is null ? "no samples in range"
        : $"{Range(LoadStart, LoadEnd)} ({Span(LoadDuration)}" + (HasDips ? $", {Span(TimeInRange)} in range over {LoadPeriods} periods)" : ")");
}
