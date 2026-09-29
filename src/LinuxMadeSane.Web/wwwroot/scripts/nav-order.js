// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

const controllers = new WeakMap();

export function bindNavOrder(list, dotNet) {
    if (!list || controllers.has(list)) return;

    let pressTimer;
    let pointerId;
    let source;
    let marker;
    let insertBefore = true;
    let startX = 0;
    let startY = 0;
    let dragging = false;
    let suppressClick = false;

    const clearMarker = () => {
        marker?.classList.remove("nav-insert-before", "nav-insert-after");
        marker = null;
    };

    const reset = () => {
        clearTimeout(pressTimer);
        clearMarker();
        source?.classList.remove("nav-dragging", "nav-pressing");
        source = null;
        pointerId = null;
        dragging = false;
    };

    const locateTarget = (x, y) => {
        clearMarker();
        const candidate = document.elementFromPoint(x, y)?.closest(".nav-reorder-item");
        if (!candidate || candidate === source || !list.contains(candidate)) return;
        const bounds = candidate.getBoundingClientRect();
        insertBefore = y < bounds.top + bounds.height / 2;
        candidate.classList.add(insertBefore ? "nav-insert-before" : "nav-insert-after");
        marker = candidate;
    };

    const onPointerDown = event => {
        if (event.button !== 0 || event.target.closest("button, input, select, textarea")) return;
        const item = event.target.closest(".nav-reorder-item");
        if (!item || !list.contains(item)) return;
        suppressClick = false;
        reset();
        source = item;
        pointerId = event.pointerId;
        startX = event.clientX;
        startY = event.clientY;
        item.classList.add("nav-pressing");
        pressTimer = setTimeout(() => {
            if (!source) return;
            dragging = true;
            source.classList.remove("nav-pressing");
            source.classList.add("nav-dragging");
            try { source.setPointerCapture?.(pointerId); } catch { reset(); return; }
            suppressClick = true;
        }, 3000);
    };

    const onPointerMove = event => {
        if (event.pointerId !== pointerId || !source) return;
        if (!dragging) {
            if (Math.hypot(event.clientX - startX, event.clientY - startY) > 8) reset();
            return;
        }
        event.preventDefault();
        locateTarget(event.clientX, event.clientY);
    };

    const onPointerUp = event => {
        if (event.pointerId !== pointerId || !source) return;
        const from = source.dataset.navPath;
        const to = marker?.dataset.navPath;
        const before = insertBefore;
        const wasDragging = dragging;
        reset();
        if (wasDragging && to) dotNet.invokeMethodAsync("MoveNavigationItemAsync", from, to, before);
    };

    const onClick = event => {
        if (!suppressClick) return;
        event.preventDefault();
        event.stopPropagation();
        suppressClick = false;
    };

    const onTouchMove = event => {
        if (!dragging || !event.touches.length) return;
        event.preventDefault();
        locateTarget(event.touches[0].clientX, event.touches[0].clientY);
    };

    const onTouchEnd = () => {
        if (!dragging || !source) return;
        const from = source.dataset.navPath;
        const to = marker?.dataset.navPath;
        const before = insertBefore;
        reset();
        if (to) dotNet.invokeMethodAsync("MoveNavigationItemAsync", from, to, before);
    };

    list.addEventListener("pointerdown", onPointerDown);
    list.addEventListener("pointermove", onPointerMove);
    list.addEventListener("pointerup", onPointerUp);
    list.addEventListener("pointercancel", reset);
    list.addEventListener("click", onClick, true);
    list.addEventListener("touchmove", onTouchMove, { passive: false });
    list.addEventListener("touchend", onTouchEnd);

    controllers.set(list, () => {
        reset();
        list.removeEventListener("pointerdown", onPointerDown);
        list.removeEventListener("pointermove", onPointerMove);
        list.removeEventListener("pointerup", onPointerUp);
        list.removeEventListener("pointercancel", reset);
        list.removeEventListener("click", onClick, true);
        list.removeEventListener("touchmove", onTouchMove);
        list.removeEventListener("touchend", onTouchEnd);
    });
}

export function disposeNavOrder(list) {
    controllers.get(list)?.();
    controllers.delete(list);
}
