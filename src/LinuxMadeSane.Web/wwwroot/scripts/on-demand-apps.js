/* Copyright (c) Linux Made Sane.
 * Licensed under the Business Source License 1.1. See LICENSE.md for details. */

(() => {
    const active = new Map();
    const heartbeatIntervalMs = 30000;

    const post = path => fetch(path, {
        method: "POST",
        credentials: "same-origin",
        cache: "no-store",
        keepalive: true
    }).catch(() => undefined);

    const stopTracking = (lease, release) => {
        const tracked = active.get(lease);
        if (!tracked) {
            return;
        }

        clearInterval(tracked.timer);
        active.delete(lease);
        if (release) {
            post(`/on-demand-apps/lease/${encodeURIComponent(lease)}/release`);
        }
    };

    const track = (lease, popup) => {
        const timer = setInterval(() => {
            if (popup.closed) {
                stopTracking(lease, true);
                return;
            }

            post(`/on-demand-apps/lease/${encodeURIComponent(lease)}/heartbeat`);
        }, heartbeatIntervalMs);
        active.set(lease, { popup, timer });
    };

    window.lmsOnDemandApps = {
        openTracked(anchor, event) {
            event?.preventDefault();
            if (!anchor?.href || typeof window.crypto?.randomUUID !== "function") {
                return false;
            }

            const lease = window.crypto.randomUUID();
            const url = new URL(anchor.href, window.location.href);
            url.searchParams.set("lease", lease);
            const popup = window.open(url.toString(), "_blank");
            if (!popup) {
                window.alert("The app window was blocked. Allow pop-ups for LMS, then open the app again.");
                return false;
            }

            try {
                popup.opener = null;
            } catch {
                // Cross-origin navigation will isolate the app from LMS regardless.
            }

            track(lease, popup);
            return false;
        }
    };
})();
