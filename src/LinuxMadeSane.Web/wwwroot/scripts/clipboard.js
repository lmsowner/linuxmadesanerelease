/* Copyright (c) Richard D. Kiernan.
 * Licensed under the Business Source License 1.1. See LICENSE for details. */

window.lmsClipboard = (() => {
    async function writeText(text) {
        if (typeof text !== "string" || text.length === 0) {
            return false;
        }

        if (navigator.clipboard?.writeText) {
            await navigator.clipboard.writeText(text);
            return true;
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
        writeText
    };
})();
