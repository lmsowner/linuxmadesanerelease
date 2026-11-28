// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
(() => {
    const hint = document.createElement("div");
    hint.id = "lms-action-tooltip";
    hint.className = "icon-action-tooltip lms-tooltip";
    hint.setAttribute("role", "tooltip");
    hint.hidden = true;
    if (hint.showPopover) hint.setAttribute("popover", "manual");


    let active;
    let title;
    let suppliedName = false;
    function close() {
        if (!active) return;
        if (hint.isConnected) hint.hidePopover?.();
        hint.hidden = true;
        if (title && active.getAttribute("title") === "") active.setAttribute("title", title);
        if (suppliedName && active.getAttribute("aria-label") === title) active.removeAttribute("aria-label");
        const ids = (active.getAttribute("aria-describedby") || "").split(/\s+/).filter(id => id && id !== hint.id);
        if (ids.length) active.setAttribute("aria-describedby", ids.join(" "));
        else active.removeAttribute("aria-describedby");
        active = null;
        suppliedName = false;
    }

    function show(event) {
        if (event.pointerType === "touch") return;
        const el = event.target.closest?.('[title],button[aria-label],a[aria-label],[role="button"][aria-label]');
        if (el === active && hint.isConnected) return;
        close();
        // The animated Portal dock places its hint above the magnified icon.
        if (!el || el.closest("[data-instance-dock],.ui-field-info") || el.matches("iframe,svg,title")) return;
        title = el.getAttribute("title");
        const label = el.getAttribute("aria-label");
        const text = title || label;
        if (!text?.trim()) return;
        active = el;
        if (title) {
            // An empty title suppresses both the browser tooltip and an inherited title.
            if (!label && !el.hasAttribute("aria-labelledby") && !el.textContent.trim() && !el.labels?.length) {
                el.setAttribute("aria-label", title);
                suppliedName = true;
            }
            el.setAttribute("title", "");
        }
        if (text !== (label || (suppliedName ? title : null))) {
            const ids = el.getAttribute("aria-describedby");
            el.setAttribute("aria-describedby", ids ? ids + " " + hint.id : hint.id);
        }
        // Enhanced navigation can discard elements appended to the previous page body.
        if (!hint.isConnected) document.body.append(hint);
        hint.textContent = text;
        hint.hidden = false;
        hint.showPopover?.();
        const rect = el.getBoundingClientRect();
        hint.style.left = Math.max(4, Math.min(innerWidth - hint.offsetWidth - 4, rect.left + (rect.width - hint.offsetWidth) / 2)) + "px";
        hint.style.top = Math.max(4, rect.bottom + hint.offsetHeight + 8 < innerHeight ? rect.bottom + 4 : rect.top - hint.offsetHeight - 4) + "px";
    }

    function leave(event) {
        if (active?.contains(event.target) && !active.contains(event.relatedTarget)) close();
    }
    document.addEventListener("pointerover", show, true);
    document.addEventListener("focusin", show);
    document.addEventListener("pointerout", leave, true);
    document.addEventListener("focusout", leave);
    document.addEventListener("pointerdown", close);
    document.addEventListener("keydown", event => { if (event.key === "Escape") close(); });
    document.addEventListener("scroll", close, true);
    document.addEventListener("enhancedload", close);
    window.addEventListener("resize", close);
    new MutationObserver(() => { if (active && !active.isConnected) close(); })
        .observe(document.documentElement, { childList: true, subtree: true });
})();
