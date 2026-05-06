window.lmsTerminal = (() => {
    const terminals = new Map();

    function getState(id) {
        return terminals.get(id);
    }

    function getCssValue(name) {
        return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    }

    function buildTerminalTheme() {
        const accentRgb = getCssValue("--color-accent-rgb") || "125, 60, 255";

        return {
            background: getCssValue("--color-base-900") || "#1a0b26",
            foreground: getCssValue("--color-text") || "#f8f5ff",
            cursor: getCssValue("--color-accent") || "#7d3cff",
            selectionBackground: `rgba(${accentRgb}, 0.24)`,
            black: getCssValue("--color-base-950") || "#130817",
            brightBlack: getCssValue("--color-base-800") || "#2d1748",
            red: getCssValue("--color-danger") || "#ff7a94",
            brightRed: getCssValue("--color-danger") || "#ff7a94",
            green: getCssValue("--color-success") || "#7bdcb5",
            brightGreen: getCssValue("--color-success") || "#7bdcb5",
            yellow: getCssValue("--color-warning") || "#ffd36a",
            brightYellow: getCssValue("--color-warning") || "#ffd36a",
            blue: getCssValue("--color-accent-deep") || "#4f21b8",
            brightBlue: getCssValue("--color-accent") || "#7d3cff",
            magenta: getCssValue("--color-accent") || "#7d3cff",
            brightMagenta: getCssValue("--color-accent-bright") || "#d8b6ff",
            cyan: getCssValue("--color-accent-bright") || "#d8b6ff",
            brightCyan: getCssValue("--color-accent-bright") || "#d8b6ff",
            white: getCssValue("--color-text") || "#f8f5ff",
            brightWhite: "#ffffff"
        };
    }

    function applyThemeToTerminal(state) {
        if (!state?.terminal) {
            return;
        }

        state.terminal.options.theme = buildTerminalTheme();
        state.terminal.refresh(0, Math.max(state.terminal.rows - 1, 0));
    }

    function refreshAllThemes() {
        terminals.forEach(state => {
            applyThemeToTerminal(state);
        });
    }

    function createTerminal(id, elementId, dotNetRef) {
        disposeTerminal(id);

        const element = document.getElementById(elementId);
        if (!element) {
            return false;
        }

        const terminal = new Terminal({
            allowTransparency: true,
            cursorBlink: true,
            cursorStyle: "block",
            fontFamily: '"SFMono-Regular", Consolas, "Liberation Mono", Menlo, monospace',
            fontSize: 14,
            lineHeight: 1.25,
            scrollback: 5000,
            theme: buildTerminalTheme()
        });

        const fitAddon = new FitAddon.FitAddon();
        terminal.loadAddon(fitAddon);
        terminal.open(element);
        fitAddon.fit();
        terminal.focus();

        const resizeObserver = new ResizeObserver(() => {
            try {
                fitAddon.fit();
                dotNetRef.invokeMethodAsync("OnTerminalResize", terminal.cols, terminal.rows);
            } catch {
            }
        });

        resizeObserver.observe(element);

        const dataSubscription = terminal.onData(data => {
            const state = terminals.get(id);
            if (!state) {
                return;
            }

            state.pendingInput = (state.pendingInput || "") + data;
            if (state.inputFlushHandle) {
                return;
            }

            state.inputFlushHandle = window.setTimeout(() => {
                const latestState = terminals.get(id);
                if (!latestState) {
                    return;
                }

                const payload = latestState.pendingInput || "";
                latestState.pendingInput = "";
                latestState.inputFlushHandle = 0;

                if (payload) {
                    dotNetRef.invokeMethodAsync("OnTerminalData", payload);
                }
            }, 12);
        });

        terminals.set(id, {
            terminal,
            fitAddon,
            resizeObserver,
            dataSubscription,
            dotNetRef,
            lastOutput: "",
            lastRevision: -1,
            pendingInput: "",
            inputFlushHandle: 0
        });

        dotNetRef.invokeMethodAsync("OnTerminalResize", terminal.cols, terminal.rows);
        return true;
    }

    function writeDelta(id, fullOutput) {
        const state = getState(id);
        if (!state) {
            return;
        }

        const output = fullOutput ?? "";
        const previousOutput = state.lastOutput ?? "";

        if (!previousOutput || output.startsWith(previousOutput)) {
            if (output.length > previousOutput.length) {
                state.terminal.write(output.slice(previousOutput.length));
            }
        } else {
            state.terminal.reset();
            state.terminal.write(output);
        }

        state.lastOutput = output;
        state.terminal.scrollToBottom();
    }

    function appendChunk(id, chunk, outputRevision) {
        const state = getState(id);
        if (!state || !chunk) {
            return;
        }

        if ((state.lastRevision ?? -1) >= outputRevision) {
            return;
        }

        state.terminal.write(chunk);
        state.lastRevision = outputRevision;
        state.terminal.scrollToBottom();
    }

    function focus(id) {
        const state = getState(id);
        state?.terminal.focus();
    }

    async function copySelection(id) {
        const state = getState(id);
        const text = state?.terminal.getSelection() ?? "";
        if (text && navigator.clipboard?.writeText) {
            await navigator.clipboard.writeText(text);
        }
    }

    function getSelection(id) {
        const state = getState(id);
        return state?.terminal.getSelection() ?? "";
    }

    async function readClipboard() {
        if (navigator.clipboard?.readText) {
            return await navigator.clipboard.readText();
        }

        return "";
    }

    function clear(id) {
        const state = getState(id);
        if (!state) {
            return;
        }

        state.terminal.clear();
        state.lastOutput = "";
        state.lastRevision = -1;
    }

    function registerAiPromptShortcut(element, dotNetRef) {
        if (!element || !dotNetRef) {
            return;
        }

        if (element._lmsAiPromptKeydownHandler) {
            element.removeEventListener("keydown", element._lmsAiPromptKeydownHandler);
        }

        const handler = event => {
            if (event.key !== "Enter" || event.shiftKey || event.isComposing) {
                return;
            }

            event.preventDefault();
            dotNetRef.invokeMethodAsync("SubmitPromptFromKeyboardAsync");
        };

        element._lmsAiPromptKeydownHandler = handler;
        element.addEventListener("keydown", handler);
    }

    function disposeTerminal(id) {
        const state = getState(id);
        if (!state) {
            return;
        }

        state.dataSubscription?.dispose?.();
        state.resizeObserver?.disconnect?.();
        if (state.inputFlushHandle) {
            window.clearTimeout(state.inputFlushHandle);
        }
        state.terminal?.dispose?.();
        terminals.delete(id);
    }

    function getContextMenuPosition(hostElement, clientX, clientY) {
        if (!hostElement) {
            return { left: 8, top: 8 };
        }

        const rect = hostElement.getBoundingClientRect();
        const menuWidth = 192;
        const menuHeight = 176;
        const gutter = 8;
        const relativeX = clientX - rect.left;
        const relativeY = clientY - rect.top;
        const maxLeft = Math.max(gutter, rect.width - menuWidth - gutter);
        const maxTop = Math.max(gutter, rect.height - menuHeight - gutter);

        return {
            left: Math.min(Math.max(relativeX, gutter), maxLeft),
            top: Math.min(Math.max(relativeY, gutter), maxTop)
        };
    }

    function scrollElementToBottom(element) {
        if (!element) {
            return false;
        }

        element.scrollTop = element.scrollHeight;
        return true;
    }

    function beginAiPanelResize(container, clientX, minWidth, maxWidth, minConsoleWidth, dotNetRef, tabId) {
        if (!container || !dotNetRef || !tabId || !window.lmsSplitPane) {
            return false;
        }

        return window.lmsSplitPane.beginResize(
            container,
            {
                orientation: "right",
                pointerX: clientX,
                minSize: minWidth,
                maxSize: maxWidth,
                minPrimarySize: minConsoleWidth,
                splitterSize: 10,
                cssVariableName: "--terminal-ai-panel-width",
                callbackMethod: "OnAiPanelWidthChanged",
                contextId: tabId
            },
            dotNetRef);
    }

    window.addEventListener("lms-theme-change", refreshAllThemes);

    return {
        createTerminal,
        writeDelta,
        appendChunk,
        focus,
        copySelection,
        getSelection,
        readClipboard,
        clear,
        registerAiPromptShortcut,
        disposeTerminal,
        getContextMenuPosition,
        beginAiPanelResize,
        scrollElementToBottom,
        refreshThemes: refreshAllThemes
    };
})();

window.lmsTerminalWindow = (() => {
    const preparedPopups = new Map();

    function getCenteredPopupPosition(width, height) {
        const hostLeft = typeof window.screenX === "number" ? window.screenX : window.screenLeft;
        const hostTop = typeof window.screenY === "number" ? window.screenY : window.screenTop;
        const hostWidth = window.outerWidth || document.documentElement.clientWidth || screen.availWidth || width;
        const hostHeight = window.outerHeight || document.documentElement.clientHeight || screen.availHeight || height;

        const centeredLeft = Math.round(hostLeft + Math.max((hostWidth - width) / 2, 0));
        const centeredTop = Math.round(hostTop + Math.max((hostHeight - height) / 2, 0));

        return {
            left: Math.max(centeredLeft, 0),
            top: Math.max(centeredTop, 0)
        };
    }

    function openPopupWindow(url, name, width, height) {
        const position = getCenteredPopupPosition(width, height);
        const features = [
            "popup=yes",
            `width=${width}`,
            `height=${height}`,
            `left=${position.left}`,
            `top=${position.top}`,
            "resizable=yes",
            "scrollbars=no"
        ].join(",");

        const popup = window.open(url, name, features);
        if (!popup) {
            return null;
        }

        try {
            popup.focus();
        } catch {
        }

        try {
            popup.resizeTo(width, height);
        } catch {
        }

        try {
            popup.moveTo(position.left, position.top);
        } catch {
        }

        return popup;
    }

    function preparePopup(name, width = 1180, height = 860) {
        if (!name) {
            return false;
        }

        const popup = openPopupWindow("about:blank", name, width, height);
        if (!popup) {
            return false;
        }

        preparedPopups.set(name, popup);
        return true;
    }

    function openPopup(url, name, width = 1180, height = 860) {
        const preparedPopup = preparedPopups.get(name);
        if (preparedPopup && !preparedPopup.closed) {
            preparedPopups.delete(name);
            try {
                preparedPopup.location.href = url;
                preparedPopup.focus();
                preparedPopup.resizeTo(width, height);
                return true;
            } catch {
            }
        }

        preparedPopups.delete(name);
        return !!openPopupWindow(url, name, width, height);
    }

    function handleReattachToMain(url) {
        if (window.opener && !window.opener.closed) {
            try {
                window.opener.location.href = url;
                window.opener.focus();
                window.close();
                return true;
            } catch {
            }
        }

        window.location.href = url;
        return false;
    }

    function closeSelf() {
        window.close();
    }

    return {
        preparePopup,
        openPopup,
        handleReattachToMain,
        closeSelf
    };
})();
