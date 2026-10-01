// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

(() => {
    document.addEventListener("click", event => {
        if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || event.defaultPrevented) return;
        const link = event.target.closest("a[data-ai-provider-link]");
        if (!link || link.target === "_blank") return;
        if (link.getAttribute("aria-busy") === "true") {
            event.preventDefault();
            return;
        }
        const status = link.querySelector("[data-provider-navigation-status]");
        if (status) status.textContent = "Opening provider...";
        link.setAttribute("aria-busy", "true");
    }, true);

    const reset = () => {
        document.querySelectorAll("a[data-ai-provider-link][aria-busy]").forEach(link => {
            link.removeAttribute("aria-busy");
            const status = link.querySelector("[data-provider-navigation-status]");
            if (status) status.textContent = "Open provider";
        });
    };
    window.addEventListener("pageshow", reset);
    document.addEventListener("DOMContentLoaded", () => {
        window.Blazor?.addEventListener("enhancedload", reset);
    });
})();
