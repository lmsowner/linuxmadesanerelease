/* Copyright (c) Richard D. Kiernan.
 * Licensed under the Business Source License 1.1. See LICENSE for details. */

window.lmsFileBrowser = (() => {
    const scrollWatchers = new WeakMap();
    const keyWatchers = new WeakMap();
    const uploadSelections = new Map();
    const uploadTargets = new Map();

    function disposeSuggestionScroll(element) {
        const watcher = scrollWatchers.get(element);
        if (!watcher) {
            return false;
        }

        watcher.dispose();
        scrollWatchers.delete(element);
        return true;
    }

    function watchSuggestionScroll(element, dotNetReference) {
        if (!element || !dotNetReference) {
            return false;
        }

        disposeSuggestionScroll(element);

        let callbackPending = false;
        const threshold = 28;

        async function handleScroll() {
            if (callbackPending) {
                return;
            }

            const distanceFromBottom = element.scrollHeight - element.scrollTop - element.clientHeight;
            if (distanceFromBottom > threshold) {
                return;
            }

            callbackPending = true;
            try {
                await dotNetReference.invokeMethodAsync("OnPathSuggestionListBottomReached");
            } catch {
            } finally {
                callbackPending = false;
            }
        }

        element.addEventListener("scroll", handleScroll, { passive: true });
        scrollWatchers.set(element, {
            dispose: () => element.removeEventListener("scroll", handleScroll)
        });

        return true;
    }

    function disposeSuggestionKeys(element) {
        const watcher = keyWatchers.get(element);
        if (!watcher) {
            return false;
        }

        watcher.dispose();
        keyWatchers.delete(element);
        return true;
    }

    function watchSuggestionKeys(element, dotNetReference) {
        if (!element || !dotNetReference) {
            return false;
        }

        disposeSuggestionKeys(element);

        const handledKeys = new Set(["ArrowDown", "ArrowUp", "Enter", "Tab"]);
        let callbackPending = false;

        async function handleKeyDown(event) {
            if (!handledKeys.has(event.key) || element.getAttribute("aria-expanded") !== "true") {
                return;
            }

            event.preventDefault();
            event.stopImmediatePropagation();

            if (callbackPending) {
                return;
            }

            callbackPending = true;
            try {
                await dotNetReference.invokeMethodAsync("OnPathSuggestionKeyDown", event.key);
            } catch {
            } finally {
                callbackPending = false;
            }
        }

        element.addEventListener("keydown", handleKeyDown, true);
        keyWatchers.set(element, {
            dispose: () => element.removeEventListener("keydown", handleKeyDown, true)
        });

        return true;
    }

    function scrollActiveSuggestionIntoView(element, selector) {
        if (!element) {
            return false;
        }

        const activeSelector = typeof selector === "string" && selector.trim().length > 0
            ? selector
            : ".host-file-path-suggestion.active, .ui-path-autocomplete-suggestion.active";
        const activeSuggestion = element.querySelector(activeSelector);
        if (!activeSuggestion) {
            return false;
        }

        activeSuggestion.scrollIntoView({ block: "nearest" });
        return true;
    }

    function focusPathInputAtEnd(element) {
        if (!element) {
            return false;
        }

        element.focus({ preventScroll: true });
        const valueLength = typeof element.value === "string" ? element.value.length : 0;
        if (typeof element.setSelectionRange === "function") {
            element.setSelectionRange(valueLength, valueLength);
        }

        return true;
    }

    function positionContextMenus() {
        const menus = document.querySelectorAll("[data-lms-context-menu='true']");
        for (const menu of menus) {
            positionContextMenu(menu);
        }

        return menus.length;
    }

    function refreshOpenLayoutSurfaces() {
        window.requestAnimationFrame(() => {
            positionContextMenus();
        });
    }

    function positionContextMenu(menu) {
        if (!(menu instanceof HTMLElement)) {
            return false;
        }

        const margin = 8;
        const viewportWidth = Math.max(document.documentElement.clientWidth || 0, window.innerWidth || 0);
        const viewportHeight = Math.max(document.documentElement.clientHeight || 0, window.innerHeight || 0);
        const wantedX = parseFiniteNumber(menu.dataset.contextX, margin);
        const wantedY = parseFiniteNumber(menu.dataset.contextY, margin);
        const maxMenuHeight = Math.max(160, viewportHeight - margin * 2);

        menu.style.maxHeight = `${maxMenuHeight}px`;
        const origin = getPositioningOrigin(menu);
        menu.style.left = "0px";
        menu.style.top = "0px";

        const menuWidth = Math.ceil(menu.offsetWidth || 212);
        const menuHeight = Math.min(Math.ceil(menu.scrollHeight || menu.offsetHeight || 0), maxMenuHeight);
        const viewportLeft = clamp(wantedX, margin, Math.max(margin, viewportWidth - menuWidth - margin));
        const viewportTop = clamp(wantedY, margin, Math.max(margin, viewportHeight - menuHeight - margin));

        menu.style.left = `${viewportLeft - origin.x}px`;
        menu.style.top = `${viewportTop - origin.y}px`;
        menu.style.maxHeight = `${Math.max(120, viewportHeight - viewportTop - margin)}px`;

        const submenus = menu.querySelectorAll(".host-file-context-submenu");
        for (const submenu of submenus) {
            setupContextSubmenu(submenu);
            positionContextSubmenu(submenu);
        }

        return true;
    }

    function setupContextSubmenu(submenu) {
        if (!(submenu instanceof HTMLElement) || submenu.dataset.positionWatcher === "true") {
            return;
        }

        const reposition = () => positionContextSubmenu(submenu);
        submenu.addEventListener("pointerenter", reposition);
        submenu.addEventListener("focusin", reposition);

        const menu = submenu.closest("[data-lms-context-menu='true']");
        if (menu) {
            menu.addEventListener("scroll", reposition, { passive: true });
        }

        window.addEventListener("resize", reposition, { passive: true });
        submenu.dataset.positionWatcher = "true";
    }

    function positionContextSubmenu(submenu) {
        if (!(submenu instanceof HTMLElement)) {
            return false;
        }

        const trigger = submenu.querySelector(".host-file-context-submenu-trigger");
        const panel = submenu.querySelector(".host-file-context-submenu-panel");
        if (!(trigger instanceof HTMLElement) || !(panel instanceof HTMLElement)) {
            return false;
        }

        const margin = 8;
        const gap = 4;
        const viewportWidth = Math.max(document.documentElement.clientWidth || 0, window.innerWidth || 0);
        const viewportHeight = Math.max(document.documentElement.clientHeight || 0, window.innerHeight || 0);
        const triggerRect = trigger.getBoundingClientRect();
        const panelSize = measureHiddenElement(panel);
        const origin = getPositioningOrigin(panel);
        const panelWidth = Math.ceil(panelSize.width || 168);
        const panelHeight = Math.min(Math.ceil(panelSize.height || 0), Math.max(120, viewportHeight - margin * 2));

        const rightLeft = triggerRect.right + gap;
        const leftLeft = triggerRect.left - panelWidth - gap;
        const left = rightLeft + panelWidth <= viewportWidth - margin
            ? rightLeft
            : clamp(leftLeft, margin, Math.max(margin, viewportWidth - panelWidth - margin));
        const top = clamp(triggerRect.top, margin, Math.max(margin, viewportHeight - panelHeight - margin));

        panel.style.left = `${left - origin.x}px`;
        panel.style.top = `${top - origin.y}px`;
        panel.style.maxHeight = `${Math.max(120, viewportHeight - top - margin)}px`;
        return true;
    }

    function getPositioningOrigin(element) {
        const previousLeft = element.style.left;
        const previousTop = element.style.top;
        const previousDisplay = element.style.display;
        const previousVisibility = element.style.visibility;
        const previousPointerEvents = element.style.pointerEvents;
        const computedDisplay = window.getComputedStyle(element).display;

        if (computedDisplay === "none") {
            element.style.display = "grid";
            element.style.visibility = "hidden";
            element.style.pointerEvents = "none";
        }

        element.style.left = "0px";
        element.style.top = "0px";
        const rect = element.getBoundingClientRect();
        const origin = {
            x: Number.isFinite(rect.left) ? rect.left : 0,
            y: Number.isFinite(rect.top) ? rect.top : 0
        };

        element.style.left = previousLeft;
        element.style.top = previousTop;
        element.style.display = previousDisplay;
        element.style.visibility = previousVisibility;
        element.style.pointerEvents = previousPointerEvents;
        return origin;
    }

    function measureHiddenElement(element) {
        const previousDisplay = element.style.display;
        const previousVisibility = element.style.visibility;
        const previousPointerEvents = element.style.pointerEvents;

        element.style.display = "grid";
        element.style.visibility = "hidden";
        element.style.pointerEvents = "none";
        const rect = element.getBoundingClientRect();
        const size = {
            width: rect.width || element.scrollWidth || element.offsetWidth || 0,
            height: rect.height || element.scrollHeight || element.offsetHeight || 0
        };
        element.style.display = previousDisplay;
        element.style.visibility = previousVisibility;
        element.style.pointerEvents = previousPointerEvents;
        return size;
    }

    function clamp(value, min, max) {
        if (!Number.isFinite(value)) {
            return min;
        }

        if (max < min) {
            return min;
        }

        return Math.min(Math.max(value, min), max);
    }

    function parseFiniteNumber(value, fallback) {
        const parsed = Number.parseFloat(value);
        return Number.isFinite(parsed) ? parsed : fallback;
    }

    async function selectFilesForUpload() {
        const files = await pickFiles();
        return createUploadSelection(files);
    }

    function createUploadSelection(files) {
        if (!Array.isArray(files) || files.length === 0) {
            return null;
        }

        const selectionId = createIdentifier();
        const selectionEntries = [];
        const selectionFiles = [];

        for (const file of files) {
            if (!(file instanceof File)) {
                continue;
            }

            const clientFileId = createIdentifier();
            selectionEntries.push([clientFileId, file]);
            selectionFiles.push({
                clientFileId,
                fileName: file.name,
                sizeBytes: Number.isFinite(file.size) ? file.size : 0
            });
        }

        if (selectionFiles.length === 0) {
            return null;
        }

        uploadSelections.set(selectionId, new Map(selectionEntries));
        return {
            selectionId,
            files: selectionFiles
        };
    }

    async function pickUploadForTarget(targetId, destinationPath) {
        const target = uploadTargets.get(targetId);
        if (!target?.dotNetReference) {
            return false;
        }

        let selection = null;
        try {
            selection = await selectFilesForUpload();
            if (!selection) {
                return false;
            }

            openFileActionsPopup(target.fileActions);
            const uploadPlans = await target.dotNetReference.invokeMethodAsync(
                "PrepareBrowserUploadSelectionAsync",
                destinationPath || "",
                selection);
            const normalizedPlans = Array.isArray(uploadPlans) ? uploadPlans : [];
            if (normalizedPlans.length === 0) {
                releaseUploadSelection(selection.selectionId);
                return false;
            }

            await uploadManagedSelection(selection.selectionId, normalizedPlans);
            await target.dotNetReference.invokeMethodAsync(
                "OnBrowserUploadSelectionCompletedAsync",
                selection.files.map(file => file.fileName));
            return true;
        } catch (error) {
            if (selection?.selectionId) {
                releaseUploadSelection(selection.selectionId);
            }

            if (isPickerCancellation(error)) {
                return false;
            }

            await reportUploadFailure(target, error);
            return false;
        }
    }

    function registerUploadTarget(targetId, element, dotNetReference, fileActions) {
        if (!targetId || !element || !dotNetReference) {
            return false;
        }

        disposeUploadTarget(targetId);

        let dragDepth = 0;

        function handleDragEnter(event) {
            if (!isFileDrag(event)) {
                return;
            }

            dragDepth += 1;
            element.classList.add("upload-drag-active");
            event.preventDefault();
        }

        function handleDragOver(event) {
            if (!isFileDrag(event)) {
                return;
            }

            event.preventDefault();
            if (event.dataTransfer) {
                event.dataTransfer.dropEffect = "copy";
            }
        }

        function handleDragLeave(event) {
            if (!isFileDrag(event)) {
                return;
            }

            dragDepth = Math.max(0, dragDepth - 1);
            if (dragDepth === 0) {
                element.classList.remove("upload-drag-active");
            }
        }

        async function handleDrop(event) {
            if (!isFileDrag(event)) {
                return;
            }

            event.preventDefault();
            dragDepth = 0;
            element.classList.remove("upload-drag-active");

            const files = Array.from(event.dataTransfer?.files || []);
            const selection = createUploadSelection(files);
            if (!selection) {
                return;
            }

            openFileActionsPopup(fileActions);
            try {
                const uploadPlans = await dotNetReference.invokeMethodAsync(
                    "PrepareBrowserUploadSelectionAsync",
                    "",
                    selection);
                const normalizedPlans = Array.isArray(uploadPlans) ? uploadPlans : [];
                if (normalizedPlans.length === 0) {
                    releaseUploadSelection(selection.selectionId);
                    return;
                }

                await uploadManagedSelection(selection.selectionId, normalizedPlans);
                await dotNetReference.invokeMethodAsync(
                    "OnBrowserUploadSelectionCompletedAsync",
                    selection.files.map(file => file.fileName));
            } catch (error) {
                releaseUploadSelection(selection.selectionId);
                await reportUploadFailure({ dotNetReference }, error);
            }
        }

        element.addEventListener("dragenter", handleDragEnter);
        element.addEventListener("dragover", handleDragOver);
        element.addEventListener("dragleave", handleDragLeave);
        element.addEventListener("drop", handleDrop);

        uploadTargets.set(targetId, {
            dotNetReference,
            fileActions,
            dispose: () => {
                element.classList.remove("upload-drag-active");
                element.removeEventListener("dragenter", handleDragEnter);
                element.removeEventListener("dragover", handleDragOver);
                element.removeEventListener("dragleave", handleDragLeave);
                element.removeEventListener("drop", handleDrop);
            }
        });
        return true;
    }

    function openFileActionsPopup(fileActions) {
        const url = fileActions?.url || fileActions?.Url;
        const target = fileActions?.target || fileActions?.Target;
        if (!url || !target || !window.lmsTerminalWindow?.openPopup) {
            return false;
        }

        return window.lmsTerminalWindow.openPopup(
            url,
            target,
            fileActions?.width || fileActions?.Width || 860,
            fileActions?.height || fileActions?.Height || 620);
    }

    function disposeUploadTarget(targetId) {
        const target = uploadTargets.get(targetId);
        if (!target) {
            return false;
        }

        target.dispose();
        uploadTargets.delete(targetId);
        return true;
    }

    function releaseUploadSelection(selectionId) {
        if (!selectionId) {
            return false;
        }

        return uploadSelections.delete(selectionId);
    }

    async function reportUploadFailure(target, error) {
        if (!target?.dotNetReference) {
            return;
        }

        const message = error instanceof Error ? error.message : "Upload failed.";
        try {
            await target.dotNetReference.invokeMethodAsync("OnBrowserUploadSelectionFailedAsync", message);
        } catch {
        }
    }

    function isFileDrag(event) {
        return Array.from(event.dataTransfer?.types || []).includes("Files");
    }

    function isPickerCancellation(error) {
        return error?.name === "AbortError";
    }

    async function uploadManagedSelection(selectionId, uploadPlans) {
        const selection = uploadSelections.get(selectionId);
        if (!selection) {
            throw new Error("The selected files are no longer available.");
        }

        try {
            const normalizedPlans = Array.isArray(uploadPlans) ? uploadPlans : [];
            for (const plan of normalizedPlans) {
                const file = selection.get(plan.clientFileId);
                if (!(file instanceof File)) {
                    continue;
                }

                await uploadFileInChunks(file, plan);
            }
        } finally {
            uploadSelections.delete(selectionId);
        }
    }

    async function startManagedDownload(request) {
        if (!request || !request.contentUrl || !request.cancelUrl) {
            throw new Error("Download request is incomplete.");
        }

        let writable = null;
        try {
            const requestedContentType = request.contentType || "";
            const fileName = request.fileName || "download.bin";

            if (canPromptForSaveFile()) {
                const saveOptions = {
                    suggestedName: fileName
                };
                const fileTypes = buildSaveFileTypes(fileName, requestedContentType);
                if (fileTypes.length > 0) {
                    saveOptions.types = fileTypes;
                }

                const handle = await window.showSaveFilePicker(saveOptions);
                writable = await handle.createWritable();

                const response = await fetch(request.contentUrl, {
                    method: "GET",
                    credentials: "same-origin"
                });

                if (!response.ok || !response.body) {
                    throw new Error(await tryReadError(response, "Download failed."));
                }

                await streamDownloadToWritable(response.body, writable);
                return true;
            }

            startNativeDownload(request.contentUrl, fileName);
            return true;
        } catch (error) {
            if (writable && typeof writable.abort === "function") {
                try {
                    await writable.abort();
                } catch {
                }
            }

            try {
                await postJson(request.cancelUrl, { reason: error instanceof Error ? error.message : "Browser download failed." });
            } catch {
            }

            throw error;
        }
    }

    async function pickFiles() {
        if (typeof window.showOpenFilePicker === "function" && window.isSecureContext) {
            const handles = await window.showOpenFilePicker({ multiple: true });
            return Promise.all(handles.map(handle => handle.getFile()));
        }

        return pickFilesWithInput();
    }

    function pickFilesWithInput() {
        return new Promise(resolve => {
            const input = document.createElement("input");
            input.type = "file";
            input.multiple = true;
            input.style.position = "fixed";
            input.style.left = "-9999px";
            input.style.top = "-9999px";
            input.addEventListener("change", () => {
                const files = Array.from(input.files || []);
                input.remove();
                resolve(files);
            }, { once: true });

            document.body.appendChild(input);
            input.click();
        });
    }

    async function uploadFileInChunks(file, plan) {
        const chunkSize = 2 * 1024 * 1024;
        let offset = 0;

        try {
            if (file.size === 0) {
                await postJson(plan.completeUrl, {});
                return;
            }

            while (offset < file.size) {
                const nextOffset = Math.min(offset + chunkSize, file.size);
                const chunk = file.slice(offset, nextOffset);
                const response = await fetch(`${plan.uploadChunkUrl}?offset=${offset}`, {
                    method: "POST",
                    body: chunk,
                    credentials: "same-origin"
                });

                if (!response.ok) {
                    throw new Error(await tryReadError(response, `Uploading ${file.name} failed.`));
                }

                offset = nextOffset;
            }

            await postJson(plan.completeUrl, {});
        } catch (error) {
            try {
                await postJson(plan.cancelUrl, { reason: error instanceof Error ? error.message : `Uploading ${file.name} failed.` });
            } catch {
            }

            throw error;
        }
    }

    async function streamDownloadToWritable(stream, writable) {
        let completed = false;
        try {
            const reader = stream.getReader();
            while (true) {
                const { done, value } = await reader.read();
                if (done) {
                    break;
                }

                await writable.write(value);
            }

            completed = true;
        } finally {
            if (completed) {
                await writable.close();
            } else if (typeof writable.abort === "function") {
                await writable.abort();
            }
        }
    }

    function canPromptForSaveFile() {
        return typeof window.showSaveFilePicker === "function" &&
            window.isSecureContext &&
            !!navigator.userActivation?.isActive;
    }

    function buildSaveFileTypes(fileName, contentType) {
        const extension = getExtension(fileName);
        if (!extension) {
            return [];
        }

        return [
            {
                description: contentType || "File",
                accept: { [contentType || "application/octet-stream"]: [extension] }
            }
        ];
    }

    function startNativeDownload(url, fileName) {
        const anchor = document.createElement("a");
        anchor.href = url;
        anchor.download = fileName || "";
        anchor.rel = "noopener";
        anchor.style.display = "none";
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
    }

    async function postJson(url, payload) {
        const response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "Content-Type": "application/json"
            },
            body: JSON.stringify(payload || {})
        });

        if (!response.ok) {
            throw new Error(await tryReadError(response, "Transfer request failed."));
        }
    }

    async function tryReadError(response, fallbackMessage) {
        try {
            const text = await response.text();
            return text && text.trim().length > 0 ? text.trim() : fallbackMessage;
        } catch {
            return fallbackMessage;
        }
    }

    function getExtension(fileName) {
        const index = typeof fileName === "string" ? fileName.lastIndexOf(".") : -1;
        return index >= 0 ? fileName.slice(index) : "";
    }

    function createIdentifier() {
        if (globalThis.crypto && typeof globalThis.crypto.randomUUID === "function") {
            return globalThis.crypto.randomUUID();
        }

        return `lms-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
    }

    window.addEventListener("lms-theme-change", refreshOpenLayoutSurfaces);

    return {
        watchSuggestionScroll,
        disposeSuggestionScroll,
        watchSuggestionKeys,
        disposeSuggestionKeys,
        scrollActiveSuggestionIntoView,
        focusPathInputAtEnd,
        positionContextMenus,
        selectFilesForUpload,
        pickUploadForTarget,
        registerUploadTarget,
        disposeUploadTarget,
        uploadManagedSelection,
        startManagedDownload
    };
})();
