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
    let reorderMode = false;
    let dragging = false;
    let suppressClick = false;

    const clearMarker = () => {
        marker?.classList.remove("nav-insert-before", "nav-insert-after");
        marker = null;
    };

    const endGesture = () => {
        clearTimeout(pressTimer);
        clearMarker();
        if (source?.hasPointerCapture(pointerId)) source.releasePointerCapture(pointerId);
        source?.classList.remove("nav-dragging", "nav-pressing");
        source = null;
        pointerId = null;
        dragging = false;
    };

    const leaveReorderMode = () => {
        endGesture();
        reorderMode = false;
        suppressClick = false;
        list.classList.remove("nav-reordering");
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

        endGesture();
        source = item;
        pointerId = event.pointerId;
        startX = event.clientX;
        startY = event.clientY;

        if (reorderMode) {
            event.preventDefault();
            source.setPointerCapture(pointerId);
            suppressClick = true;
            return;
        }

        source.classList.add("nav-pressing");
        pressTimer = setTimeout(() => {
            if (!source) return;
            reorderMode = true;
            source.classList.remove("nav-pressing");
            list.classList.add("nav-reordering");
            source.setPointerCapture(pointerId);
            suppressClick = true;
        }, 2000);
    };

    const onPointerMove = event => {
        if (event.pointerId !== pointerId || !source) return;
        const distance = Math.hypot(event.clientX - startX, event.clientY - startY);
        if (!reorderMode) {
            if (distance > 18) endGesture();
            return;
        }
        if (distance < 5 && !dragging) return;

        dragging = true;
        source.classList.add("nav-dragging");
        event.preventDefault();
        locateTarget(event.clientX, event.clientY);
    };

    const onPointerUp = event => {
        if (event.pointerId !== pointerId || !source) return;
        if (dragging) locateTarget(event.clientX, event.clientY);
        const from = source.dataset.navPath;
        const to = dragging ? marker?.dataset.navPath : null;
        const before = insertBefore;
        const wasReordering = reorderMode;
        endGesture();
        if (wasReordering) suppressClick = true;
        if (to) void dotNet.invokeMethodAsync("MoveNavigationItemAsync", from, to, before);
    };

    const onClick = event => {
        if (!suppressClick && !reorderMode) return;
        event.preventDefault();
        event.stopPropagation();
        suppressClick = false;
    };

    const onOutsidePointerDown = event => {
        if (reorderMode && !list.contains(event.target)) leaveReorderMode();
    };

    const onKeyDown = event => {
        if (reorderMode && event.key === "Escape") leaveReorderMode();
    };

    const onDragStart = event => {
        if (source || reorderMode) event.preventDefault();
    };

    const onContextMenu = event => {
        if (source || reorderMode) event.preventDefault();
    };

    list.addEventListener("pointerdown", onPointerDown);
    list.addEventListener("dragstart", onDragStart);
    list.addEventListener("contextmenu", onContextMenu);
    list.addEventListener("click", onClick, true);
    window.addEventListener("pointermove", onPointerMove, { passive: false });
    window.addEventListener("pointerup", onPointerUp);
    window.addEventListener("pointercancel", endGesture);
    document.addEventListener("pointerdown", onOutsidePointerDown, true);
    window.addEventListener("keydown", onKeyDown);

    controllers.set(list, () => {
        leaveReorderMode();
        list.removeEventListener("pointerdown", onPointerDown);
        list.removeEventListener("dragstart", onDragStart);
        list.removeEventListener("contextmenu", onContextMenu);
        list.removeEventListener("click", onClick, true);
        window.removeEventListener("pointermove", onPointerMove);
        window.removeEventListener("pointerup", onPointerUp);
        window.removeEventListener("pointercancel", endGesture);
        document.removeEventListener("pointerdown", onOutsidePointerDown, true);
        window.removeEventListener("keydown", onKeyDown);
    });
}

export function disposeNavOrder(list) {
    controllers.get(list)?.();
    controllers.delete(list);
}
