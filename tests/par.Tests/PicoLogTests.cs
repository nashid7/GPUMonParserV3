using par.Services;
using static par.Tests.LogBuilder;

namespace par.Tests;

public class PicoLogTests
{
    private static readonly string[] Channels = ["#1_Top_Left", "#3,Top_Right", "Chamber_Temp", "#2"];

    [Fact]
    public void Parses_channels_detects_the_chamber_and_skips_blank_rows()
    {
        var content = PicoLog([(0, [70m, 72m, 65m, 74m]), (1, [71m, 73m, 65m, 75m])], Channels);

        Assert.True(PicoLogParser.LooksLikePicoLog(content));
        var pico = PicoLogParser.Parse(content);

        Assert.Equal(["#1_Top_Left", "#3,Top_Right", "Chamber_Temp", "#2"], pico.Channels.Select(channel => channel.Name));
        Assert.Equal("Chamber_Temp", Assert.Single(pico.Channels, channel => channel.IsChamber).Name);
        Assert.Equal(2, pico.Readings.Count);
        Assert.Equal(Start, pico.Start);
    }

    [Fact]
    public void Gpumon_logs_are_not_detected_as_picolog()
        => Assert.False(PicoLogParser.LooksLikePicoLog(Gpumon7(Steady(3))));

    [Fact]
    public void Only_in_range_gpumon_seconds_are_matched_and_tcs_are_averaged_without_the_chamber()
    {
        // GPUMON: seconds 0-3, second 2 is below the utilization range. PicoLog covers seconds 0-3.
        var analysis = Analyze(Gpumon7([new Row(0, 100, 75), new Row(1, 100, 75), new Row(2, 50, 75), new Row(3, 100, 75)]));
        var pico = PicoLogParser.Parse(PicoLog(
        [
            (0, [70m, 72m, 60m, 74m]),
            (1, [71m, 73m, 60m, 75m]),
            (2, [99m, 99m, 99m, 99m]),
            (3, [72m, 74m, 61m, 76m]),
        ], Channels));

        var result = PicoLogParser.Match(pico, analysis.InRangeSamples);

        Assert.Equal(3, result.MatchedSamples);
        Assert.Equal(72m, result.TcAverage.Minimum);       // mean of 70, 72, 74
        Assert.Equal(73m, result.TcAverage.Average);       // mean of 72, 73, 74
        Assert.Equal(60.33m, Math.Round(result.Chamber!.Average!.Value, 2));
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Readings_more_than_one_second_away_are_not_matched()
    {
        var analysis = Analyze(Gpumon7(Steady(3)));
        // PicoLog starts 5 s after the GPUMON log ends.
        var pico = PicoLogParser.Parse(PicoLog([(8, [70m, 70m, 60m, 70m]), (9, [70m, 70m, 60m, 70m])], Channels));

        var result = PicoLogParser.Match(pico, analysis.InRangeSamples);

        Assert.Equal(0, result.MatchedSamples);
        Assert.Null(result.TcAverage.Average);
        Assert.Contains("does not overlap", result.Warning);
    }

    [Fact]
    public void Partial_overlap_is_reported()
    {
        var analysis = Analyze(Gpumon7(Steady(10)));
        var pico = PicoLogParser.Parse(PicoLog(Enumerable.Range(0, 5).Select(second => (second, new[] { 70m, 70m, 60m, 70m })), Channels));

        var result = PicoLogParser.Match(pico, analysis.InRangeSamples);

        Assert.Equal(5, result.MatchedSamples);
        Assert.Contains("Only 50%", result.Warning);
    }

    [Fact]
    public void Timezone_offset_is_ignored_because_gpumon_logs_local_time()
    {
        var analysis = Analyze(Gpumon7(Steady(2)));
        var pico = PicoLogParser.Parse(PicoLog([(0, [70m, 70m, 60m, 70m]), (1, [70m, 70m, 60m, 70m])], Channels, offset: "+09:00"));

        Assert.Equal(2, PicoLogParser.Match(pico, analysis.InRangeSamples).MatchedSamples);
    }
}
