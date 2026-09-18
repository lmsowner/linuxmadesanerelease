/* Copyright (c) Linux Made Sane.
 * Licensed under the Business Source License 1.1. See LICENSE for details. */

const reconnectElementId = "components-reconnect-modal";
const disconnectedStates = new Set(["show", "retrying", "failed", "rejected", "paused", "resume-failed"]);
const retryDelayMs = 30_000;
let recovery = null;
let requestPending = false;
let reloadPending = false;
let countdownTimer;

function showStatus(message, seconds) {
    const element = document.getElementById(reconnectElementId);
    if (!element) return;
    const status = element.querySelector("[data-reconnect-status]");
    if (status.textContent !== message) status.textContent = message;
    const countdown = element.querySelector("[data-reconnect-countdown]");
    countdown.hidden = seconds === undefined;
    if (seconds !== undefined) countdown.textContent = `Checking again in ${seconds}s`;
    if (!element.open) element.showModal();
}

function scheduleRetry() {
    const retryAt = Date.now() + retryDelayMs;
    const tick = () => {
        if (navigator.onLine === false) {
            showStatus("You’re offline. Waiting for your connection.");
            return;
        }
        const seconds = Math.max(0, Math.ceil((retryAt - Date.now()) / 1000));
        showStatus("LMS is not available yet. Retrying automatically.", seconds);
        if (seconds === 0) void checkAvailability();
    };
    countdownTimer = window.setInterval(tick, 1000);
    tick();
}

async function checkAvailability() {
    if (!recovery || requestPending || reloadPending) return;
    window.clearInterval(countdownTimer);
    if (navigator.onLine === false) {
        showStatus("You’re offline. Waiting for your connection.");
        return;
    }

    requestPending = true;
    showStatus("Checking the connection…");
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 5000);
    try {
        const availability = new URL(recovery.availability);
        availability.searchParams.set("_", Date.now().toString());
        const response = await fetch(availability, {
            cache: "no-store",
            credentials: "same-origin",
            redirect: "error",
            headers: { Accept: "application/json" },
            signal: controller.signal
        });
        // Cloudflare errors and gateway login pages must never trigger navigation.
        if (response.status === 200 && !response.redirected &&
            response.headers.get("content-type")?.split(";")[0].trim() === "application/json") {
            const result = await response.json();
            if (result?.product === "linux-made-sane" && result.status === "ok") {
                reloadPending = true;
                showStatus("LMS is back. Reconnecting…");
                // Reload the page the user was using. Redirecting to / loses the current
                // workspace and is especially disruptive during background discovery.
                window.location.reload();
            }
        }
    } catch {
        // A stopped server, disconnected tunnel or timed-out request is retried below.
    } finally {
        window.clearTimeout(timeout);
        requestPending = false;
    }
    if (!reloadPending) scheduleRetry();
}

function recoverWhenAvailable() {
    if (recovery) return;

    const current = new URL(window.location.href);
    const reconnectElement = document.getElementById(reconnectElementId);
    const availability = new URL(reconnectElement?.dataset.availabilityUrl || "/LMSMFAAuth/availability", current.origin);
    if (availability.origin !== current.origin) return;
    recovery = { availability: availability.href };
    void checkAvailability();
}

// Enhanced navigation replaces the marker without re-running this module.
// Capture its non-bubbling events on the document so the handler stays attached.
document.addEventListener("components-reconnect-state-changed", event => {
    if (event.target?.id === reconnectElementId && disconnectedStates.has(event.detail?.state)) recoverWhenAvailable();
}, { capture: true });

document.addEventListener("cancel", event => {
    if (event.target?.id === reconnectElementId) event.preventDefault();
}, { capture: true });
window.addEventListener("online", () => void checkAvailability());

const reconnectElement = document.getElementById(reconnectElementId);
if (reconnectElement && [...disconnectedStates].some(state => reconnectElement.classList.contains(`components-reconnect-${state}`))) {
    recoverWhenAvailable();
}
