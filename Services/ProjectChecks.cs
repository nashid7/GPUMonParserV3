using System.Globalization;

namespace par.Services;

public sealed record CheckResult(string Name, string Detail, bool? Passed);

/// <summary>Overall PASS/FAIL combining the TGP check with the optional project limits.</summary>
public sealed class ProjectVerdict
{
    public required IReadOnlyList<CheckResult> Checks { get; init; }

    /// <summary>False if any check failed; true if every check that could be evaluated passed; null if none could be evaluated.</summary>
    public bool? Passed => Checks.Any(check => check.Passed == false) ? false
        : Checks.Any(check => check.Passed == true) ? true : null;

    public string Verdict => Passed switch { true => "PASS", false => "FAIL", _ => "N/A" };
    public IEnumerable<string> FailedChecks => Checks.Where(check => check.Passed == false).Select(check => check.Name);

    /// <summary>True when there is more than the TGP check to show.</summary>
    public bool HasLimitChecks => Checks.Count > 1;
}

public static class ProjectChecks
{
    public static ProjectVerdict Evaluate(GpuMonAnalysis analysis, ThermocoupleResult? thermocouples, AnalysisSettings settings)
    {
        var tgp = analysis.TgpCheck;
        var checks = new List<CheckResult>
        {
            new("TGP", $"{Pct(tgp.PercentAtOrAbove)} ≥ {Fmt(tgp.ExpectedWatts)} W (need {Fmt(tgp.RequiredPercent)}%)", tgp.Passed),
        };
        if (settings.GpuTempAvgLimit is { } gpuLimit)
            checks.Add(Limit("GPU temp avg", analysis.GpuTemperature.Average, gpuLimit, "no GPU temperature"));
        if (settings.MemTempMaxLimit is { } memLimit)
            checks.Add(Limit("Memory temp max", analysis.MemoryTemperature.Maximum, memLimit, "no memory temperature"));
        if (settings.TcAvgLimit is { } tcLimit)
            checks.Add(Limit("TC avg", thermocouples?.TcAverage.Average, tcLimit, thermocouples is null ? "no PicoLog loaded" : "no matching PicoLog readings"));
        return new ProjectVerdict { Checks = checks };
    }

    private static CheckResult Limit(string name, decimal? value, decimal limit, string missing)
        => value is { } actual
            ? new CheckResult(name, $"{Fmt(actual)} ≤ {Fmt(limit)} °C", actual <= limit)
            : new CheckResult(name, $"{missing} (limit {Fmt(limit)} °C)", null);

    // Round half away from zero so displayed values match Excel (e.g. 83.195 -> 83.2).
    public static string Fmt(decimal? value) => value.HasValue ? Math.Round(value.Value, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture) : "–";
    public static string Pct(decimal? value) => value.HasValue ? Math.Round(value.Value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture) + "%" : "n/a";
}
