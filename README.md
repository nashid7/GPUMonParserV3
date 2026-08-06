# GPU Monitor Log Parser

A .NET 9 Blazor application for analyzing NVIDIA GPU Monitor CSV exports.

## Features

- Configurable GPU-utilization filtering.
- Min, average, and max values for GPU temperature, memory temperature, power, GPC clock, and memory clock.
- GPU metadata including brand, VBIOS, and driver versions.
- P-State and capping-reason activity timelines.
- Synchronized performance charts with expandable, wheel-zoomable views.
- Comparison of two to four runs on a shared elapsed-time scale.
- Multi-run tooltips and a structured performance comparison matrix.
- Engineering PDF export.

## Run locally

```bash
dotnet restore
dotnet run
```

Open `http://localhost:5200/productDev/Tools/gpuMonLogParser`.

## Deployment

The included multi-stage `Dockerfile` publishes the application with the .NET 9 SDK and runs it on the .NET 9 ASP.NET runtime. It is configured to listen on port `10000` for Render.

Live deployment: https://gpumonparserv3.onrender.com/productDev/Tools/gpuMonLogParser

