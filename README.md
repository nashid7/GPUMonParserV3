# GPU Monitor Log Parser (.NET 9 / Blazor Server)

Lightweight internal tool to parse NVIDIA GPUMON logs (legacy and new formats), filter on GPU utilization, visualize temps/power, and export an engineering PDF.

## Features
- Upload `.csv/.log/.txt` GPUMON outputs (handles legacy headers like `1:t_gpu` and newer headers like `1:GPU Utilization (%)`).
- Utilization filter presets (99–100, 95+, or all rows).
- Metrics: GPU/Memory temps, power, PCIe link info, throttle events with breakdown.
- Charts: GPU temp, Power, Power vs Temp scatter with expandable modal view.
- PDF export (html2canvas + jsPDF) with summary tables and charts.
- Clipboard copy of summary.

## Run locally
```bash
dotnet restore
dotnet run
```
Open http://localhost:5200/productDev/Tools/gpuMonLogParser

## Codespaces (GitHub.com)
- Open the repo → Code → Codespaces → New codespace.
- In the terminal:
  ```bash
  dotnet restore
  dotnet run
  ```
- In the Ports panel, mark port 5200 as Public and share the URL (e.g., https://zany-happiness-xgv4975j49whpj49-5200.app.github.dev).

## Usage tips
- If no rows match the filter, lower the threshold to 95+ or All rows.
- “Util column” and “Power column” labels show which headers were detected.
- PDF exports include summary tables, throttle breakdown, and the three charts. Fallbacks avoid “undefined” values when data is missing.

## Repo notes
- Frontend: Blazor Server (.NET 9). Charts via Chart.js (custom wrapper in `wwwroot/js/charts.js`). PDF via `wwwroot/js/export.js`.
- Page: `Components/Pages/productDev/Tools/GpuMonLogParser.razor`
- Static assets: `wwwroot/js/*` and `wwwroot/css/app.css` (if tweaked).
