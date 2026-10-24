/* Copyright (c) 2026 LMS Developer. All rights reserved.
 * Applicable licence and release scope: see LICENSE.md for details. */

window.lmsClipboard = (() => {
    const pendingCopies = new Map();

    // Start within the native click event, before a Blazor Server round trip
    // can consume the browser's transient clipboard permission.
    function beginCopy(text) {
        pendingCopies.set(text, writeText(text).catch(() => false));
    }

    document.addEventListener("click", event => {
        const button = event.target.closest?.("[data-copy-on-click]");
        if (button && !button.disabled) beginCopy(button.dataset.copyText);
    }, true);

    async function finishCopy(text) {
        const pending = pendingCopies.get(text);
        pendingCopies.delete(text);
        return pending ? await pending : false;
    }

    async function writeText(text) {
        if (typeof text !== "string" || text.length === 0) {
            return false;
        }

        if (navigator.clipboard?.writeText) {
            try {
                await navigator.clipboard.writeText(text);
                return true;
            } catch {
            }
        }

        const fallbackElement = document.createElement("textarea");
        fallbackElement.value = text;
        fallbackElement.setAttribute("readonly", "");
        fallbackElement.style.position = "fixed";
        fallbackElement.style.opacity = "0";
        fallbackElement.style.pointerEvents = "none";

        document.body.appendChild(fallbackElement);
        fallbackElement.select();

        let copied = false;
        try {
            copied = document.execCommand("copy");
        } finally {
            document.body.removeChild(fallbackElement);
        }

        return copied;
    }

    return {
        writeText,
        finishCopy
    };
})();
