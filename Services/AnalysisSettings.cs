namespace par.Services;

/// <summary>User-selected analysis settings shared by the Analyze, Compare and Batch pages.</summary>
public sealed class AnalysisSettings
{
    public int UtilizationMin { get; set; } = 99;
    public int UtilizationMax { get; set; } = 100;
    public decimal ExpectedTgpWatts { get; set; } = GpuMonLogAnalyzer.DefaultExpectedTgpWatts;
    public decimal RequiredTgpPercent { get; set; } = GpuMonLogAnalyzer.DefaultRequiredTgpPercent;

    // Optional project limits; null means the check is not run.
    public decimal? GpuTempAvgLimit { get; set; }
    public decimal? MemTempMaxLimit { get; set; }
    public decimal? TcAvgLimit { get; set; }

    public bool HasLimits => GpuTempAvgLimit.HasValue || MemTempMaxLimit.HasValue || TcAvgLimit.HasValue;

    /// <summary>Name of the preset the values came from, if they still match it.</summary>
    public string? PresetName { get; set; }

    public string UtilizationRange => $"{Math.Clamp(Math.Min(UtilizationMin, UtilizationMax), 0, 100)}-{Math.Clamp(Math.Max(UtilizationMin, UtilizationMax), 0, 100)}%";

    public bool Matches(AnalysisPreset preset)
        => preset.UtilizationMin == UtilizationMin && preset.UtilizationMax == UtilizationMax &&
           preset.ExpectedTgpWatts == ExpectedTgpWatts && preset.RequiredTgpPercent == RequiredTgpPercent &&
           preset.GpuTempAvgLimit == GpuTempAvgLimit && preset.MemTempMaxLimit == MemTempMaxLimit && preset.TcAvgLimit == TcAvgLimit;

    public void Apply(AnalysisPreset preset)
    {
        UtilizationMin = preset.UtilizationMin;
        UtilizationMax = preset.UtilizationMax;
        ExpectedTgpWatts = preset.ExpectedTgpWatts;
        RequiredTgpPercent = preset.RequiredTgpPercent;
        GpuTempAvgLimit = preset.GpuTempAvgLimit;
        MemTempMaxLimit = preset.MemTempMaxLimit;
        TcAvgLimit = preset.TcAvgLimit;
        PresetName = preset.Name;
    }
}

/// <summary>A saved set of analysis settings, optionally applied automatically to files whose name starts with <see cref="FilePrefix"/>.</summary>
public sealed record AnalysisPreset(
    string Name,
    string? FilePrefix,
    int UtilizationMin,
    int UtilizationMax,
    decimal ExpectedTgpWatts,
    decimal RequiredTgpPercent,
    bool BuiltIn = false,
    decimal? GpuTempAvgLimit = null,
    decimal? MemTempMaxLimit = null,
    decimal? TcAvgLimit = null)
{
    /// <summary>Analysis settings equivalent to this preset.</summary>
    public AnalysisSettings ToSettings()
    {
        var settings = new AnalysisSettings();
        settings.Apply(this);
        return settings;
    }
}

public enum SettingsChange
{
    Range,
    Tgp,
    Limits,
}
