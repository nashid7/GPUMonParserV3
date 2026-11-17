window.gpuSettings = (() => {
    const presetsKey = "gpumon.presets.v1";
    const lastKey = "gpumon.settings.v1";

    const read = key => {
        try {
            return JSON.parse(localStorage.getItem(key));
        } catch {
            return null;
        }
    };

    const write = (key, value) => {
        try {
            localStorage.setItem(key, JSON.stringify(value));
        } catch {
            // Storage can be unavailable (private mode); settings then last for the session only.
        }
    };

    const load = () => ({ presets: read(presetsKey) || [], last: read(lastKey) });
    const savePresets = presets => write(presetsKey, presets || []);
    const saveLast = settings => write(lastKey, settings);

    const downloadText = (filename, text, mimeType) => {
        const blob = new Blob([text], { type: mimeType || "text/plain;charset=utf-8" });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    };

    return { load, savePresets, saveLast, downloadText };
})();
