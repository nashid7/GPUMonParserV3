# GPU Monitor Log Parser (.NET 9 / Blazor Server)

Lightweight internal tool to parse NVIDIA GPUMON logs (legacy and new formats), filter on GPU utilization, visualize temps/power, and export an engineering PDF.

## Features
- Upload `.csv/.log/.txt` GPUMON outputs.
- Supports multiple GPUMON header variants across old and new log versions.
- Dynamic utilization range filtering.
- Metrics for GPU temp, memory temp, power, GPC clock, PCIe link info, and throttle events.
- Charts for GPU temp, power, and power vs GPU temp with expandable modal view.
- PDF export via `html2canvas` and `jsPDF`.
- Clipboard copy of summary.

## Run locally
```bash
dotnet restore
dotnet run
```

Open `http://localhost:5200/productDev/Tools/gpuMonLogParser`.

## Deploy

This repo includes a `Dockerfile` for deploying the Blazor Server app to Render or any container host.

Live test deployment:
- https://gpumonparserv3.onrender.com/productDev/Tools/gpuMonLogParser

## Repo notes
- Frontend: Blazor Server (.NET 9)
- Charts: `wwwroot/js/charts.js`
- PDF export: `wwwroot/js/export.js`
- Main page: `Components/Pages/productDev/Tools/GpuMonLogParser.razor`
