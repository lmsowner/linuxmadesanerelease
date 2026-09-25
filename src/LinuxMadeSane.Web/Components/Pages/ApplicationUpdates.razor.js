// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

let countdownTimer;
const restartProbeIntervalMilliseconds = 1000;

async function reloadWhenAvailable() {
    while (true) {
        try {
            const response = await window.fetch(window.location.href, {
                cache: "no-store",
                credentials: "same-origin"
            });
            if (response.ok) {
                window.location.reload();
                return;
            }
        } catch {
            // LMS is still restarting. Keep the current update screen visible.
        }

        await new Promise(resolve => window.setTimeout(resolve, restartProbeIntervalMilliseconds));
    }
}

export function startCountdown(seconds) {
    window.clearInterval(countdownTimer);

    let remaining = Math.max(0, Number(seconds) || 0);
    const render = () => {
        const value = document.querySelector("[data-lms-update-countdown-value]");
        const bar = document.querySelector("[data-lms-update-countdown-bar]");
        if (value) value.textContent = String(remaining);
        if (bar) bar.style.width = `${Math.max(0, Math.min(100, ((Number(seconds) - remaining) / Number(seconds)) * 100))}%`;
    };

    render();
    countdownTimer = window.setInterval(() => {
        remaining -= 1;
        render();
        if (remaining <= 0) {
            window.clearInterval(countdownTimer);
            void reloadWhenAvailable();
        }
    }, 1000);
}
