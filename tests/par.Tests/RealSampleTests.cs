using par.Services;

namespace par.Tests;

/// <summary>
/// Checks results on the real GPUMON sample logs against independently calculated values.
/// The samples are internal and only exist in the company repository, so these tests do nothing when the folder is absent.
/// </summary>
public class RealSampleTests
{
    private static string? SamplesFolder()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "samples");
            if (File.Exists(Path.Combine(candidate, "163L010051-gpumon-retest.csv"))) return candidate;
        }
        return null;
    }

    [Theory]
    // file, utilization min, samples >= 70 W, in-range TGP samples, TGP average, passing TGP
    [InlineData("163L010051-gpumon-retest.csv", 99, 1128, 1129, 73.85, 70.86)]
    [InlineData("163L010051-gpumon-retest.csv", 90, 1201, 1202, 73.89, 70.86)]
    [InlineData("1539010011_GPU-MON_HOT.csv", 99, 816, 816, 79.78, 79.07)]
    [InlineData("1538010114_July_22_2026-gpumonhot.csv", 99, 831, 832, 78.45, 74.29)]
    public void Results_match_independent_calculation(string file, int utilizationMin, int atOrAbove, int samples, decimal tgpAverage, decimal passingTgp)
    {
        if (SamplesFolder() is not { } folder) return;
        var analyzer = new GpuMonLogAnalyzer();

        var analysis = analyzer.Analyze(analyzer.Parse(File.ReadAllText(Path.Combine(folder, file))), utilizationMin, 100, 70, 98);

        Assert.Equal(atOrAbove, analysis.TgpCheck.SamplesAtOrAbove);
        Assert.Equal(samples, analysis.TgpCheck.SampleCount);
        Assert.Equal(tgpAverage, Math.Round(analysis.Tgp.Average!.Value, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(passingTgp, Math.Round(analysis.TgpCheck.PassingTgpWatts!.Value, 2, MidpointRounding.AwayFromZero));
        Assert.Equal("PASS", analysis.TgpCheck.Verdict);
    }
}
