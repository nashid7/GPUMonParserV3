window.gpuCharts = (() => {
    const instances = {};
    const cache = {};
    const timelineStates = {};
    const expandedZoomStates = {};
    const syncGroups = {};

    const ensureChartJs = () => {
        if (typeof Chart === "undefined") {
            console.warn("Chart.js not loaded");
            return false;
        }
        return true;
    };

    // Runs fn at most once per animation frame with the latest arguments, so fast pointer/wheel events
    // never trigger more redraws than the screen can show.
    const perFrame = fn => {
        let frame = 0;
        let latest = [];
        return (...args) => {
            latest = args;
            if (frame) return;
            frame = requestAnimationFrame(() => {
                frame = 0;
                fn(...latest);
            });
        };
    };

    // Index of the point whose x is closest to value; points must be sorted by x.
    const nearestByX = (points, value) => {
        let low = 0;
        let high = points.length - 1;
        if (high < 0) return -1;
        while (low < high) {
            const middle = (low + high) >> 1;
            if (Number(points[middle].x) < value) low = middle + 1;
            else high = middle;
        }
        if (low > 0 && Math.abs(Number(points[low - 1].x) - value) <= Math.abs(Number(points[low].x) - value)) return low - 1;
        return low;
    };

    const destroy = (id) => {
        const expandedState = expandedZoomStates[id];
        if (expandedState?.canvas && expandedState.wheel) {
            expandedState.canvas.removeEventListener("wheel", expandedState.wheel);
            delete expandedZoomStates[id];
        }
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
            instances[id].$cursorLayer?.remove();
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

    const updateTimelineInspector = (inspector, segments, seconds, title, highlights) => {
        if (!inspector) return;
        const active = segments.filter(segment =>
            seconds >= segment.startSeconds && seconds < segment.endSeconds);
        const clockTime = clockAtElapsed(segments, seconds);
        inspector.replaceChildren();

        const time = document.createElement("div");
        time.className = "pointer-time";
        time.textContent = `${clockTime || "Time unavailable"} · +${formatElapsed(seconds)}`;
        inspector.appendChild(time);

        if (highlights?.length) {
            const inRange = highlights.some(range => seconds >= range.start && seconds < range.end);
            const range = document.createElement("div");
            range.className = "pointer-overlap";
            range.style.color = inRange ? "#4ade80" : "#94a3b8";
            range.textContent = inRange ? "Within utilization range" : "Outside utilization range";
            inspector.appendChild(range);
        }

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

    const normalizeHighlights = highlights => (highlights || []).map(range => ({
        start: Number(range.startSeconds ?? range.StartSeconds ?? 0),
        end: Number(range.endSeconds ?? range.EndSeconds ?? 0),
    })).filter(range => Number.isFinite(range.start) && Number.isFinite(range.end) && range.end > range.start);

    // Shades the elapsed-time windows where GPU utilization is inside the selected range.
    const inRangeShadePlugin = {
        id: "inRangeShade",
        beforeDatasetsDraw(chart) {
            const windows = chart.$inRangeWindows;
            if (!windows?.length) return;
            const { ctx: chartContext, chartArea, scales } = chart;
            chartContext.save();
            chartContext.beginPath();
            chartContext.rect(chartArea.left, chartArea.top, chartArea.right - chartArea.left, chartArea.bottom - chartArea.top);
            chartContext.clip();
            chartContext.fillStyle = chart.$inRangeColor || "rgba(34,197,94,0.14)";
            for (const range of windows) {
                const left = scales.x.getPixelForValue(range.start);
                const right = scales.x.getPixelForValue(range.end);
                if (right < chartArea.left || left > chartArea.right) continue;
                chartContext.fillRect(left, chartArea.top, Math.max(1, right - left), chartArea.bottom - chartArea.top);
            }
            chartContext.restore();
        },
    };

    const timelineCursorPlugin = {
        id: "timelineCursor",
        afterDatasetsDraw(chart) {
            const seconds = chart.$timelineCursorSeconds;
            if (!Number.isFinite(seconds)) return;
            const { ctx: chartContext, chartArea } = chart;
            const x = chart.scales.x.getPixelForValue(seconds);
            if (x < chartArea.left || x > chartArea.right) return;
            chartContext.save();
            chartContext.beginPath();
            chartContext.moveTo(x, chartArea.top);
            chartContext.lineTo(x, chartArea.bottom);
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
        state.chart.$timelineCursorSeconds = state.cursorSeconds;
        state.chart.update("none");

        const cursorIsVisible = state.cursorSeconds >= min && state.cursorSeconds <= max;
        if (cursorIsVisible && state.inspector.classList.contains("show"))
            positionTimelineInspector(state, state.chart.scales.x.getPixelForValue(state.cursorSeconds), state.cursorY);
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

        updateTimelineInspector(state.inspector, state.segments, state.cursorSeconds, state.title, state.highlights);
        const cursorIsVisible = state.cursorSeconds >= min && state.cursorSeconds <= max;
        state.chart.$timelineCursorSeconds = state.cursorSeconds;
        state.chart.draw();
        if (cursorIsVisible && (Number.isFinite(pointerY) || ensureVisible))
            positionTimelineInspector(state, state.chart.scales.x.getPixelForValue(state.cursorSeconds), state.cursorY);
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
        let pendingFactor = 1;
        let pendingCenter = 0;
        const zoom = perFrame(() => {
            const factor = pendingFactor;
            pendingFactor = 1;
            zoomTimelineAround(state, pendingCenter, factor);
        });
        const pan = perFrame((min, max) => applyTimelineWindow(state, min, max));
        const hover = perFrame((clientX, clientY) => {
            const rect = state.canvas.getBoundingClientRect();
            const { left, right } = state.chart.chartArea;
            const cursorX = Math.max(left, Math.min(right, clientX - rect.left));
            setTimelineCursor(state, state.chart.scales.x.getValueForPixel(cursorX), false, clientY - rect.top);
        });

        const wheel = event => {
            event.preventDefault();
            const rect = state.canvas.getBoundingClientRect();
            pendingCenter = state.chart.scales.x.getValueForPixel(event.clientX - rect.left);
            pendingFactor *= event.deltaY < 0 ? 0.72 : 1.4;
            zoom();
            hover(event.clientX, event.clientY);
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
            if (!state.drag) {
                hover(event.clientX, event.clientY);
                return;
            }
            event.preventDefault();
            const chartWidth = Math.max(1, state.chart.chartArea.right - state.chart.chartArea.left);
            const secondsPerPixel = (state.drag.max - state.drag.min) / chartWidth;
            const shift = -(event.clientX - state.drag.clientX) * secondsPerPixel;
            pan(state.drag.min + shift, state.drag.max + shift);
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

    // Dashed "expected" line; samples below it are drawn in red (used for the TGP chart).
    const thresholdPlugin = {
        id: "thresholdLine",
        afterDatasetsDraw(chart) {
            const threshold = chart.$threshold;
            if (!Number.isFinite(threshold) || !chart.scales.y) return;
            const y = chart.scales.y.getPixelForValue(threshold);
            const { left, right, top, bottom } = chart.chartArea;
            if (y < top || y > bottom) return;
            const context = chart.ctx;
            const large = chart.$reportScale ? 2 : 1;
            context.save();
            context.beginPath();
            context.moveTo(left, y);
            context.lineTo(right, y);
            context.lineWidth = 1.5 * large;
            context.strokeStyle = "#f87171";
            context.setLineDash([6 * large, 4 * large]);
            context.stroke();
            context.setLineDash([]);
            context.font = `600 ${11 * large}px system-ui, sans-serif`;
            context.fillStyle = "#f87171";
            context.textAlign = "right";
            context.fillText(`Expected ${threshold.toLocaleString(undefined, { maximumFractionDigits: 2 })} W`, right - 4, y - 5 * large);
            context.restore();
        },
    };

    const thresholdDatasetStyle = (threshold, color) => !Number.isFinite(threshold) ? {} : {
        pointBackgroundColor: context => Number(context.raw) < threshold ? "#ef4444" : color,
        pointBorderColor: context => Number(context.raw) < threshold ? "#ef4444" : color,
        segment: {
            borderColor: context => context.p0.parsed.y < threshold || context.p1.parsed.y < threshold ? "#ef4444" : undefined,
        },
    };

    const thresholdScale = threshold => !Number.isFinite(threshold) ? {} : { suggestedMin: threshold - 1, suggestedMax: threshold + 1 };

    const renderLine = (id, labels, data, label, color, threshold, inspect = false) => {
        if (!ensureChartJs()) return;
        const ctx = document.getElementById(id);
        if (!ctx) return;
        destroy(id);
        const expected = threshold === null || threshold === undefined ? Number.NaN : Number(threshold);
        cache[id] = { kind: "line", labels, data, label, color, threshold: expected };
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
                        borderWidth: 1.5,
                        fill: false,
                        pointRadius: 0,
                        pointHoverRadius: inspect ? 3 : 0,
                        tension: 0,
                        spanGaps: true,
                        ...thresholdDatasetStyle(expected, color || "#3b82f6"),
                    },
                ],
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                normalized: true,
                // The small charts use the synced cursor/readout instead of Chart.js hover handling, which is much cheaper.
                events: inspect ? ["mousemove", "mouseout", "click"] : [],
                interaction: { mode: "index", intersect: false },
                plugins: { legend: { display: inspect }, tooltip: { enabled: inspect, animation: false } },
                scales: {
                    x: { ticks: { autoSkip: true, maxTicksLimit: 8, maxRotation: 0, sampleSize: 24 } },
                    y: { beginAtZero: false, ticks: { sampleSize: 24 }, afterFit: fixedYAxisWidth, ...thresholdScale(expected) },
                },
            },
            plugins: [syncCursorPlugin, thresholdPlugin],
        });
        instances[id].$threshold = expected;
    };

    // Transparent canvas layered over a chart. The hover cursor is drawn here, so moving the mouse
    // never re-renders the chart's lines, axes or grid.
    const cursorLayer = chart => {
        const canvas = chart.canvas;
        if (!canvas?.parentElement) return null;
        let layer = chart.$cursorLayer;
        if (!layer || !layer.isConnected) {
            layer = document.createElement("canvas");
            layer.style.position = "absolute";
            layer.style.pointerEvents = "none";
            if (getComputedStyle(canvas.parentElement).position === "static") canvas.parentElement.style.position = "relative";
            canvas.parentElement.appendChild(layer);
            chart.$cursorLayer = layer;
        }
        if (layer.width !== canvas.width || layer.height !== canvas.height) {
            layer.width = canvas.width;
            layer.height = canvas.height;
        }
        layer.style.left = `${canvas.offsetLeft}px`;
        layer.style.top = `${canvas.offsetTop}px`;
        layer.style.width = `${canvas.clientWidth}px`;
        layer.style.height = `${canvas.clientHeight}px`;
        const context = layer.getContext("2d");
        context.setTransform(1, 0, 0, 1, 0, 0);
        context.clearRect(0, 0, layer.width, layer.height);
        const ratio = canvas.clientWidth ? canvas.width / canvas.clientWidth : 1;
        context.setTransform(ratio, 0, 0, ratio, 0, 0);
        return context;
    };

    const paintSyncCursor = chart => {
        const context = cursorLayer(chart);
        const cursor = chart.$syncCursor;
        if (!context || !cursor) return;
            const x = chart.scales.x.getPixelForValue(cursor.value);
            if (!Number.isFinite(x) || x < chart.chartArea.left || x > chart.chartArea.right) return;
            context.save();
            context.beginPath();
            context.moveTo(x, chart.chartArea.top);
            context.lineTo(x, chart.chartArea.bottom);
            context.lineWidth = 1.2;
            context.strokeStyle = "rgba(226,232,240,.9)";
            context.setLineDash([4, 3]);
            context.stroke();
            context.setLineDash([]);
            if (chart.config.type === "bar") {
                context.restore();
                return;
            }
            const tooltipRows = [];
            chart.data.datasets.forEach((dataset, datasetIndex) => {
                const meta = chart.getDatasetMeta(datasetIndex);
                if (meta.hidden || !meta.data?.length) return;
                let pointIndex = cursor.value;
                if (cursor.mode === "value") pointIndex = nearestByX(dataset.data || [], cursor.value);
                const element = meta.data[pointIndex];
                if (!element || !Number.isFinite(element.x) || !Number.isFinite(element.y)) return;
                context.beginPath();
                context.arc(element.x, element.y, 4, 0, Math.PI * 2);
                context.fillStyle = dataset.borderColor || dataset.backgroundColor || "#38bdf8";
                context.fill();
                context.lineWidth = 1.5;
                context.strokeStyle = "#f8fafc";
                context.stroke();
                const rawValue = dataset.data?.[pointIndex]?.y;
                const numericValue = Number(rawValue);
                tooltipRows.push({
                    label: dataset.label || `Run ${datasetIndex + 1}`,
                    value: Number.isFinite(numericValue)
                        ? numericValue.toLocaleString(undefined, { maximumFractionDigits: 2 })
                        : "-",
                    color: dataset.borderColor || dataset.backgroundColor || "#38bdf8",
                    y: element.y,
                });
            });
            if (chart.$syncTooltip && tooltipRows.length) {
                const title = cursor.mode === "value"
                    ? `Elapsed ${formatElapsed(cursor.value)}`
                    : chart.data.labels?.[cursor.value] || `Sample ${cursor.value + 1}`;
                context.font = "600 12px system-ui, sans-serif";
                const rowWidths = tooltipRows.map(row => context.measureText(`${row.label}: ${row.value}`).width + 28);
                const width = Math.max(128, context.measureText(title).width + 20, ...rowWidths);
                const height = 31 + tooltipRows.length * 21;
                const averageY = tooltipRows.reduce((sum, row) => sum + row.y, 0) / tooltipRows.length;
                let left = x + 12;
                if (left + width > chart.chartArea.right - 4) left = x - width - 12;
                left = Math.max(chart.chartArea.left + 4, Math.min(chart.chartArea.right - width - 4, left));
                const top = Math.max(chart.chartArea.top + 4, Math.min(chart.chartArea.bottom - height - 4, averageY - height / 2));
                context.fillStyle = "rgba(5,12,24,.96)";
                context.strokeStyle = "#3b526f";
                context.lineWidth = 1;
                context.fillRect(left, top, width, height);
                context.strokeRect(left + .5, top + .5, width - 1, height - 1);
                context.fillStyle = "#f8fafc";
                context.fillText(title, left + 10, top + 19);
                context.font = "600 12px system-ui, sans-serif";
                tooltipRows.forEach((row, index) => {
                    const rowY = top + 39 + index * 21;
                    context.fillStyle = row.color;
                    context.fillRect(left + 10, rowY - 9, 9, 9);
                    context.fillStyle = "#e2e8f0";
                    context.fillText(`${row.label}: ${row.value}`, left + 26, rowY);
                });
            }
            context.restore();
    };

    // Keeps the cursor layer aligned after Chart.js re-renders (resize, zoom, data changes).
    const syncCursorPlugin = {
        id: "syncCursor",
        afterRender(chart) {
            if (chart.$cursorLayer || chart.$syncCursor) paintSyncCursor(chart);
        },
    };

    const clearSyncGroup = groupId => {
        const group = syncGroups[groupId];
        if (!group) return;
        (group.listeners || []).forEach(listener => {
            listener.target.removeEventListener("pointermove", listener.move);
            listener.target.removeEventListener("pointerleave", listener.leave);
        });
        // Charts may already have been destroyed and replaced by a re-render.
        (group.charts || []).forEach(chart => {
            if (!chart?.ctx) return;
            chart.$syncCursor = null;
            paintSyncCursor(chart);
        });
        group.readout?.classList.remove("show");
        delete syncGroups[groupId];
    };

    // Gives every chart in a group the same y-axis width so their plot areas line up vertically:
    // the same horizontal pointer position then means the same time on every chart.
    const fixedYAxisWidth = scale => {
        if (scale.chart.$yAxisWidth) scale.width = scale.chart.$yAxisWidth;
    };

    const alignYAxes = charts => {
        const width = Math.max(...charts.map(chart => chart.scales.y?.width || 0));
        charts.forEach(chart => {
            chart.$yAxisWidth = width;
            chart.update("none");
        });
    };

    // One pointer listener on the charts' shared container keeps the cursor steady while the pointer
    // crosses the gaps and titles between charts; it is hidden only when the pointer leaves the container.
    const listenForSync = (group, container, onMove, onLeave) => {
        const move = event => onMove(event);
        const leave = () => onLeave();
        container.addEventListener("pointermove", move);
        container.addEventListener("pointerleave", leave);
        group.listeners = [{ target: container, move, leave }];
        group.charts.forEach(chart => { chart.canvas.style.cursor = "crosshair"; });
    };

    // Converts a pointer position to an x value using the first chart; the others share the same plot area.
    const pointerValue = (chart, clientX) => {
        const rect = chart.canvas.getBoundingClientRect();
        const pixel = Math.max(chart.chartArea.left, Math.min(chart.chartArea.right, clientX - rect.left));
        return Number(chart.scales.x.getValueForPixel(pixel));
    };
    // Builds the readout chips once per group and only updates their text afterwards.
    const updateMainSyncReadout = (group, index) => {
        const { readout, charts } = group;
        if (!readout) return;
        if (!group.chips) {
            readout.replaceChildren();
            group.heading = document.createElement("strong");
            const values = document.createElement("div");
            values.className = "sync-values";
            group.chips = charts.map(chart => {
                const chip = document.createElement("span");
                chip.style.setProperty("--sync-color", chart.data.datasets[0]?.borderColor || "#38bdf8");
                values.appendChild(chip);
                return chip;
            });
            readout.append(group.heading, values);
        }
        group.heading.textContent = charts[0]?.data?.labels?.[index] || `Sample ${index + 1}`;
        charts.forEach((chart, chartIndex) => {
            const dataset = chart.data.datasets[0];
            const numeric = Number(dataset?.data?.[index]);
            group.chips[chartIndex].textContent = `${dataset?.label || "Metric"}: ${Number.isFinite(numeric) ? numeric.toLocaleString(undefined, { maximumFractionDigits: 2 }) : "-"}`;
        });
        readout.classList.add("show");
    };

    const createMainSyncGroup = (groupId, chartIds, readoutId) => {
        clearSyncGroup(groupId);
        const charts = chartIds.map(id => instances[id]).filter(Boolean);
        const readout = document.getElementById(readoutId);
        if (charts.length < 2) return;
        alignYAxes(charts);
        const group = { readout, charts, listeners: [], index: -1 };
        const show = perFrame(index => {
            if (index === group.index) return;
            group.index = index;
            charts.forEach(item => { item.$syncCursor = index < 0 ? null : { mode: "index", value: index }; paintSyncCursor(item); });
            if (index < 0) readout?.classList.remove("show");
            else updateMainSyncReadout(group, index);
        });
        const first = charts[0];
        const container = first.canvas.closest(".chart-grid") || first.canvas.parentElement;
        listenForSync(group, container,
            event => show(Math.max(0, Math.min(first.data.labels.length - 1, Math.round(pointerValue(first, event.clientX))))),
            () => show(-1));
        syncGroups[groupId] = group;
    };
    const clampZoomWindow = (requestedMin, requestedMax, fullMin, fullMax, minimumWidth) => {
        const fullWidth = fullMax - fullMin;
        if (!Number.isFinite(fullWidth) || fullWidth <= 0)
            return { min: fullMin, max: fullMax };
        const width = Math.max(Math.min(minimumWidth, fullWidth), Math.min(requestedMax - requestedMin, fullWidth));
        const min = Math.max(fullMin, Math.min(requestedMin, fullMax - width));
        return { min, max: min + width };
    };

    const attachExpandedZoom = id => {
        const chart = instances[id];
        const canvas = chart?.canvas;
        const xScale = chart?.scales?.x;
        if (!chart || !canvas || !xScale) return;

        const fullX = { min: Number(xScale.min), max: Number(xScale.max) };
        const minimumXWidth = Math.min(2, Math.max(1, fullX.max - fullX.min));
        let pendingFactor = 1;
        let pendingCenter = null;

        // Wheel ticks arriving within one frame are combined into a single zoom and redraw.
        const apply = perFrame(() => {
            const currentX = {
                min: Number(chart.options.scales.x.min ?? chart.scales.x.min),
                max: Number(chart.options.scales.x.max ?? chart.scales.x.max),
            };
            const nextX = clampZoomWindow(
                pendingCenter - (pendingCenter - currentX.min) * pendingFactor,
                pendingCenter + (currentX.max - pendingCenter) * pendingFactor,
                fullX.min,
                fullX.max,
                minimumXWidth);
            pendingFactor = 1;
            chart.options.scales.x.min = nextX.min;
            chart.options.scales.x.max = nextX.max;
            chart.update("none");
        });

        const wheel = event => {
            event.preventDefault();
            const rect = canvas.getBoundingClientRect();
            const pointerX = Math.max(chart.chartArea.left, Math.min(chart.chartArea.right, event.clientX - rect.left));
            pendingCenter = chart.scales.x.getValueForPixel(pointerX);
            pendingFactor *= event.deltaY < 0 ? 0.72 : 1.4;
            apply();
        };

        expandedZoomStates[id] = { canvas, wheel };
        canvas.addEventListener("wheel", wheel, { passive: false });
        canvas.style.cursor = "crosshair";
    };

    const openModalFrom = (modalCanvasId, sourceId) => {
        if (!ensureChartJs()) return;
        const ctx = document.getElementById(modalCanvasId);
        const cached = cache[sourceId];
        if (!cached || !ctx) return;
        const render = () => {
            destroy(modalCanvasId);
            if (cached.kind === "line") {
                renderLine(modalCanvasId, cached.labels, cached.data, cached.label, cached.color, cached.threshold, true);
            } else if (cached.kind === "timeline") {
                renderTimeline(modalCanvasId, cached.segments, cached.title, cached.highlights);
            }
            if (instances[modalCanvasId]) {
                instances[modalCanvasId].resize();
                if (cached.kind === "line") attachExpandedZoom(modalCanvasId);
            }
        };
        requestAnimationFrame(() => requestAnimationFrame(render));
    };
    const renderTimeline = (id, segments, title, highlights) => {
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

        cache[id] = { kind: "timeline", segments, title, highlights };
        const inspector = ensureTimelineInspector(id, ctx);
        const labels = [...new Set(normalized.map(segment => segment.label))];
        const fullMax = Math.max(...normalized.map(segment => segment.endSeconds));
        const state = {
            canvas: ctx,
            chart: null,
            inspector,
            segments: normalized,
            highlights: normalizeHighlights(highlights),
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
                // Hover, drag and wheel are handled by attachTimelineInteractions (once per frame) instead of Chart.js events.
                events: [],
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
            plugins: [inRangeShadePlugin, timelineCursorPlugin],
        });
        chart.$inRangeWindows = state.highlights;
        chart.update("none");
        instances[id] = chart;
        state.chart = chart;
        timelineStates[id] = state;
        attachTimelineInteractions(state);
        setTimelineCursor(state, 0);
    };

    const reportBackgroundPlugin = {
        id: "reportBackground",
        beforeDraw(chart) {
            const { ctx: chartContext, width, height } = chart;
            chartContext.save();
            chartContext.globalCompositeOperation = "destination-over";
            chartContext.fillStyle = "#ffffff";
            chartContext.fillRect(0, 0, width, height);
            chartContext.restore();
        },
    };

    const reportScaleStyle = (title, beginAtZero = false) => ({
        beginAtZero,
        title: title ? {
            display: true,
            text: title,
            color: "#334155",
            font: { size: 21, weight: "600" },
            padding: { top: 10 },
        } : undefined,
        ticks: {
            color: "#475569",
            font: { size: 18 },
            padding: 9,
            maxRotation: 35,
            minRotation: 0,
        },
        grid: { color: "rgba(148,163,184,0.28)", lineWidth: 1.2 },
        border: { color: "#94a3b8" },
    });

    const exportImage = (sourceId, requestedWidth = 1800, requestedHeight = 620) => {
        if (!ensureChartJs()) return null;
        const source = cache[sourceId];
        if (!source) return null;
        const canvas = document.createElement("canvas");
        canvas.width = Math.max(800, Number(requestedWidth) || 1800);
        canvas.height = Math.max(400, Number(requestedHeight) || 620);

        let config;
        if (source.kind === "line") {
            config = {
                type: "line",
                data: {
                    labels: source.labels,
                    datasets: [{
                        label: source.label || "Series",
                        data: source.data,
                        borderColor: source.color || "#3b82f6",
                        backgroundColor: source.color || "#3b82f6",
                        borderWidth: 4,
                        pointRadius: 1.6,
                        pointHoverRadius: 3,
                        tension: 0.18,
                        fill: false,
                        ...thresholdDatasetStyle(source.threshold, source.color || "#3b82f6"),
                    }],
                },
                options: {
                    responsive: false,
                    animation: false,
                    devicePixelRatio: 1,
                    layout: { padding: { top: 18, right: 28, bottom: 10, left: 12 } },
                    plugins: {
                        legend: {
                            display: true,
                            labels: { color: "#0f172a", font: { size: 22, weight: "600" }, boxWidth: 42, padding: 18 },
                        },
                    },
                    scales: {
                        x: {
                            ...reportScaleStyle(null),
                            ticks: { ...reportScaleStyle(null).ticks, autoSkip: true, maxTicksLimit: 11 },
                        },
                        y: { ...reportScaleStyle(null), ...thresholdScale(source.threshold) },
                    },
                },
                plugins: [reportBackgroundPlugin, thresholdPlugin],
            };
        } else if (source.kind === "timeline") {
            const normalized = (source.segments || []).map((segment, index) => ({
                label: segment.label ?? segment.Label ?? "Unknown",
                startSeconds: Number(segment.startSeconds ?? segment.StartSeconds ?? 0),
                endSeconds: Number(segment.endSeconds ?? segment.EndSeconds ?? 0),
                color: timelineColor(segment.label ?? segment.Label ?? "Unknown", index),
            }));
            const labels = [...new Set(normalized.map(segment => segment.label))];
            config = {
                type: "bar",
                data: {
                    labels,
                    datasets: [{
                        data: normalized.map(segment => ({ x: [segment.startSeconds, segment.endSeconds], y: segment.label })),
                        backgroundColor: normalized.map(segment => segment.color),
                        borderColor: normalized.map(segment => segment.color),
                        borderWidth: 2,
                        borderSkipped: false,
                        borderRadius: 5,
                        barPercentage: 0.7,
                        categoryPercentage: 0.82,
                        maxBarThickness: 46,
                        minBarLength: 5,
                    }],
                },
                options: {
                    indexAxis: "y",
                    responsive: false,
                    animation: false,
                    devicePixelRatio: 1,
                    layout: { padding: { top: 18, right: 28, bottom: 8, left: 12 } },
                    plugins: { legend: { display: false } },
                    scales: {
                        x: {
                            ...reportScaleStyle("Elapsed test time"),
                            min: 0,
                            max: Math.max(...normalized.map(segment => segment.endSeconds), 1),
                            ticks: {
                                ...reportScaleStyle("Elapsed test time").ticks,
                                callback: tickValue => formatElapsed(tickValue),
                                maxRotation: 0,
                                maxTicksLimit: 12,
                            },
                        },
                        y: {
                            ticks: { color: "#334155", font: { size: 20, weight: "600" }, padding: 12 },
                            grid: { display: false },
                            border: { display: false },
                        },
                    },
                },
                plugins: [reportBackgroundPlugin, inRangeShadePlugin],
            };
        } else {
            return null;
        }

        const chart = new Chart(canvas, config);
        if (source.kind === "line") {
            chart.$threshold = source.threshold;
            chart.$reportScale = true;
            chart.update("none");
        }
        if (source.kind === "timeline") {
            chart.$inRangeWindows = normalizeHighlights(source.highlights);
            chart.$inRangeColor = "rgba(34,197,94,0.18)";
            chart.update("none");
        }
        chart.draw();
        const result = { data: canvas.toDataURL("image/png"), width: canvas.width, height: canvas.height };
        chart.destroy();
        return result;
    };

    const comparisonValue = (item, camelName, pascalName) => item?.[camelName] ?? item?.[pascalName];

    const comparisonScales = (commonMax, yTitle) => ({
        x: {
            type: "linear",
            min: 0,
            max: commonMax,
            title: { display: true, text: "Elapsed test time", color: "#94a3b8" },
            ticks: { color: "#94a3b8", maxTicksLimit: 10, callback: value => formatElapsed(value) },
            grid: { color: "rgba(148,163,184,.12)" },
        },
        y: {
            title: { display: true, text: yTitle, color: "#94a3b8" },
            ticks: { color: "#94a3b8" },
            grid: { color: "rgba(148,163,184,.12)" },
            afterFit: fixedYAxisWidth,
        },
    });

    const renderComparisonLine = (id, runs, valueName, label, commonMax) => {
        const canvas = document.getElementById(id);
        if (!canvas) return null;
        destroy(id);
        const datasets = runs.map(run => ({
            label: run.name,
            data: run.samples.map(sample => ({
                x: Number(comparisonValue(sample, "elapsedSeconds", "ElapsedSeconds")),
                y: Number(comparisonValue(sample, valueName, valueName[0].toUpperCase() + valueName.slice(1))),
            })).filter(point => Number.isFinite(point.x) && Number.isFinite(point.y)),
            borderColor: run.color,
            backgroundColor: run.color,
            borderWidth: 1.5,
            pointRadius: 0,
            pointHoverRadius: 0,
            tension: 0,
            spanGaps: true,
        }));
        instances[id] = new Chart(canvas, {
            type: "line",
            data: { datasets },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                normalized: true,
                // Only legend clicks; hover is drawn by the synced cursor once per frame.
                events: ["click"],
                plugins: {
                    legend: { labels: { color: "#cbd5e1", usePointStyle: true, pointStyle: "line" } },
                    tooltip: { enabled: false },
                },
                scales: comparisonScales(commonMax, label),
            },
            plugins: [syncCursorPlugin],
        });
        instances[id].$syncTooltip = true;
        return instances[id];
    };

    const renderComparisonCapping = (id, runs, commonMax) => {
        const canvas = document.getElementById(id);
        if (!canvas) return null;
        destroy(id);
        const reasons = [...new Set(runs.flatMap(run => run.cappingSegments.map(segment => comparisonValue(segment, "reason", "Reason"))))].filter(Boolean);
        const labels = runs.flatMap(run => reasons
            .filter(reason => run.cappingSegments.some(segment => comparisonValue(segment, "reason", "Reason") === reason))
            .map(reason => `${run.name} · ${reason}`));
        const datasets = reasons.map((reason, reasonIndex) => ({
            label: reason,
            data: runs.flatMap(run => run.cappingSegments
                .filter(segment => comparisonValue(segment, "reason", "Reason") === reason)
                .map(segment => ({
                    x: [
                        Number(comparisonValue(segment, "startSeconds", "StartSeconds")),
                        Number(comparisonValue(segment, "endSeconds", "EndSeconds")),
                    ],
                    y: `${run.name} · ${reason}`,
                }))),
            backgroundColor: timelineColor(reason, reasonIndex),
            borderColor: timelineColor(reason, reasonIndex),
            borderWidth: 1,
            borderSkipped: false,
            borderRadius: 4,
            barPercentage: .7,
            categoryPercentage: .76,
            minBarLength: 2,
        }));
        instances[id] = new Chart(canvas, {
            type: "bar",
            data: { labels, datasets },
            options: {
                indexAxis: "y",
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                plugins: {
                    legend: { display: true, labels: { color: "#cbd5e1", usePointStyle: true, boxWidth: 8 } },
                    tooltip: { callbacks: { label: context => `${context.dataset.label}: ${formatElapsed(context.raw.x[0])}–${formatElapsed(context.raw.x[1])}` } },
                },
                scales: {
                    x: comparisonScales(commonMax, "").x,
                    y: { ticks: { color: "#cbd5e1" }, grid: { display: false }, stacked: true, afterFit: fixedYAxisWidth },
                },
            },
            plugins: [syncCursorPlugin],
        });
        return instances[id];
    };

    const renderComparison = rawRuns => {
        if (!ensureChartJs()) return;
        clearSyncGroup("comparison");
        if (!Array.isArray(rawRuns)) {
            console.error("Comparison chart data must be an array of runs.");
            return;
        }
        const runs = (rawRuns || []).map((run, index) => ({
            name: comparisonValue(run, "name", "Name") || `Run ${index + 1}`,
            color: comparisonValue(run, "color", "Color") || timelineColor("", index),
            samples: comparisonValue(run, "samples", "Samples") || [],
            cappingSegments: comparisonValue(run, "cappingSegments", "CappingSegments") || [],
        }));
        const commonMax = Math.max(1, ...runs.flatMap(run => [
            ...run.samples.map(sample => Number(comparisonValue(sample, "elapsedSeconds", "ElapsedSeconds")) || 0),
            ...run.cappingSegments.map(segment => Number(comparisonValue(segment, "endSeconds", "EndSeconds")) || 0),
        ]));
        const charts = [
            renderComparisonLine("comparison-gpu-temperature", runs, "gpuTemperature", "GPU temperature (°C)", commonMax),
            renderComparisonLine("comparison-memory-temperature", runs, "memoryTemperature", "Memory temperature (°C)", commonMax),
            renderComparisonLine("comparison-power", runs, "power", "Power (W)", commonMax),
            renderComparisonLine("comparison-gpc-clock", runs, "gpcClock", "GPC clock (MHz)", commonMax),
            renderComparisonLine("comparison-memory-clock", runs, "memoryClock", "Memory clock (MHz)", commonMax),
            renderComparisonCapping("comparison-capping", runs, commonMax),
        ].filter(Boolean);
        const show = perFrame(elapsed => {
            charts.forEach(item => { item.$syncCursor = elapsed === null ? null : { mode: "value", value: elapsed }; paintSyncCursor(item); });
        });
        if (!charts.length) return;
        alignYAxes(charts);
        const group = { charts, listeners: [] };
        const first = charts[0];
        const container = first.canvas.closest("section") || first.canvas.parentElement;
        listenForSync(group, container,
            event => show(Math.max(0, Math.min(commonMax, pointerValue(first, event.clientX) || 0))),
            () => show(null));
        syncGroups.comparison = group;
    };

    return { renderLine, renderTimeline, timelineCommand, createMainSyncGroup, renderComparison, exportImage, destroy, openModalFrom };
})();
