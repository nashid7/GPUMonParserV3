using par.Services;
using static par.Tests.LogBuilder;

namespace par.Tests;

public class TestTimelineTests
{
    [Fact]
    public void Load_period_spans_the_first_to_last_in_range_sample_and_reports_dips()
    {
        // 0-9 s idle, 10-69 s FurMark (util 100) with a 10 s dip at 30-39 s, 70-79 s idle.
        var rows = Enumerable.Range(0, 80).Select(second => new Row(second, second is >= 10 and < 70 and not (>= 30 and < 40) ? 100 : 5, 75));

        var timeline = TestTimeline.From(Analyze(Gpumon7(rows)), null);

        var ms = TimeSpan.FromMilliseconds(250);
        Assert.Equal(Start + ms, timeline.LogStart);
        Assert.Equal(Start.AddSeconds(79) + ms, timeline.LogEnd);
        Assert.Equal(Start.AddSeconds(10) + ms, timeline.LoadStart);
        Assert.Equal(Start.AddSeconds(69) + ms, timeline.LoadEnd);
        Assert.Equal(TimeSpan.FromSeconds(50), timeline.TimeInRange);
        Assert.Equal(2, timeline.LoadPeriods);
        Assert.True(timeline.HasDips);
        Assert.Equal("09:00:10 – 09:01:09 (0:59, 0:50 in range over 2 periods)", timeline.LoadText);
    }

    [Fact]
    public void Continuous_load_has_no_dips()
    {
        var timeline = TestTimeline.From(Analyze(Gpumon7(Steady(120))), null);

        Assert.Equal(1, timeline.LoadPeriods);
        Assert.False(timeline.HasDips);
        Assert.Equal("1:59", TestTimeline.Span(timeline.LoadDuration));
    }
}
