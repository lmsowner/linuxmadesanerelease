window.lmsFilePreview = (() => {
    const previewStates = new WeakMap();
    const FIT_PADDING = 28;
    const MIN_SCALE = 0.1;
    const MAX_SCALE = 8;
    const ZOOM_FACTOR = 1.2;
    let pdfWorkerConfigured = false;

    function dispose(host) {
        if (!host) {
            return false;
        }

        const existing = previewStates.get(host);
        if (existing) {
            existing.dispose?.();
            if (existing.objectUrl) {
                URL.revokeObjectURL(existing.objectUrl);
            }
            existing.renderTask?.cancel?.();
            existing.loadingTask?.destroy?.();
            existing.pdfDocument?.destroy?.();
        }

        previewStates.delete(host);
        host.innerHTML = "";
        return true;
    }

    async function load(host, fileName, contentType, streamReference, pageIndex, dotNetRef) {
        if (!host || !streamReference) {
            return buildResult();
        }

        dispose(host);

        const arrayBuffer = await streamReference.arrayBuffer();
        const normalizedContentType = (contentType || "").toLowerCase();

        if (normalizedContentType === "image/tiff") {
            return loadTiff(host, arrayBuffer, Math.max(0, pageIndex || 0), dotNetRef);
        }

        if (normalizedContentType === "application/pdf") {
            return loadPdf(host, arrayBuffer, Math.max(0, pageIndex || 0), dotNetRef);
        }

        const blob = new Blob([arrayBuffer], { type: contentType || "application/octet-stream" });
        const objectUrl = URL.createObjectURL(blob);

        const wrapper = document.createElement("div");
        wrapper.className = "file-visual-preview-image-wrap";

        const surface = document.createElement("div");
        surface.className = "file-visual-preview-pan-surface";

        const image = document.createElement("img");
        image.className = "file-visual-preview-image";
        image.src = objectUrl;
        image.alt = fileName || "Image preview";

        surface.appendChild(image);
        wrapper.appendChild(surface);
        host.appendChild(wrapper);

        await waitForImage(image);

        const state = createZoomableState("image", host, wrapper, image, objectUrl, image.naturalWidth, image.naturalHeight, dotNetRef);
        previewStates.set(host, state);
        await applyFit(state);
        return buildResult(state);
    }

    async function showPage(host, pageIndex) {
        const state = previewStates.get(host);
        if (!host || !state) {
            return buildResult();
        }

        if (state.kind === "tiff") {
            return renderTiffPage(state, pageIndex);
        }

        if (state.kind === "pdf") {
            return renderPdfPage(state, pageIndex, true);
        }

        return buildResult(state);
    }

    async function zoomIn(host) {
        const state = previewStates.get(host);
        if (!state || !isZoomable(state)) {
            return buildResult(state);
        }

        state.fitMode = false;
        return setScale(state, clamp(state.currentScale * ZOOM_FACTOR, MIN_SCALE, MAX_SCALE));
    }

    async function zoomOut(host) {
        const state = previewStates.get(host);
        if (!state || !isZoomable(state)) {
            return buildResult(state);
        }

        state.fitMode = false;
        return setScale(state, clamp(state.currentScale / ZOOM_FACTOR, MIN_SCALE, MAX_SCALE));
    }

    async function fit(host) {
        const state = previewStates.get(host);
        if (!state || !isZoomable(state)) {
            return buildResult(state);
        }

        return applyFit(state);
    }

    async function actualSize(host) {
        const state = previewStates.get(host);
        if (!state || !isZoomable(state)) {
            return buildResult(state);
        }

        state.fitMode = false;
        return setScale(state, 1);
    }

    async function loadPdf(host, arrayBuffer, pageIndex, dotNetRef) {
        const pdfjsLib = globalThis.pdfjsLib;
        const pdfjsViewer = globalThis.pdfjsViewer;
        if (!pdfjsLib || !pdfjsViewer) {
            throw new Error("PDF preview support is not loaded.");
        }

        if (!pdfWorkerConfigured) {
            pdfjsLib.GlobalWorkerOptions.workerSrc = "/lib/pdfjs/pdf.worker.min.js";
            pdfWorkerConfigured = true;
        }

        const wrapper = document.createElement("div");
        wrapper.className = "file-visual-preview-pdf-wrap";
        wrapper.tabIndex = 0;
        wrapper.style.position = "absolute";
        wrapper.style.inset = "0";
        wrapper.style.overflow = "auto";

        const viewer = document.createElement("div");
        viewer.className = "pdfViewer";
        wrapper.appendChild(viewer);
        host.appendChild(wrapper);

        const loadingTask = pdfjsLib.getDocument({ data: arrayBuffer });
        const pdfDocument = await loadingTask.promise;
        const eventBus = new pdfjsViewer.EventBus();
        const linkService = new pdfjsViewer.PDFLinkService({ eventBus });
        const pdfViewer = new pdfjsViewer.PDFViewer({
            container: wrapper,
            viewer,
            eventBus,
            linkService,
            textLayerMode: 0,
            annotationMode: 0,
            removePageBorders: true
        });
        linkService.setViewer(pdfViewer);

        const state = createZoomableState("pdf", host, wrapper, viewer, null, 1, 1, dotNetRef);
        state.loadingTask = loadingTask;
        state.pdfDocument = pdfDocument;
        state.pdfViewer = pdfViewer;
        state.linkService = linkService;
        state.eventBus = eventBus;
        state.pageCount = pdfDocument.numPages;
        state.currentScale = 1;
        state.fitScale = 1;
        linkService.setDocument(pdfDocument, null);
        pdfViewer.setDocument(pdfDocument);

        const onPageChanging = event => {
            state.currentPageIndex = Math.max(0, (event.pageNumber || 1) - 1);
            notifyState(state);
        };
        const onScaleChanging = event => {
            state.currentScale = event.scale || state.currentScale || 1;
            notifyState(state);
        };
        eventBus.on("pagechanging", onPageChanging);
        eventBus.on("scalechanging", onScaleChanging);
        state.dispose = chainDispose(state.dispose, () => {
            eventBus.off("pagechanging", onPageChanging);
            eventBus.off("scalechanging", onScaleChanging);
        });
        previewStates.set(host, state);

        await pdfViewer.pagesPromise;
        await updatePdfNaturalSize(state, pageIndex);
        if (pageIndex > 0) {
            pdfViewer.currentPageNumber = pageIndex + 1;
        }
        state.currentPageIndex = Math.max(0, pdfViewer.currentPageNumber - 1);
        await applyFit(state, state.currentPageIndex);
        return buildResult(state);
    }

    function loadTiff(host, arrayBuffer, pageIndex, dotNetRef) {
        if (typeof UTIF === "undefined") {
            throw new Error("TIFF preview support is not loaded.");
        }

        const ifds = UTIF.decode(arrayBuffer);
        if (!Array.isArray(ifds) || ifds.length === 0) {
            throw new Error("This TIFF file does not contain any readable pages.");
        }

        const wrapper = document.createElement("div");
        wrapper.className = "file-visual-preview-tiff-wrap";

        const surface = document.createElement("div");
        surface.className = "file-visual-preview-pan-surface";

        const canvas = document.createElement("canvas");
        canvas.className = "file-visual-preview-tiff-canvas";
        surface.appendChild(canvas);
        wrapper.appendChild(surface);
        host.appendChild(wrapper);

        const state = createZoomableState("tiff", host, wrapper, canvas, null, 1, 1, dotNetRef);
        state.arrayBuffer = arrayBuffer;
        state.ifds = ifds;
        state.pageCount = ifds.length;
        previewStates.set(host, state);

        return renderTiffPage(state, pageIndex);
    }

    async function renderPdfPage(state, pageIndex) {
        const safePageIndex = clamp(pageIndex, 0, Math.max(0, state.pageCount - 1));
        state.pdfViewer.currentPageNumber = safePageIndex + 1;
        state.currentPageIndex = safePageIndex;
        await updatePdfNaturalSize(state, safePageIndex);
        if (state.fitMode) {
            return applyFit(state, safePageIndex);
        }
        notifyState(state);
        return buildResult(state);
    }

    function renderTiffPage(state, pageIndex) {
        const safePageIndex = clamp(pageIndex, 0, Math.max(0, state.pageCount - 1));
        const ifd = state.ifds[safePageIndex];
        UTIF.decodeImage(state.arrayBuffer, ifd);

        const rgba = UTIF.toRGBA8(ifd);
        const width = ifd.width;
        const height = ifd.height;
        const imageData = new ImageData(new Uint8ClampedArray(rgba), width, height);
        const canvas = state.mediaElement;
        canvas.width = width;
        canvas.height = height;

        const context = canvas.getContext("2d");
        if (!context) {
            throw new Error("The browser could not initialize the TIFF preview canvas.");
        }

        context.putImageData(imageData, 0, 0);

        state.naturalWidth = width;
        state.naturalHeight = height;
        state.currentPageIndex = safePageIndex;

        if (state.fitMode || !state.currentScale) {
            return applyFit(state);
        }

        return setScale(state, state.currentScale);
    }

    function createZoomableState(kind, host, wrapper, mediaElement, objectUrl, naturalWidth, naturalHeight, dotNetRef) {
        const cleanupCallbacks = [];
        const state = {
            kind,
            host,
            wrapper,
            mediaElement,
            objectUrl,
            dotNetRef,
            naturalWidth,
            naturalHeight,
            currentScale: 1,
            fitScale: 1,
            fitMode: true,
            pageCount: 1,
            currentPageIndex: 0,
            renderTask: null,
            dispose: () => {
                for (const callback of cleanupCallbacks) {
                    callback();
                }
            }
        };

        cleanupCallbacks.push(attachPanHandlers(state));
        cleanupCallbacks.push(attachResizeWatcher(state));
        return state;
    }

    function attachPanHandlers(state) {
        const wrapper = state.wrapper;
        let dragging = false;
        let startX = 0;
        let startY = 0;
        let startScrollLeft = 0;
        let startScrollTop = 0;

        function onPointerDown(event) {
            if (state.currentScale <= state.fitScale + 0.001) {
                return;
            }

            dragging = true;
            wrapper.classList.add("dragging");
            startX = event.clientX;
            startY = event.clientY;
            startScrollLeft = wrapper.scrollLeft;
            startScrollTop = wrapper.scrollTop;
            wrapper.setPointerCapture?.(event.pointerId);
        }

        function onPointerMove(event) {
            if (!dragging) {
                return;
            }

            wrapper.scrollLeft = startScrollLeft - (event.clientX - startX);
            wrapper.scrollTop = startScrollTop - (event.clientY - startY);
        }

        function onPointerUp(event) {
            if (!dragging) {
                return;
            }

            dragging = false;
            wrapper.classList.remove("dragging");
            wrapper.releasePointerCapture?.(event.pointerId);
        }

        wrapper.addEventListener("pointerdown", onPointerDown);
        wrapper.addEventListener("pointermove", onPointerMove);
        wrapper.addEventListener("pointerup", onPointerUp);
        wrapper.addEventListener("pointercancel", onPointerUp);
        wrapper.addEventListener("lostpointercapture", onPointerUp);

        return () => {
            wrapper.removeEventListener("pointerdown", onPointerDown);
            wrapper.removeEventListener("pointermove", onPointerMove);
            wrapper.removeEventListener("pointerup", onPointerUp);
            wrapper.removeEventListener("pointercancel", onPointerUp);
            wrapper.removeEventListener("lostpointercapture", onPointerUp);
        };
    }

    function attachResizeWatcher(state) {
        if (typeof ResizeObserver === "undefined") {
            return () => {};
        }

        let resizeFrame = 0;
        const observer = new ResizeObserver(() => {
            if (!state.fitMode) {
                return;
            }

            cancelAnimationFrame(resizeFrame);
            resizeFrame = requestAnimationFrame(() => {
                applyFit(state).catch(() => {});
            });
        });

        observer.observe(state.host);
        return () => {
            cancelAnimationFrame(resizeFrame);
            observer.disconnect();
        };
    }

    async function applyFit(state, pageOverride = null) {
        state.fitMode = true;
        if (state.kind === "pdf") {
            if (pageOverride !== null) {
                state.pdfViewer.currentPageNumber = clamp(pageOverride, 0, Math.max(0, state.pageCount - 1)) + 1;
                state.currentPageIndex = Math.max(0, state.pdfViewer.currentPageNumber - 1);
            }

            await updatePdfNaturalSize(state, state.currentPageIndex);
            state.pdfViewer.currentScaleValue = "page-fit";
            await nextAnimationFrame();
            state.fitScale = state.currentScale || state.fitScale || 1;
            setWrapperOverflow(state);
            notifyState(state);
            return buildResult(state);
        }

        const availableWidth = Math.max(1, state.host.clientWidth - FIT_PADDING);
        const availableHeight = Math.max(1, state.host.clientHeight - FIT_PADDING);
        const fitScale = Math.min(
            availableWidth / Math.max(1, state.naturalWidth),
            availableHeight / Math.max(1, state.naturalHeight),
            1);
        const nextScale = clamp(fitScale, MIN_SCALE, MAX_SCALE);

        if (Math.abs(nextScale - state.fitScale) < 0.001 && Math.abs(nextScale - state.currentScale) < 0.001) {
            setWrapperOverflow(state);
            return buildResult(state);
        }

        state.fitScale = nextScale;
        return setScale(state, nextScale, pageOverride, true);
    }

    async function setScale(state, scale, pageOverride = null, centerContent = false) {
        const safeScale = clamp(scale, MIN_SCALE, MAX_SCALE);

        if (state.kind === "pdf") {
            return setPdfScale(state, safeScale);
        }

        applyCssScale(state, safeScale, centerContent);
        return buildResult(state);
    }

    function applyCssScale(state, scale, centerContent) {
        const wrapper = state.wrapper;
        const previousCenterX = wrapper.scrollLeft + (wrapper.clientWidth / 2);
        const previousCenterY = wrapper.scrollTop + (wrapper.clientHeight / 2);
        const previousScale = state.currentScale || scale;

        state.currentScale = scale;
        state.mediaElement.style.width = `${Math.max(1, Math.round(state.naturalWidth * scale))}px`;
        state.mediaElement.style.height = `${Math.max(1, Math.round(state.naturalHeight * scale))}px`;
        setWrapperOverflow(state);

        if (centerContent || scale <= state.fitScale + 0.001) {
            wrapper.scrollLeft = 0;
            wrapper.scrollTop = 0;
            return;
        }

        const scaleRatio = scale / previousScale;
        wrapper.scrollLeft = Math.max(0, (previousCenterX * scaleRatio) - (wrapper.clientWidth / 2));
        wrapper.scrollTop = Math.max(0, (previousCenterY * scaleRatio) - (wrapper.clientHeight / 2));
    }

    function setPdfScale(state, scale) {
        state.fitMode = false;
        state.currentScale = scale;
        state.pdfViewer.currentScale = scale;
        setWrapperOverflow(state);
        notifyState(state);
        return buildResult(state);
    }

    function setWrapperOverflow(state) {
        if (state.kind === "pdf") {
            state.wrapper.style.overflow = "auto";
            return;
        }

        state.wrapper.style.overflow = state.currentScale <= state.fitScale + 0.001 ? "hidden" : "auto";
    }

    function isZoomable(state) {
        return state.kind === "image" || state.kind === "tiff" || state.kind === "pdf";
    }

    function notifyState(state) {
        if (!state?.dotNetRef) {
            return;
        }

        state.dotNetRef.invokeMethodAsync(
            "SyncViewerState",
            state.currentPageIndex,
            state.pageCount || 1,
            Math.round((state.currentScale || 1) * 100))
            .catch(() => {});
    }

    function chainDispose(existingDispose, extraDispose) {
        return () => {
            existingDispose?.();
            extraDispose?.();
        };
    }

    function buildResult(state = null) {
        return {
            pageCount: state?.pageCount ?? 1,
            currentPageIndex: state?.currentPageIndex ?? 0,
            zoomPercent: Math.round((state?.currentScale ?? 1) * 100)
        };
    }

    function clamp(value, min, max) {
        return Math.min(max, Math.max(min, value));
    }

    function waitForImage(image) {
        if (image.complete && image.naturalWidth > 0) {
            return Promise.resolve();
        }

        return new Promise((resolve, reject) => {
            image.addEventListener("load", () => resolve(), { once: true });
            image.addEventListener("error", () => reject(new Error("The browser could not decode this image preview.")), { once: true });
        });
    }

    async function updatePdfNaturalSize(state, pageIndex) {
        const pdfDocument = state.pdfDocument;
        if (!pdfDocument) {
            return;
        }

        const page = await pdfDocument.getPage(Math.max(1, pageIndex + 1));
        const viewport = page.getViewport({ scale: 1 });
        state.naturalWidth = viewport.width;
        state.naturalHeight = viewport.height;
    }

    function nextAnimationFrame() {
        return new Promise(resolve => requestAnimationFrame(() => resolve()));
    }

    return {
        load,
        showPage,
        zoomIn,
        zoomOut,
        fit,
        actualSize,
        dispose
    };
})();
