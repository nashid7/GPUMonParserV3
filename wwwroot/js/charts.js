window.gpuCharts = (() => {
    const instances = {};
    const cache = {};
    const timelineStates = {};

    const ensureChartJs = () => {
        if (typeof Chart === "undefined") {
            console.warn("Chart.js not loaded");
            return false;
        }
        return true;
    };

    const destroy = (id) => {
        const state = timelineStates[id];
        if (state?.canvas && state.handlers) {
            state.canvas.removeEventListener("wheel", state.handlers.wheel);
            state.canvas.removeEventListener("pointerdown", state.handlers.pointerDown);
            state.canvas.removeEventListener("pointermove", state.handlers.pointerMove);
            state.canvas.removeEventListener("pointerup", state.handlers.pointerUp);
            state.canvas.removeEventListener("pointercancel", state.handlers.pointerUp);
            state.inspector?.classList.remove("show");
            delete timelineStates[id];
        }
        if (instances[id]) {
            instances[id].destroy();
            delete instances[id];
        }
    };

    const formatElapsed = (seconds) => {
        const total = Math.max(0, Math.round(Number(seconds) || 0));
        const hours = Math.floor(total / 3600);
        const minutes = Math.floor((total % 3600) / 60);
        const secs = total % 60;
        return hours > 0
            ? `${hours}:${String(minutes).padStart(2, "0")}:${String(secs).padStart(2, "0")}`
            : `${minutes}:${String(secs).padStart(2, "0")}`;
    };

    const timelineColor = (label, index) => {
        const known = {
            Pwr: "#f59e0b",
            Thml: "#ef4444",
            VRel: "#a855f7",
            VOp: "#ec4899",
            Util: "#38bdf8",
            P0: "#22c55e",
            P2: "#14b8a6",
            P3: "#3b82f6",
            P4: "#6366f1",
            P5: "#8b5cf6",
            P8: "#64748b",
            Unknown: "#94a3b8",
        };
        const palette = ["#3b82f6", "#14b8a6", "#8b5cf6", "#f59e0b", "#ef4444", "#06b6d4", "#84cc16"];
        return known[label] || palette[index % palette.length];
    };

    const ensureTimelineInspector = (id, canvas) => {
        const inspectorId = `${id}-inspector`;
        let inspector = document.getElementById(inspectorId);
        if (inspector) {
            inspector.className = "timeline-pointer-readout";
            inspector.replaceChildren();
            return inspector;
        }

        inspector = document.createElement("div");
        inspector.id = inspectorId;
        inspector.className = "timeline-pointer-readout";
        const parent = canvas.parentElement;
        if (parent) {
            parent.insertBefore(inspector, canvas);
        }
        return inspector;
    };

    const clockAtElapsed = (segments, seconds) => {
        const anchor = [...segments].sort((left, right) => left.startSeconds - right.startSeconds)[0];
        if (!anchor || !/^\d{2}:\d{2}:\d{2}(?:\.\d{3})?$/.test(anchor.startTime)) return "";
        const [hours, minutes, secondsAndMilliseconds] = anchor.startTime.split(":");
        const [wholeSeconds, milliseconds = "0"] = secondsAndMilliseconds.split(".");
        const anchorMilliseconds =
            Number(hours) * 3600000 +
            Number(minutes) * 60000 +
            Number(wholeSeconds) * 1000 +
            Number(milliseconds.padEnd(3, "0"));
        const dayMilliseconds = 24 * 3600000;
        const value = ((anchorMilliseconds - anchor.startSeconds * 1000 + seconds * 1000) % dayMilliseconds + dayMilliseconds) % dayMilliseconds;
        const resultHours = Math.floor(value / 3600000);
        const resultMinutes = Math.floor((value % 3600000) / 60000);
        const resultSeconds = Math.floor((value % 60000) / 1000);
        const resultMilliseconds = Math.floor(value % 1000);
        return `${String(resultHours).padStart(2, "0")}:${String(resultMinutes).padStart(2, "0")}:${String(resultSeconds).padStart(2, "0")}.${String(resultMilliseconds).padStart(3, "0")}`;
    };

    const updateTimelineInspector = (inspector, segments, seconds, title) => {
        if (!inspector) return;
        const active = segments.filter(segment =>
            seconds >= segment.startSeconds && seconds < segment.endSeconds);
        const clockTime = clockAtElapsed(segments, seconds);
        inspector.replaceChildren();

        const time = document.createElement("div");
        time.className = "pointer-time";
        time.textContent = `${clockTime || "Time unavailable"} · +${formatElapsed(seconds)}`;
        inspector.appendChild(time);

        const badges = document.createElement("div");
        badges.className = "pointer-badges";
        if (active.length) {
            active.forEach((segment, index) => {
                const badge = document.createElement("span");
                badge.className = "pointer-badge";
                badge.textContent = segment.label;
                badge.style.color = timelineColor(segment.label, index);
                badge.title = `${segment.startTime} - ${segment.endTime}`;
                badges.appendChild(badge);
            });
        } else {
            const badge = document.createElement("span");
            badge.className = "pointer-badge";
            badge.textContent = title?.toLowerCase().includes("capping") ? "No capping" : "Unknown";
            badge.style.color = "#94a3b8";
            badges.appendChild(badge);
        }
        inspector.appendChild(badges);

        if (active.length > 1) {
            const overlap = document.createElement("div");
            overlap.className = "pointer-overlap";
            overlap.textContent = `${active.length} overlapping reasons`;
            inspector.appendChild(overlap);
        }
    };

    const positionTimelineInspector = (state, cursorX, cursorY) => {
        const parent = state.canvas.parentElement;
        if (!parent || !state.inspector) return;
        state.inspector.classList.add("show");
        const gap = 12;
        const canvasLeft = state.canvas.offsetLeft;
        const canvasTop = state.canvas.offsetTop;
        const width = state.inspector.offsetWidth;
        const height = state.inspector.offsetHeight;
        let left = canvasLeft + cursorX + gap;
        if (left + width > parent.clientWidth - 8)
            left = canvasLeft + cursorX - width - gap;
        let top = canvasTop + cursorY - height / 2;
        left = Math.max(8, Math.min(parent.clientWidth - width - 8, left));
        top = Math.max(8, Math.min(parent.clientHeight - height - 8, top));
        state.inspector.style.left = `${left}px`;
        state.inspector.style.top = `${top}px`;
    };

    const timelineCursorPlugin = {
        id: "timelineCursor",
        afterDatasetsDraw(chart) {
            if (!Number.isFinite(chart.$timelineCursorX)) return;
            const { ctx: chartContext, chartArea } = chart;
            chartContext.save();
            chartContext.beginPath();
            chartContext.moveTo(chart.$timelineCursorX, chartArea.top);
            chartContext.lineTo(chart.$timelineCursorX, chartArea.bottom);
            chartContext.lineWidth = 1;
            chartContext.strokeStyle = "rgba(226,232,240,0.9)";
            chartContext.setLineDash([4, 3]);
            chartContext.stroke();
            chartContext.restore();
        },
    };

    const applyTimelineWindow = (state, requestedMin, requestedMax) => {
        if (!state?.chart) return;
        const fullWidth = state.fullMax - state.fullMin;
        const requestedWidth = requestedMax - requestedMin;
        const width = Math.max(Math.min(5, fullWidth), Math.min(fullWidth, requestedWidth));
        const min = Math.max(state.fullMin, Math.min(requestedMin, state.fullMax - width));
        const max = min + width;
        state.chart.options.scales.x.min = min;
        state.chart.options.scales.x.max = max;
        state.chart.update("none");

        const cursorIsVisible = state.cursorSeconds >= min && state.cursorSeconds <= max;
        state.chart.$timelineCursorX = cursorIsVisible
            ? state.chart.scales.x.getPixelForValue(state.cursorSeconds)
            : Number.NaN;
        state.chart.draw();
        if (cursorIsVisible && state.inspector.classList.contains("show"))
            positionTimelineInspector(state, state.chart.$timelineCursorX, state.cursorY);
    };

    const setTimelineCursor = (state, seconds, ensureVisible = false, pointerY = null) => {
        if (!state?.chart) return;
        state.cursorSeconds = Math.max(state.fullMin, Math.min(state.fullMax, seconds));
        if (Number.isFinite(pointerY)) state.cursorY = pointerY;
        let min = Number(state.chart.options.scales.x.min ?? state.fullMin);
        let max = Number(state.chart.options.scales.x.max ?? state.fullMax);
        if (ensureVisible && (state.cursorSeconds < min || state.cursorSeconds > max)) {
            const width = max - min;
            min = state.cursorSeconds - width / 2;
            max = state.cursorSeconds + width / 2;
            applyTimelineWindow(state, min, max);
            min = Number(state.chart.options.scales.x.min);
            max = Number(state.chart.options.scales.x.max);
        }

        updateTimelineInspector(state.inspector, state.segments, state.cursorSeconds, state.title);
        state.chart.$timelineCursorX = state.cursorSeconds >= min && state.cursorSeconds <= max
            ? state.chart.scales.x.getPixelForValue(state.cursorSeconds)
            : Number.NaN;
        state.chart.draw();
        if (Number.isFinite(state.chart.$timelineCursorX) && (Number.isFinite(pointerY) || ensureVisible))
            positionTimelineInspector(state, state.chart.$timelineCursorX, state.cursorY);
    };

    const zoomTimelineAround = (state, center, factor) => {
        const min = Number(state.chart.options.scales.x.min ?? state.fullMin);
        const max = Number(state.chart.options.scales.x.max ?? state.fullMax);
        const safeCenter = Math.max(min, Math.min(max, center));
        applyTimelineWindow(
            state,
            safeCenter - (safeCenter - min) * factor,
            safeCenter + (max - safeCenter) * factor);
    };

    const timelineCommand = (id, command) => {
        const state = timelineStates[id];
        if (!state?.chart) return;
        const min = Number(state.chart.options.scales.x.min ?? state.fullMin);
        const max = Number(state.chart.options.scales.x.max ?? state.fullMax);
        const center = state.cursorSeconds >= min && state.cursorSeconds <= max
            ? state.cursorSeconds
            : (min + max) / 2;

        if (command === "full") {
            applyTimelineWindow(state, state.fullMin, state.fullMax);
            return;
        }
        if (command === "zoomIn") {
            zoomTimelineAround(state, center, 0.5);
            return;
        }
        if (command === "zoomOut") {
            zoomTimelineAround(state, center, 2);
            return;
        }
        if (command === "window60" || command === "window300") {
            const width = command === "window60" ? 60 : 300;
            applyTimelineWindow(state, center - width / 2, center + width / 2);
            return;
        }
        if (command === "next") {
            const next = state.boundaries.find(boundary => boundary > state.cursorSeconds + 0.0001)
                ?? state.boundaries[0];
            setTimelineCursor(state, next, true);
            return;
        }
        if (command === "previous") {
            const previous = [...state.boundaries].reverse()
                .find(boundary => boundary < state.cursorSeconds - 0.0001)
                ?? state.boundaries[state.boundaries.length - 1];
            setTimelineCursor(state, previous, true);
        }
    };

    const attachTimelineInteractions = (state) => {
        const wheel = event => {
            event.preventDefault();
            const rect = state.canvas.getBoundingClientRect();
            const pixel = event.clientX - rect.left;
            const center = state.chart.scales.x.getValueForPixel(pixel);
            setTimelineCursor(state, center, false, event.clientY - rect.top);
            zoomTimelineAround(state, center, event.deltaY < 0 ? 0.72 : 1.4);
        };
        const pointerDown = event => {
            if (event.button !== 0) return;
            state.drag = {
                clientX: event.clientX,
                min: Number(state.chart.options.scales.x.min ?? state.fullMin),
                max: Number(state.chart.options.scales.x.max ?? state.fullMax),
            };
            state.canvas.setPointerCapture?.(event.pointerId);
            state.canvas.style.cursor = "grabbing";
        };
        const pointerMove = event => {
            if (!state.drag) return;
            event.preventDefault();
            const chartWidth = Math.max(1, state.chart.chartArea.right - state.chart.chartArea.left);
            const secondsPerPixel = (state.drag.max - state.drag.min) / chartWidth;
            const shift = -(event.clientX - state.drag.clientX) * secondsPerPixel;
            applyTimelineWindow(state, state.drag.min + shift, state.drag.max + shift);
        };
        const pointerUp = event => {
            if (!state.drag) return;
            state.drag = null;
            state.canvas.releasePointerCapture?.(event.pointerId);
            state.canvas.style.cursor = "crosshair";
        };

        state.handlers = { wheel, pointerDown, pointerMove, pointerUp };
        state.canvas.addEventListener("wheel", wheel, { passive: false });
        state.canvas.addEventListener("pointerdown", pointerDown);
        state.canvas.addEventListener("pointermove", pointerMove);
        state.canvas.addEventListener("pointerup", pointerUp);
        state.canvas.addEventListener("pointercancel", pointerUp);
        state.canvas.style.cursor = "crosshair";
        state.canvas.style.touchAction = "none";
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
            } else if (cached.kind === "timeline") {
                renderTimeline(modalCanvasId, cached.segments, cached.title);
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

    const renderTimeline = (id, segments, title) => {
        if (!ensureChartJs()) return;
        const ctx = document.getElementById(id);
        if (!ctx) return;
        destroy(id);

        const normalized = (segments || []).map((segment, index) => ({
            label: segment.label ?? segment.Label ?? "Unknown",
            startSeconds: Number(segment.startSeconds ?? segment.StartSeconds ?? 0),
            endSeconds: Number(segment.endSeconds ?? segment.EndSeconds ?? 0),
            startTime: segment.startTime ?? segment.StartTime ?? "",
            endTime: segment.endTime ?? segment.EndTime ?? "",
            durationSeconds: Number(segment.durationSeconds ?? segment.DurationSeconds ?? 0),
            color: timelineColor(segment.label ?? segment.Label ?? "Unknown", index),
        }));
        if (!normalized.length) return;

        cache[id] = { kind: "timeline", segments, title };
        const inspector = ensureTimelineInspector(id, ctx);
        const labels = [...new Set(normalized.map(segment => segment.label))];
        const fullMax = Math.max(...normalized.map(segment => segment.endSeconds));
        const state = {
            canvas: ctx,
            chart: null,
            inspector,
            segments: normalized,
            title,
            boundaries: [...new Set(normalized.flatMap(segment => [segment.startSeconds, segment.endSeconds]))]
                .sort((left, right) => left - right),
            fullMin: 0,
            fullMax,
            cursorSeconds: 0,
            cursorY: 24,
            drag: null,
            handlers: null,
        };
        const chart = new Chart(ctx, {
            type: "bar",
            data: {
                labels,
                datasets: [{
                    label: title || "Events",
                    data: normalized.map(segment => ({
                        x: [segment.startSeconds, segment.endSeconds],
                        y: segment.label,
                        startTime: segment.startTime,
                        endTime: segment.endTime,
                        durationSeconds: segment.durationSeconds,
                    })),
                    backgroundColor: normalized.map(segment => segment.color),
                    borderColor: normalized.map(segment => segment.color),
                    borderWidth: 1,
                    borderSkipped: false,
                    borderRadius: 4,
                    barPercentage: 0.68,
                    categoryPercentage: 0.82,
                    maxBarThickness: 24,
                    minBarLength: 4,
                }],
            },
            options: {
                indexAxis: "y",
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                layout: { padding: { top: 4, right: 6, bottom: 0, left: 2 } },
                interaction: { mode: "nearest", axis: "x", intersect: false },
                onHover: (event, _elements, activeChart) => {
                    if (!Number.isFinite(event.x)) return;
                    const { left, right } = activeChart.chartArea;
                    const cursorX = Math.max(left, Math.min(right, event.x));
                    const seconds = activeChart.scales.x.getValueForPixel(cursorX);
                    setTimelineCursor(state, seconds, false, event.y);
                },
                plugins: {
                    legend: { display: false },
                    tooltip: { enabled: false },
                },
                scales: {
                    x: {
                        type: "linear",
                        min: 0,
                        max: fullMax,
                        title: {
                            display: true,
                            text: "Elapsed test time",
                            color: "#7f91aa",
                            font: { size: 11, weight: "500" },
                        },
                        ticks: {
                            callback: value => formatElapsed(value),
                            color: "#718198",
                            padding: 7,
                            maxRotation: 0,
                            font: { size: 10 },
                        },
                        grid: { color: "rgba(71,85,105,0.12)", drawTicks: false },
                        border: { color: "rgba(71,85,105,0.35)" },
                    },
                    y: {
                        type: "category",
                        title: { display: false },
                        ticks: {
                            color: "#94a3b8",
                            padding: 9,
                            font: { size: 11, weight: "600" },
                        },
                        grid: { display: false },
                        border: { display: false },
                    },
                },
            },
            plugins: [timelineCursorPlugin],
        });
        instances[id] = chart;
        state.chart = chart;
        timelineStates[id] = state;
        attachTimelineInteractions(state);
        setTimelineCursor(state, 0);
    };

    return { renderLine, renderScatter, renderTimeline, timelineCommand, destroy, openModalFrom };
})();
