/* Copyright (c) 2026 LMS Developer. All rights reserved.
 * Applicable licence and release scope: see LICENSE.md for details. */
window.lmsDocumentThumbnails = (() => {
    const states = new WeakMap(), observers = new WeakMap();
    function release(host) {
        const state = states.get(host); states.delete(host);
        state?.reject?.(new Error("The document thumbnail was closed."));
        state?.renderTask?.cancel();
        const destroy = state?.pdf ? state.pdf.destroy() : state?.loadingTask?.destroy();
        Promise.resolve(destroy).catch(() => {});
    }
    function dispose(host) { observers.get(host)?.disconnect(); observers.delete(host); release(host); }
    function observe(host, dotnet) {
        dispose(host);
        const observer = new IntersectionObserver(entries => {
            for (const entry of entries) {
                if (entry.isIntersecting) dotnet.invokeMethodAsync("LoadVisibleAsync").catch(() => {});
                else { release(host); dotnet.invokeMethodAsync("CancelInvisible").catch(() => {}); }
            }
        }, { rootMargin: "120px" });
        observers.set(host, observer); observer.observe(host);
    }
    async function render(host, name, dotnet, identity, length, pageIndex, maxDimension) {
        release(host);
        const state = {}; states.set(host, state);
        const failed = new Promise((_, reject) => state.reject = reject);
        failed.catch(() => {});
        const timeout = setTimeout(() => state.reject(new Error("This page took too long to thumbnail.")), 30000);
        const read = async (offset, count) => {
            if (states.get(host) !== state) throw new Error("The document thumbnail was closed.");
            if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(count) || offset < 0 || count < 1 || offset + count > length)
                throw new Error("Invalid document byte range.");
            const result = new Uint8Array(count);
            for (let n = 0; n < count; n += 1048576) result.set(await dotnet.invokeMethodAsync("ReadRangeAsync", identity, offset + n, Math.min(1048576, count - n)), n);
            return result;
        };
        try {
            if (/\.pdf$/i.test(name)) {
                pdfjsLib.GlobalWorkerOptions.workerSrc = new URL("lib/pdfjs/pdf.worker.min.js", document.baseURI).href;
                const initial = await read(0, Math.min(length, 65536));
                const transport = new pdfjsLib.PDFDataRangeTransport(length, initial);
                transport.requestDataRange = (begin, end) => {
                    read(begin, end - begin).then(bytes => transport.onDataRange(begin, bytes)).catch(error => {
                        state.rangeError = error; state.reject(error);
                    });
                };
                state.loadingTask = pdfjsLib.getDocument({ range: transport, length, rangeChunkSize: 65536,
                    disableAutoFetch: true, disableStream: true, isEvalSupported: false, maxImageSize: 16777216 });
                state.pdf = await Promise.race([state.loadingTask.promise, failed]);
                state.pageCount = state.pdf.numPages;
            } else { throw new Error("TIFF thumbnails are rendered by the server."); }
        if (!state || states.get(host) !== state || pageIndex < 0 || pageIndex >= state.pageCount)
            throw new Error("This document page is unavailable. Try again.");
        const canvas = document.createElement("canvas");
        if (state.pdf) {
            const page = await Promise.race([state.pdf.getPage(pageIndex + 1), failed]);
            const natural = page.getViewport({ scale: 1 });
            const viewport = page.getViewport({ scale: Math.min(maxDimension / natural.width, maxDimension / natural.height) });
            canvas.width = Math.max(1, Math.ceil(viewport.width)); canvas.height = Math.max(1, Math.ceil(viewport.height));
            state.renderTask = page.render({ canvasContext: canvas.getContext("2d"), viewport, background: "white" });
            await Promise.race([state.renderTask.promise, failed]); state.renderTask = null; page.cleanup();
        }
        if (states.get(host) !== state) throw new Error("The document thumbnail was closed.");
        const jpeg = await new Promise(resolve => canvas.toBlob(resolve, "image/jpeg", 0.85));
        canvas.width = canvas.height = 0;
        if (!jpeg) throw new Error("Could not render a document thumbnail.");
        return { pageCount: state.pageCount, pageCountComplete: state.pageCountComplete ?? true, stream: DotNet.createJSStreamReference(jpeg) };
        } catch (error) { throw state.rangeError || error; }
        finally { clearTimeout(timeout); if (states.get(host) === state) release(host); }
    }
    return { observe, dispose, render };
})();
