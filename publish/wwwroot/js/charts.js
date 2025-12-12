window.gpuCharts = (() => {
    const instances = {};
    const cache = {};

    const ensureChartJs = () => {
        if (typeof Chart === "undefined") {
            console.warn("Chart.js not loaded");
            return false;
        }
        return true;
    };

    const destroy = (id) => {
        if (instances[id]) {
            instances[id].destroy();
            delete instances[id];
        }
    };

    const renderLine = (id, labels, data, label, color) => {
        if (!ensureChartJs()) return;
        const ctx = document.getElementById(id);
        if (!ctx) return;
        destroy(id);
        cache[id] = { kind: "line", labels, data, label, color };
        instances[id] = new Chart(ctx, {
            type: "line",
            data: {
                labels,
                datasets: [
                    {
                        label: label || "Series",
                        data,
                        borderColor: color || "#3b82f6",
                        backgroundColor: "rgba(59,130,246,0.15)",
                        fill: false,
                        pointRadius: 1,
                        tension: 0.2,
                    },
                ],
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: "index", intersect: false },
                plugins: { legend: { display: true } },
                scales: {
                    x: { ticks: { autoSkip: true, maxTicksLimit: 10 } },
                    y: { beginAtZero: false },
                },
            },
        });
    };

    const openModalFrom = (modalCanvasId, sourceId) => {
        if (!ensureChartJs()) return;
        const ctx = document.getElementById(modalCanvasId);
        const cached = cache[sourceId];
        if (!cached || !ctx) return;
        const render = () => {
            destroy(modalCanvasId);
            if (cached.kind === "line") {
                renderLine(modalCanvasId, cached.labels, cached.data, cached.label, cached.color);
            } else if (cached.kind === "scatter") {
                renderScatter(modalCanvasId, cached.points, cached.xLabel, cached.yLabel, cached.color);
            }
            if (instances[modalCanvasId]) {
                instances[modalCanvasId].resize();
            }
        };
        requestAnimationFrame(() => requestAnimationFrame(render));
    };

    const renderScatter = (id, points, xLabel, yLabel, color) => {
        if (!ensureChartJs()) return;
        const ctx = document.getElementById(id);
        if (!ctx) return;
        destroy(id);
        cache[id] = { kind: "scatter", points, xLabel, yLabel, color };
        instances[id] = new Chart(ctx, {
            type: "scatter",
            data: {
                datasets: [
                    {
                        label: `${yLabel || "Y"} vs ${xLabel || "X"}`,
                        data: points,
                        backgroundColor: color || "rgba(52,211,153,0.6)",
                        borderColor: color || "#34d399",
                        pointRadius: 3,
                    },
                ],
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: "nearest", intersect: false },
                plugins: { legend: { display: true } },
                scales: {
                    x: { title: { display: true, text: xLabel || "X" } },
                    y: { title: { display: true, text: yLabel || "Y" } },
                },
            },
        });
    };

    return { renderLine, renderScatter, destroy, openModalFrom };
})();
