using System.Globalization;
using System.Text;
using par.Services;

namespace par.Tests;

/// <summary>Builds small synthetic GPUMON and PicoLog files so tests never depend on real (internal) logs.</summary>
internal static class LogBuilder
{
    public sealed record Row(int Seconds, decimal Util, decimal Tgp, decimal GpuTemp = 80, decimal MemTemp = 85, decimal GpcClock = 1500, decimal MemClock = 9001);

    public static readonly DateTime Start = new(2026, 10, 22, 9, 0, 0);

    /// <summary>GPUMon 7.x layout. Extra columns (including names that contain "TGP") can shift the real columns.</summary>
    public static string Gpumon7(IEnumerable<Row> rows, bool shuffled = false)
    {
        var columns = shuffled
            ? new[] { "Iteration", "Date", "Timestamp", "1:AC Configurable TGP Offset (mW)", "1:FB Utilization (%)", "1:TGP (W)", "1:Memory Clock (MHz)",
                      "1:GPU Utilization (%)", "1:TPP Measured (W)", "1:Temperature Memory (C)", "1:GPC Clock (MHz)", "1:Temperature GPU (C)" }
            : new[] { "Iteration", "Date", "Timestamp", "1:Temperature GPU (C)", "1:Temperature Memory (C)", "1:GPC Clock (MHz)", "1:Memory Clock (MHz)",
                      "1:GPU Utilization (%)", "1:FB Utilization (%)", "1:TGP (W)" };
        var sb = new StringBuilder();
        sb.AppendLine("*** Tool Informations ***").AppendLine("Tool Version, Operating system").AppendLine("7.3, Windows").AppendLine();
        sb.AppendLine("*** GPU Informations ***").AppendLine("GPU Index Number, Brand Name, VBIOS Version, Driver Version").AppendLine("1, Test GPU, 98.00.00.00.01, 600.01").AppendLine();
        sb.AppendLine(string.Join(", ", columns));
        var iteration = 0;
        foreach (var row in rows)
        {
            var time = Start.AddSeconds(row.Seconds);
            var values = new Dictionary<string, string>
            {
                ["Iteration"] = (iteration++).ToString(CultureInfo.InvariantCulture),
                ["Date"] = time.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
                ["Timestamp"] = time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ":250",
                ["1:Temperature GPU (C)"] = Num(row.GpuTemp),
                ["1:Temperature Memory (C)"] = Num(row.MemTemp),
                ["1:GPC Clock (MHz)"] = Num(row.GpcClock),
                ["1:Memory Clock (MHz)"] = Num(row.MemClock),
                ["1:GPU Utilization (%)"] = Num(row.Util),
                ["1:FB Utilization (%)"] = "100",
                ["1:TGP (W)"] = Num(row.Tgp),
                ["1:AC Configurable TGP Offset (mW)"] = "150000",
                ["1:TPP Measured (W)"] = "999",
            };
            sb.AppendLine(string.Join(", ", columns.Select(column => values[column])));
        }
        return sb.ToString();
    }

    /// <summary>GPUMon 6.x layout: snake_case names and TGP in milliwatts.</summary>
    public static string Gpumon6(IEnumerable<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tool Version: GPUMon v6.5").AppendLine("GPU#1 - Test GPU 6").AppendLine("      - VBIOS: 95.00.00.00.01").AppendLine("      - Driver: 581.42");
        sb.AppendLine("date,time,1:TPP_Measured,1:AC_Baseline_TGP_mode_2,1:t_gpu,1:mem_temp,1:gpc_clk,1:clk_mem,1:gpc_util,1:p_state,1:capping_reason,1:pwr_tgp");
        foreach (var row in rows)
        {
            var time = Start.AddSeconds(row.Seconds);
            sb.AppendLine(string.Join(",", time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                "999", "0", Num(row.GpuTemp), Num(row.MemTemp), Num(row.GpcClock), Num(row.MemClock), Num(row.Util), "3", "Pwr ", Num(row.Tgp * 1000)));
        }
        return sb.ToString();
    }

    /// <summary>PicoLog export with ISO timestamps (offset), a channel name containing a comma, and a trailing blank row.</summary>
    public static string PicoLog(IEnumerable<(int Seconds, decimal[] Values)> rows, string[] channels, string offset = "-04:00")
    {
        var sb = new StringBuilder();
        sb.AppendLine("\"\"," + string.Join(",", channels.Select(channel => $"\"{channel} Last (C)\"")));
        var last = 0;
        foreach (var (seconds, values) in rows)
        {
            last = seconds;
            sb.AppendLine($"\"{Start.AddSeconds(seconds):yyyy-MM-ddTHH:mm:ss}{offset}\"," + string.Join(",", values.Select(value => $"\"{Num(value)}\"")));
        }
        sb.AppendLine($"\"{Start.AddSeconds(last + 1):yyyy-MM-ddTHH:mm:ss}{offset}\"," + string.Join(",", channels.Select(_ => "\"\"")));
        return sb.ToString();
    }

    public static IEnumerable<Row> Steady(int count, decimal util = 100, decimal tgp = 75, int startSecond = 0)
        => Enumerable.Range(0, count).Select(index => new Row(startSecond + index, util, tgp));

    private static string Num(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static GpuMonAnalysis Analyze(string content, int min = 99, int max = 100, decimal expected = 70, decimal required = 98)
    {
        var analyzer = new GpuMonLogAnalyzer();
        return analyzer.Analyze(analyzer.Parse(content), min, max, expected, required);
    }
}
