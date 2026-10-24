/* Copyright (c) 2026 LMS Developer. All rights reserved.
 * Applicable licence and release scope: see LICENSE.md for details. */

const reconnectElementId = "components-reconnect-modal";
const terminalDisconnectedStates = new Set(["failed", "rejected", "paused", "resume-failed"]);
const retryDelayMs = 30_000;
let recovery = null;
let requestPending = false;
let recoveryStopped = false;
let countdownTimer;
let plannedRestart = null;
let activeRequest = null;

function showStatus(message, seconds, allowManualRefresh = false) {
    const element = document.getElementById(reconnectElementId);
    if (!element) return;
    const status = element.querySelector("[data-reconnect-status]");
    if (status.textContent !== message) status.textContent = message;
    const countdown = element.querySelector("[data-reconnect-countdown]");
    countdown.hidden = seconds === undefined;
    if (seconds !== undefined) countdown.textContent = `Checking again in ${seconds}s`;
    const refresh = element.querySelector("[data-reconnect-refresh]");
    if (refresh) refresh.hidden = !allowManualRefresh;
    if (!element.open && !plannedRestart?.dismissed) element.showModal();
}

function scheduleRetry() {
    const retryAt = Date.now() + (plannedRestart ? 3000 : retryDelayMs);
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
    if (!recovery || requestPending || recoveryStopped || (plannedRestart && Date.now() < plannedRestart.readyAt)) return;
    window.clearInterval(countdownTimer);
    if (navigator.onLine === false) {
        showStatus("You’re offline. Waiting for your connection.");
        return;
    }

    requestPending = true;
    showStatus("Checking the connection…");
    const requestRecovery = recovery;
    const controller = new AbortController();
    activeRequest = controller;
    const timeout = window.setTimeout(() => controller.abort(), 5000);
    try {
        const availability = new URL(requestRecovery.availability);
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
            if (recovery !== requestRecovery) return;
            if (result?.product === "linux-made-sane" && result.status === "ok") {
                showStatus("LMS is back. Reconnecting…");
                if (plannedRestart) {
                    // A service restart destroys the old circuit. Reload this same page
                    // to refresh licence status and discard the disconnected modal.
                    recoveryStopped = true;
                    window.location.reload();
                    return;
                }
                const blazor = window.Blazor ?? window.blazor;
                const reconnected = typeof blazor?.reconnect === "function" && await blazor.reconnect();
                if (reconnected) {
                    recovery = null;
                    const element = document.getElementById(reconnectElementId);
                    if (element?.open) element.close();
                    return;
                }

                recoveryStopped = true;
                showStatus(
                    "LMS is available, but this browser session cannot be resumed. Refresh the page when you are ready.",
                    undefined,
                    true);
                return;
            }
        }
    } catch {
        // A stopped server, disconnected tunnel or timed-out request is retried below.
    } finally {
        window.clearTimeout(timeout);
        if (activeRequest === controller) { requestPending = false; activeRequest = null; }
    }
    if (!recoveryStopped && recovery === requestRecovery) scheduleRetry();
}

function recoverWhenAvailable() {
    if (recovery || plannedRestart) return;

    const current = new URL(window.location.href);
    const reconnectElement = document.getElementById(reconnectElementId);
    const availability = new URL(reconnectElement?.dataset.availabilityUrl || "/healthz", current.origin);
    if (availability.origin !== current.origin) return;
    recovery = { availability: availability.href };
    void checkAvailability();
}

// Enhanced navigation replaces the marker without re-running this module.
// Capture its non-bubbling events on the document so the handler stays attached.
document.addEventListener("components-reconnect-state-changed", event => {
    if (event.target?.id === reconnectElementId && terminalDisconnectedStates.has(event.detail?.state)) recoverWhenAvailable();
}, { capture: true });

document.addEventListener("click", event => {
    if (event.target?.closest?.("[data-restart-dismiss]") && plannedRestart) {
        event.preventDefault();
        plannedRestart.dismissed = true;
        document.getElementById(reconnectElementId)?.close();
    }
    if (event.target?.closest?.("[data-reconnect-refresh]")) window.location.reload();
}, { capture: true });

document.addEventListener("cancel", event => {
    if (event.target?.id === reconnectElementId) {
        event.preventDefault();
        if (plannedRestart) {
            plannedRestart.dismissed = true;
            event.target.close();
        }
    }
}, { capture: true });
window.addEventListener("online", () => void checkAvailability());

const reconnectElement = document.getElementById(reconnectElementId);
if (reconnectElement && [...terminalDisconnectedStates].some(state => reconnectElement.classList.contains(`components-reconnect-${state}`))) {
    recoverWhenAvailable();
}

// Runs entirely in the browser so progress and Close survive a lost Blazor circuit.
window.lmsRestart = {
    begin(seconds = 5) {
        this.cancel();
        window.clearInterval(countdownTimer);
        activeRequest?.abort(); activeRequest = null; requestPending = false;
        const element = document.getElementById(reconnectElementId);
        if (!element) throw new Error("The LMS reconnect dialog is unavailable.");
        const duration = Math.max(1, Math.min(30, Number(seconds) || 5));
        const activation = document.querySelector(".licensing-activation-modal");
        plannedRestart = { readyAt: Date.now() + duration * 1000, dismissed: false,
            activation, previousDisplay: activation?.style.display,
            title: element.querySelector("h2").textContent,
            description: element.querySelector("#reconnect-description").textContent };
        if (activation) activation.style.display = "none";
        element.querySelector("h2").textContent = "Restarting LMS";
        element.querySelector("#reconnect-description").textContent = "This page will refresh automatically when LMS is ready. You can close this dialog while it reconnects.";
        element.querySelector("[data-restart-dismiss]").hidden = false;
        const availability = new URL(element.dataset.availabilityUrl || "/healthz", window.location.href);
        if (availability.origin !== new URL(window.location.href).origin) { this.cancel(); throw new Error("Invalid restart availability origin."); }
        recovery = { availability: availability.href };
        recoveryStopped = false;
        const tick = () => {
            const seconds = Math.max(0, Math.ceil((plannedRestart.readyAt - Date.now()) / 1000));
            showStatus("LMS is restarting. Reconnecting automatically…", seconds);
            if (seconds === 0) { window.clearInterval(countdownTimer); void checkAvailability(); }
        };
        countdownTimer = window.setInterval(tick, 1000);
        tick();
    },
    cancel() {
        if (!plannedRestart) return;
        activeRequest?.abort(); activeRequest = null; requestPending = false;
        window.clearInterval(countdownTimer);
        const element = document.getElementById(reconnectElementId);
        if (plannedRestart.activation) plannedRestart.activation.style.display = plannedRestart.previousDisplay;
        if (element) {
            element.close();
            element.querySelector("h2").textContent = plannedRestart.title;
            element.querySelector("#reconnect-description").textContent = plannedRestart.description;
            element.querySelector("[data-restart-dismiss]").hidden = true;
        }
        plannedRestart = null;
        recovery = null;
        recoveryStopped = false;
    }
};
