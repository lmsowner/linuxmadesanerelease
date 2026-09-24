(() => {
    const padding = 10;

    const clamp = (value, min, max) => Math.min(Math.max(value, min), max);

    const placeStampDock = root => {
        const trigger = root.querySelector(".edge-http-service-stamp");
        const dock = root.querySelector(".edge-http-service-stamp-dock");
        if (!trigger || !dock) {
            return;
        }

        const panel = root.closest(".ui-modal-panel") || root.closest('[role="dialog"]');
        const bounds = panel
            ? panel.getBoundingClientRect()
            : new DOMRect(padding, padding, window.innerWidth - padding * 2, window.innerHeight - padding * 2);
        const rect = trigger.getBoundingClientRect();
        const width = clamp(
            Math.max(rect.width * 1.55, 15.5 * 16),
            12 * 16,
            Math.min(22 * 16, bounds.width - padding * 2));
        const gap = 10;

        dock.style.position = "fixed";
        dock.style.zIndex = "250";
        dock.style.width = `${Math.round(width)}px`;
        dock.style.maxWidth = `${Math.round(width)}px`;
        dock.style.right = "auto";
        dock.style.bottom = "auto";
        dock.style.visibility = "hidden";
        dock.style.left = `${Math.round(rect.left)}px`;
        dock.style.top = `${Math.round(rect.top)}px`;
        dock.classList.add("is-placed");

        const dockHeight = dock.getBoundingClientRect().height;
        const spaceAbove = rect.top - bounds.top - padding;
        const spaceBelow = bounds.bottom - rect.bottom - padding;
        const placeAbove = spaceAbove >= dockHeight + gap || spaceAbove >= spaceBelow;

        let top = placeAbove ? rect.top - dockHeight - gap : rect.bottom + gap;
        let left = rect.left + rect.width / 2 - width / 2;

        const minLeft = bounds.left + padding;
        const maxLeft = bounds.right - padding - width;
        left = maxLeft >= minLeft ? clamp(left, minLeft, maxLeft) : minLeft;

        const minTop = bounds.top + padding;
        const maxTop = bounds.bottom - padding - Math.min(dockHeight, bounds.height - padding * 2);
        top = maxTop >= minTop ? clamp(top, minTop, maxTop) : minTop;

        dock.style.left = `${Math.round(left)}px`;
        dock.style.top = `${Math.round(top)}px`;
        dock.style.transformOrigin = placeAbove ? "center bottom" : "center top";
        dock.classList.toggle("is-above", placeAbove);
        dock.classList.toggle("is-below", !placeAbove);
        dock.style.visibility = "visible";
    };

    const placeHoveredStamps = () => {
        document.querySelectorAll(".edge-http-service-stamp-shell:hover, .edge-http-service-stamp-shell:focus-within")
            .forEach(placeStampDock);
    };

    document.addEventListener("pointerover", event => {
        const stamp = event.target?.closest?.(".edge-http-service-stamp-shell");
        if (stamp) {
            placeStampDock(stamp);
        }
    }, true);

    document.addEventListener("focusin", event => {
        const stamp = event.target?.closest?.(".edge-http-service-stamp-shell");
        if (stamp) {
            placeStampDock(stamp);
        }
    }, true);

    document.addEventListener("scroll", placeHoveredStamps, true);
    window.addEventListener("resize", placeHoveredStamps);
})();
