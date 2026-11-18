using par.Services;
using static par.Tests.LogBuilder;

namespace par.Tests;

public class GpuMonLogAnalyzerTests
{
    [Fact]
    public void Columns_are_found_by_name_not_position_and_decoy_tgp_columns_are_ignored()
    {
        var rows = new[] { new Row(0, 100, 72, GpuTemp: 81, MemTemp: 86), new Row(1, 99, 74, GpuTemp: 83, MemTemp: 88) };
        var normal = Analyze(Gpumon7(rows));
        var shuffled = Analyze(Gpumon7(rows, shuffled: true));

        Assert.Equal("1:TGP (W)", shuffled.Log.Columns.Tgp);
        foreach (var analysis in new[] { normal, shuffled })
        {
            Assert.Equal(73m, analysis.Tgp.Average);
            Assert.Equal(82m, analysis.GpuTemperature.Average);
            Assert.Equal(88m, analysis.MemoryTemperature.Maximum);
        }
    }

    [Fact]
    public void Gpumon6_snake_case_columns_are_read_and_tgp_is_converted_from_milliwatts()
    {
        var analysis = Analyze(Gpumon6([new Row(0, 100, 79.5m), new Row(1, 100, 80.5m)]));

        Assert.Equal("1:pwr_tgp", analysis.Log.Columns.Tgp);
        Assert.Equal(80m, analysis.Tgp.Average);
        Assert.Equal("Test GPU 6", analysis.Log.BrandName);
        Assert.Equal("95.00.00.00.01", analysis.Log.VbiosVersion);
    }

    [Fact]
    public void Only_samples_inside_the_inclusive_utilization_range_are_used()
    {
        var rows = new[] { new Row(0, 98, 10), new Row(1, 99, 70), new Row(2, 100, 80), new Row(3, 50, 10) };

        var analysis = Analyze(Gpumon7(rows), 99, 100);

        Assert.Equal(2, analysis.InRangeSamples.Count);
        Assert.Equal(75m, analysis.Tgp.Average);
        Assert.Equal(70m, analysis.Tgp.Minimum);
    }

    [Theory]
    [InlineData(98, "PASS", 0)]
    [InlineData(97, "FAIL", 1)]
    public void Tgp_check_passes_when_the_required_share_of_samples_meets_the_expected_tgp(int samplesAtOrAbove, string verdict, int samplesShort)
    {
        // 100 samples: the first N at exactly 70 W (counts as >= 70), the rest at 69.9 W.
        var rows = Enumerable.Range(0, 100).Select(index => new Row(index, 100, index < samplesAtOrAbove ? 70 : 69.9m));

        var check = Analyze(Gpumon7(rows)).TgpCheck;

        Assert.Equal(verdict, check.Verdict);
        Assert.Equal(samplesAtOrAbove, check.SamplesAtOrAbove);
        Assert.Equal(samplesShort, check.SamplesShort);
    }

    [Fact]
    public void Passing_tgp_is_the_highest_expected_value_the_run_would_still_pass()
    {
        // 50 samples at 80 W and 50 at 60 W: 98% of samples are only guaranteed to reach 60 W.
        var rows = Enumerable.Range(0, 100).Select(index => new Row(index, 100, index < 50 ? 80 : 60));

        var check = Analyze(Gpumon7(rows)).TgpCheck;

        Assert.Equal(60m, check.PassingTgpWatts);
        Assert.Equal("FAIL", check.Verdict);
    }

    [Fact]
    public void A_file_without_a_gpumon_header_is_rejected_with_a_clear_message()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new GpuMonLogAnalyzer().Parse("\"\",\"#1 Last (C)\"\n\"2026-01-01T00:00:00-05:00\",\"1\""));

        Assert.Contains("doesn't look like a GPU Monitor", error.Message);
    }

    [Fact]
    public void Low_in_range_sample_count_produces_a_reliability_warning()
    {
        Assert.NotNull(Analyze(Gpumon7(Steady(20))).ReliabilityWarning);
        Assert.Null(Analyze(Gpumon7(Steady(GpuMonLogAnalyzer.MinimumReliableSamples))).ReliabilityWarning);
    }
}
