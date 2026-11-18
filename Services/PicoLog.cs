using System.Globalization;
using System.Text.RegularExpressions;

namespace par.Services;

public sealed record PicoChannel(int Column, string Name, bool IsChamber);

public sealed record PicoReading(DateTime Time, decimal?[] Values);

/// <summary>A PicoLog thermocouple export: an ISO timestamp column followed by one column per channel.</summary>
public sealed class PicoLog
{
    public required IReadOnlyList<PicoChannel> Channels { get; init; }
    public required IReadOnlyList<PicoReading> Readings { get; init; }
    public DateTime Start => Readings[0].Time;
    public DateTime End => Readings[^1].Time;
    public IEnumerable<PicoChannel> Thermocouples => Channels.Where(channel => !channel.IsChamber);
    public PicoChannel? Chamber => Channels.FirstOrDefault(channel => channel.IsChamber);
}

public sealed record ChannelStats(string Name, bool IsChamber, MetricStats Stats);

/// <summary>PicoLog readings matched to the in-range GPUMON samples.</summary>
public sealed class ThermocoupleResult
{
    public required PicoLog Log { get; init; }
    public required IReadOnlyList<ChannelStats> Channels { get; init; }

    /// <summary>Per matched second, the mean of the available thermocouples (chamber excluded).</summary>
    public required MetricStats TcAverage { get; init; }
    public MetricStats? Chamber => Channels.FirstOrDefault(channel => channel.IsChamber)?.Stats;
    public int MatchedSamples { get; init; }
    public int InRangeSamples { get; init; }
    public decimal CoveragePercent => InRangeSamples == 0 ? 0 : MatchedSamples * 100m / InRangeSamples;

    /// <summary>Matched reading for each in-range GPUMON sample (null where nothing matched), aligned with the in-range samples.</summary>
    public required IReadOnlyList<PicoReading?> Aligned { get; init; }
    public string? Warning { get; init; }
}

public static class PicoLogParser
{
    // Within this distance a PicoLog reading counts as the same moment as a GPUMON sample.
    public static readonly TimeSpan MatchTolerance = TimeSpan.FromSeconds(1);
    private const decimal PartialCoverageWarningPercent = 90m;
    private static readonly Regex UnitSuffix = new(@"\s*(Last|Average|Avg|Min|Max)?\s*\((°?C)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns true when the content looks like a PicoLog export (first data cell is an ISO timestamp).</summary>
    public static bool LooksLikePicoLog(string content)
    {
        var lines = content.Split(["\r\n", "\n", "\r"], 3, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return false;
        var firstCell = GpuMonLogAnalyzer.SplitDelimitedLine(lines[1], ',')[0].Trim();
        return firstCell.Contains('T') && DateTimeOffset.TryParse(firstCell, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    public static PicoLog Parse(string content)
    {
        var lines = content.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || !LooksLikePicoLog(content))
            throw new InvalidOperationException("This doesn't look like a PicoLog export: expected a timestamp column followed by thermocouple columns.");

        var headers = GpuMonLogAnalyzer.SplitDelimitedLine(lines[0], ',');
        var channels = new List<PicoChannel>();
        for (var column = 1; column < headers.Length; column++)
        {
            var name = UnitSuffix.Replace(headers[column].Trim().Trim('"'), "").Trim();
            if (name.Length == 0) continue;
            channels.Add(new PicoChannel(column, name, name.Contains("chamber", StringComparison.OrdinalIgnoreCase)));
        }
        if (channels.Count == 0) throw new InvalidOperationException("The PicoLog file has no channel columns.");

        var readings = new List<PicoReading>();
        foreach (var line in lines.Skip(1))
        {
            var cells = GpuMonLogAnalyzer.SplitDelimitedLine(line, ',');
            // PicoLog timestamps carry a UTC offset; GPUMON logs local wall-clock time, so the offset is dropped.
            if (!DateTimeOffset.TryParse(cells[0].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
            var values = channels.Select(channel => channel.Column < cells.Length &&
                    decimal.TryParse(cells[channel.Column].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value : (decimal?)null).ToArray();
            if (values.All(value => value is null)) continue;
            readings.Add(new PicoReading(time.DateTime, values));
        }
        if (readings.Count == 0) throw new InvalidOperationException("The PicoLog file has no temperature readings.");
        readings.Sort((left, right) => left.Time.CompareTo(right.Time));
        return new PicoLog { Channels = channels, Readings = readings };
    }

    /// <summary>
    /// Matches each in-range GPUMON sample to the PicoLog reading closest in time (within <see cref="MatchTolerance"/>)
    /// and averages the thermocouples over the matched readings only.
    /// </summary>
    public static ThermocoupleResult Match(PicoLog pico, IReadOnlyList<GpuMonSample> inRangeSamples)
    {
        var aligned = new PicoReading?[inRangeSamples.Count];
        var matched = 0;
        for (var index = 0; index < inRangeSamples.Count; index++)
        {
            if (inRangeSamples[index].Timestamp is not { } time) continue;
            var reading = Nearest(pico.Readings, time);
            if (reading is null) continue;
            aligned[index] = reading;
            matched++;
        }

        var matchedReadings = aligned.Where(reading => reading is not null).Select(reading => reading!).ToArray();
        var channels = pico.Channels.Select((channel, channelIndex) => new ChannelStats(channel.Name, channel.IsChamber,
            MetricStats.From(matchedReadings.Select(reading => reading.Values[channelIndex]).Where(value => value.HasValue).Select(value => value!.Value).ToArray()))).ToArray();
        var tcIndexes = pico.Channels.Select((channel, index) => (channel, index)).Where(item => !item.channel.IsChamber).Select(item => item.index).ToArray();

        return new ThermocoupleResult
        {
            Log = pico,
            Channels = channels,
            TcAverage = MetricStats.From(matchedReadings.Select(reading => TcMean(reading, tcIndexes)).Where(value => value.HasValue).Select(value => value!.Value).ToArray()),
            MatchedSamples = matched,
            InRangeSamples = inRangeSamples.Count,
            Aligned = aligned,
            Warning = BuildWarning(pico, inRangeSamples, matched),
        };
    }

    /// <summary>Mean of the thermocouple values available in one reading (chamber excluded).</summary>
    public static decimal? TcMean(PicoReading reading, IReadOnlyList<int> tcIndexes)
    {
        var values = tcIndexes.Select(index => reading.Values[index]).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static PicoReading? Nearest(IReadOnlyList<PicoReading> readings, DateTime time)
    {
        int low = 0, high = readings.Count - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (readings[middle].Time < time) low = middle + 1;
            else high = middle;
        }
        var best = readings[low];
        if (low > 0 && (time - readings[low - 1].Time).Duration() < (best.Time - time).Duration()) best = readings[low - 1];
        return (best.Time - time).Duration() <= MatchTolerance ? best : null;
    }

    private static string? BuildWarning(PicoLog pico, IReadOnlyList<GpuMonSample> inRangeSamples, int matched)
    {
        if (inRangeSamples.Count == 0) return null;
        var times = inRangeSamples.Where(sample => sample.Timestamp.HasValue).Select(sample => sample.Timestamp!.Value).ToArray();
        var gpuRange = times.Length == 0 ? "no timestamps" : $"{times.Min():yyyy-MM-dd HH:mm}–{times.Max():HH:mm}";
        var picoRange = $"{pico.Start:yyyy-MM-dd HH:mm}–{pico.End:HH:mm}";
        if (matched == 0)
            return $"The PicoLog does not overlap the in-range GPUMON samples (PicoLog {picoRange}, GPUMON {gpuRange}). Check that the logs are from the same test.";
        var coverage = matched * 100m / inRangeSamples.Count;
        return coverage < PartialCoverageWarningPercent
            ? $"Only {coverage.ToString("0.#", CultureInfo.InvariantCulture)}% of the in-range GPUMON samples have a PicoLog reading (PicoLog {picoRange}, GPUMON {gpuRange})."
            : null;
    }
}
