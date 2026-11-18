using par.Services;
using static par.Tests.LogBuilder;

namespace par.Tests;

public class ProjectChecksTests
{
    private static readonly GpuMonAnalysis Passing = Analyze(Gpumon7(Steady(100, tgp: 75)));

    [Fact]
    public void Without_limits_only_the_tgp_check_is_reported()
    {
        var verdict = ProjectChecks.Evaluate(Passing, null, new AnalysisSettings());

        Assert.False(verdict.HasLimitChecks);
        Assert.Equal("PASS", verdict.Verdict);
    }

    [Fact]
    public void Any_failed_limit_fails_the_overall_result()
    {
        // Synthetic rows use GPU temp 80 °C and memory temp 85 °C.
        var settings = new AnalysisSettings { GpuTempAvgLimit = 80, MemTempMaxLimit = 84.9m };

        var verdict = ProjectChecks.Evaluate(Passing, null, settings);

        Assert.Equal("FAIL", verdict.Verdict);
        Assert.Equal(["Memory temp max"], verdict.FailedChecks);
        Assert.True(verdict.Checks.Single(check => check.Name == "GPU temp avg").Passed);
    }

    [Fact]
    public void A_tc_limit_without_a_picolog_is_not_evaluated_and_does_not_fail_the_result()
    {
        var verdict = ProjectChecks.Evaluate(Passing, null, new AnalysisSettings { TcAvgLimit = 70 });

        Assert.Null(verdict.Checks.Single(check => check.Name == "TC avg").Passed);
        Assert.Equal("PASS", verdict.Verdict);
    }

    [Fact]
    public void Tc_limit_uses_the_matched_tc_average()
    {
        var pico = PicoLogParser.Parse(PicoLog(Enumerable.Range(0, 100).Select(second => (second, new[] { 70m, 72m, 60m })), ["#1", "#2", "chamber"]));
        var tc = PicoLogParser.Match(pico, Passing.InRangeSamples);

        Assert.Equal("PASS", ProjectChecks.Evaluate(Passing, tc, new AnalysisSettings { TcAvgLimit = 71 }).Verdict);
        Assert.Equal("FAIL", ProjectChecks.Evaluate(Passing, tc, new AnalysisSettings { TcAvgLimit = 70.9m }).Verdict);
    }

    [Theory]
    [InlineData(83.195, "83.2")]
    [InlineData(1227.645, "1227.65")]
    [InlineData(70.004, "70")]
    public void Values_are_rounded_half_away_from_zero_like_excel(decimal value, string expected)
        => Assert.Equal(expected, ProjectChecks.Fmt(value));
}
