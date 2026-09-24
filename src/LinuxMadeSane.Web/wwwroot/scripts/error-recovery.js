// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

(() => {
    const errorUiSelector = "#blazor-error-ui";
    const recoveryKey = "lms-blazor-error-recovery";
    const recoveryWindowMs = 30_000;
    const maxAutomaticReloads = 1;
    let reloadScheduled = false;

    const isVisible = element => {
        if (!element) {
            return false;
        }

        const style = window.getComputedStyle(element);
        return style.display !== "none" && style.visibility !== "hidden";
    };

    const scheduleRecovery = element => {
        if (reloadScheduled || !isVisible(element)) {
            return;
        }

        let state = null;
        try {
            state = JSON.parse(window.sessionStorage.getItem(recoveryKey) || "null");
        } catch {
            state = null;
        }

        const now = Date.now();
        const attempts = state && now - state.startedAt < recoveryWindowMs
            ? Number(state.attempts || 0)
            : 0;

        if (attempts >= maxAutomaticReloads) {
            return;
        }

        reloadScheduled = true;
        try {
            window.sessionStorage.setItem(recoveryKey, JSON.stringify({
                startedAt: attempts === 0 ? now : state.startedAt,
                attempts: attempts + 1
            }));
        } catch {
            // A blocked sessionStorage must not prevent the manual fallback.
        }

        element.setAttribute("aria-hidden", "true");
        element.style.display = "none";
        window.setTimeout(() => window.location.reload(), 250);
    };

    const inspect = () => scheduleRecovery(document.querySelector(errorUiSelector));

    const observer = new MutationObserver(inspect);
    observer.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ["style", "class"],
        subtree: true
    });

    inspect();
})();
