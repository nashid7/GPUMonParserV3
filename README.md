# GPU Monitor Log Parser

A .NET 9 Blazor application for analyzing NVIDIA GPU Monitor CSV exports.

## Features

- **Analyze:** drop a GPUMON .csv and it is analyzed immediately. The page shows:
  - the file name, GPU, VBIOS and driver
  - a PASS/FAIL verdict
  - a min/avg/max table, synchronized charts, and P-State and capping timelines
- **TGP requirement check (PASS/FAIL):** equivalent to the spreadsheet formula
  `=IF(COUNTIF(range,">="&70)/COUNT(range)>=98%,"PASS","FAIL")`, evaluated over the `1:TGP (W)` values within the GPU utilization range.
  - A FAIL shows how many more samples need to reach the expected TGP, and the highest expected TGP the run would still pass.
  - The TGP chart draws the expected TGP as a dashed line, with samples below it in red.
- **GPU utilization filter** (`1:GPU Utilization (%)`): every metric, the TGP check, and the P-State/capping counts use only samples inside the selected range. Timelines show the whole test with the in-range periods shaded.
- **Presets:** save a combination of utilization range, expected TGP and required % per project.
  - A preset can include a file-name prefix (e.g. `163L`) so it is applied automatically to matching logs.
  - Presets and your last manual settings are stored in the browser.
- **Reliability warning** when fewer than 100 samples fall inside the utilization range.
- **Batch:** drop many logs at once to get a PASS/FAIL table for every unit.
  - Each file uses its matching preset.
  - The table exports to CSV, and any row opens on the Analyze page.
- **Compare:** two to four runs on a shared elapsed-time scale, with per-run TGP PASS/FAIL. Results update automatically.
- **Traceability:** the file name and settings are included in the copied summary and the PDF report.
- **Engineering PDF export.**

## Supported log formats

Columns are located by header name, never by position, because GPUMON changes column order and count depending on the options selected when recording starts. The `N:` GPU-index prefix and the unit in parentheses are ignored when matching.

| Metric | GPUMon 7.x header | GPUMon 6.x header |
|---|---|---|
| Date / time | `Date`, `Timestamp` | `date`, `time` |
| GPU utilization | `1:GPU Utilization (%)` | `1:gpc_util` |
| GPU temperature | `1:Temperature GPU (C)` | `1:t_gpu` |
| Memory temperature | `1:Temperature Memory (C)` | `1:mem_temp` |
| TGP | `1:TGP (W)` | `1:pwr_tgp` (mW, converted to W) |
| GPC clock | `1:GPC Clock (MHz)` | `1:gpc_clk` |
| Memory clock | `1:Memory Clock (MHz)` | `1:clk_mem` |
| P-State | `1:P-State` | `1:p_state` |
| Capping reason | `1:Capping Reason` | `1:capping_reason` |

The columns that were used are shown under the metrics on the Analyze page.

## Run locally

```bash
dotnet restore
dotnet run
```

Open `http://localhost:5200/productDev/Tools/gpuMonLogParser`.

## Repository layout

- `Components/` contains the Blazor UI: the Analyze, Batch and Compare pages, plus shared components such as the settings/preset bar (`Components/Shared/AnalysisSettingsBar.razor`).
- `Services/` contains the shared GPUMON log parser/analyzer (`GpuMonLogAnalyzer`) used by every page, the analysis settings/preset model, and the log hand-off used to open Batch results on the Analyze page.
- `wwwroot/` contains the browser-side chart, clipboard, export, and styling assets.
- `samples/` contains representative GPUMON CSV fixtures for manual and regression testing.
- `reports/` contains the human-readable calculation audits. PDF exports are intentionally not versioned.

Build output (`bin/`, `obj/`, `publish/`, and `publish.zip`) is generated locally or by the deployment pipeline and is intentionally excluded from source control.

## Deployment

The included multi-stage `Dockerfile` publishes the application with the .NET 9 SDK and runs it on the .NET 9 ASP.NET runtime. It is configured to listen on port `10000` for Render.

Live deployment: https://gpumonparserv3.onrender.com/productDev/Tools/gpuMonLogParser

