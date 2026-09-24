// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

let countdownTimer;

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
            window.location.reload();
        }
    }, 1000);
}
