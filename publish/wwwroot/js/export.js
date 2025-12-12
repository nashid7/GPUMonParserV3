window.gpuExport = (() => {
    const ensureDeps = () => {
        if (typeof html2canvas === "undefined" || typeof window.jspdf === "undefined") {
            console.warn("html2canvas or jsPDF not loaded");
            return false;
        }
        return true;
    };

    const captureChart = async (id) => {
        const el = document.getElementById(id);
        if (!el) return null;
        const canvas = await html2canvas(el, { scale: 2, useCORS: true, backgroundColor: "#ffffff" });
        return canvas.toDataURL("image/png");
    };

    const addTable = (pdf, title, rows, startY) => {
        if (!rows || !rows.length) return startY;
        pdf.setFontSize(12);
        pdf.text(title, 14, startY);
        startY += 4;
        const headers = Object.keys(rows[0]);
        const data = rows.map(r => headers.map(h => r[h] ?? ""));
        if (pdf.autoTable) {
            pdf.autoTable({
                head: [headers],
                body: data,
                startY,
                styles: { fontSize: 8 },
                theme: "grid",
                margin: { left: 14, right: 14 }
            });
            return pdf.lastAutoTable.finalY + 8;
        }
        return startY + 8;
    };

    const exportEngineeringPdf = async (summary, throttle, filteredLines, filename) => {
        if (!ensureDeps()) return;
        const { jsPDF } = window.jspdf;
        const pdf = new jsPDF("p", "mm", "a4");
        const pageWidth = pdf.internal.pageSize.getWidth();
        const margin = 14;

        // Title / summary
        pdf.setFontSize(18);
        pdf.text("GPU Monitor Log Report", margin, 18);
        pdf.setFontSize(10);
        pdf.text(`Generated: ${new Date().toLocaleString()}`, margin, 24);

        const v = (x) => (x === null || x === undefined || x === "" ? "-" : `${x}`);
        const summaryRows = [
            { Metric: "Date", Value: v(summary.Date) },
            { Metric: "Duration", Value: v(summary.Duration) },
            { Metric: "Coverage", Value: `${v(summary.Coverage)} (${v(summary.Rows)} rows)` },
            { Metric: "Filter", Value: v(summary.Filter) },
            { Metric: "Util column", Value: v(summary.UtilColumn) },
            { Metric: "Power column", Value: v(summary.PowerColumn) }
        ];
        let y = addTable(pdf, "Summary", summaryRows, 32);

        const metricRows = [
            { Metric: "GPU Temp (Avg/Max)", Value: `${v(summary.GpuTjAvg)} / ${v(summary.GpuTjMax)}` },
            { Metric: "Memory Temp (Avg/Max)", Value: `${v(summary.MemTjAvg)} / ${v(summary.MemTjMax)}` },
            { Metric: "Power W (Avg/Max)", Value: `${v(summary.PowerAvg)} / ${v(summary.PowerMax)}` },
            { Metric: "PCIe", Value: `${v(summary.PcieWidth)} / ${v(summary.PcieSpeed)}` },
            { Metric: "Throttle events", Value: v(summary.ThrottleEvents) }
        ];
        y = addTable(pdf, "Key metrics", metricRows, y + 2);

        if (throttle && throttle.length) {
            y = addTable(pdf, "Throttle breakdown", throttle, y + 4);
        }

        // Charts page
        pdf.addPage();
        pdf.setFontSize(14);
        pdf.text("Charts", margin, 18);
        const charts = [
            { id: "chart-gpu", label: "GPU Temp (C)" },
            { id: "chart-power", label: "Power (W)" },
            { id: "chart-scatter", label: "Power vs GPU Temp" }
        ];
        let cy = 26;
        for (const ch of charts) {
            const img = await captureChart(ch.id);
            if (img) {
                pdf.setFontSize(11);
                pdf.text(ch.label, margin, cy);
                cy += 4;
                pdf.addImage(img, "PNG", margin, cy, pageWidth - margin * 2, 60);
                cy += 68;
            }
        }

        pdf.save(filename || "gpu-log-report.pdf");
    };

    return { exportEngineeringPdf };
})();
