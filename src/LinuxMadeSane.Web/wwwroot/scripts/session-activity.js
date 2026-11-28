// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
(() => {
    const reportInterval = 10000;
    let pending = false;
    let busy = false;
    let lastInteraction = 0;
    let lastReport = -Infinity;
    let reportTimer;
    let expiryTimer;

    async function check(report) {
        if (busy) return;
        const ageMs = Math.round(performance.now() - lastInteraction);
        if (report && ageMs > 15000) {
            pending = false;
            report = false;
        }
        busy = true;
        if (report) {
            pending = false;
            lastReport = performance.now();
        }
        try {
            const response = await fetch('/auth/session/activity', {
                method: report ? 'POST' : 'GET',
                headers: report ? { 'X-LMS-Activity': 'interaction', 'X-LMS-Activity-Age': String(ageMs) } : {},
                credentials: 'same-origin', cache: 'no-store', keepalive: report,
                redirect: 'manual'
            });
            if (response.type === 'opaqueredirect' || [401, 403].includes(response.status)) {
                if (!desktopFrame) location.reload(); // The desktop viewer has its own sign-in prompt.
                return;
            }
            if (!response.ok) throw new Error('Session check unavailable');
            const { remainingMs } = await response.json();
            clearTimeout(expiryTimer);
            // Another tab can renew the shared cookie. Ask the server before leaving this page.
            expiryTimer = setTimeout(() => check(false), Math.min(2147483647, remainingMs + 250));
        } catch {
            clearTimeout(expiryTimer);
            expiryTimer = setTimeout(() => check(false), reportInterval);
        } finally {
            busy = false;
            if (pending) scheduleReport();
        }
    }

    function scheduleReport() {
        if (busy || reportTimer !== undefined) return;
        const delay = Math.max(0, reportInterval - (performance.now() - lastReport));
        if (!delay) {
            void check(true);
            return;
        }
        reportTimer = setTimeout(() => {
            reportTimer = undefined;
            if (pending) void check(true);
        }, delay);
    }

    function interacted(event) {
        if (!event.isTrusted || document.visibilityState !== 'visible') return;
        lastInteraction = performance.now();
        pending = true;
        scheduleReport();
    }
    function listen(target) {
        for (const event of ['pointerdown', 'pointermove', 'keydown', 'input', 'wheel', 'touchmove']) {
            target?.addEventListener(event, interacted, { passive: true, capture: true });
        }
    }
    listen(document);
    const desktopFrame = document.querySelector('.remote-desktop-viewer iframe');
    if (desktopFrame) {
        listen(desktopFrame.contentDocument);
        desktopFrame.addEventListener('load', () => listen(desktopFrame.contentDocument));
    }
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') void check(false);
    });
    if (document.currentScript?.dataset.userNavigation === 'true') {
        lastInteraction = performance.now();
        void check(true);
    } else {
        void check(false);
    }
})();
