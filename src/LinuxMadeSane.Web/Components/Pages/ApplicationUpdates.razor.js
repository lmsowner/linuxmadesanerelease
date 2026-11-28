// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

let countdownTimer;
let countdownAnimation;
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
    window.clearTimeout(countdownTimer);
    countdownAnimation?.cancel();

    const duration = Math.max(0, seconds * 1000);
    const deadline = performance.now() + duration;
    const value = document.querySelector("[data-lms-update-countdown-value]");
    const bar = document.querySelector("[data-lms-update-countdown-bar]");
    countdownAnimation = bar?.animate(
        [{ transform: "scaleX(0)" }, { transform: "scaleX(1)" }],
        { duration, easing: "linear", fill: "forwards" }
    );

    function updateSeconds() {
        const milliseconds = Math.max(0, deadline - performance.now());
        const remaining = Math.ceil(milliseconds / 1000);
        if (value) value.textContent = String(remaining);
        if (remaining === 0) {
            void reloadWhenAvailable();
            return;
        }

        countdownTimer = window.setTimeout(updateSeconds, milliseconds - (remaining - 1) * 1000);
    }

    updateSeconds();
}
