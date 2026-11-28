/* Copyright (c) 2026 LMS Developer. All rights reserved.
 * Applicable licence and release scope: see LICENSE.md for details. */
// The stock same-origin Guacamole client owns the desktop and input handling.
const desktopFrame = document.querySelector('.remote-desktop-viewer iframe');
const loadStatus = document.querySelector('.remote-desktop-load-status');
let clientLoaded = false;
let desktopReady = false;
let sessionEnded = false;
const started = Date.now();
function checkDesktop() {
    if (sessionEnded || !desktopFrame || !loadStatus) return;
    try {
        const doc = desktopFrame.contentDocument;
        // Recognise the pinned stock client's failure/login views without changing it.
        // A failed tunnel can otherwise turn into Guacamole's full-page generic ERROR.
        if (!doc?.body?.hasAttribute('ng-cloak') && doc?.querySelector('.fatal-page-error-modal, .automatic-login-rejected-modal, guac-login, .logged-out-modal')) {
            sessionEnded = true;
            clearInterval(checkTimer); clearInterval(sessionTimer);
            loadStatus.hidden = false;
            loadStatus.textContent = 'The desktop connection failed or ended. Change the settings above and retry Reconnect. If it still fails, check the guacd and target logs in Hosts.';
            desktopFrame.src = 'about:blank';
            return;
        }
        if (doc?.querySelector('canvas')) {
            if (!desktopReady) { desktopReady = true; desktopFrame.hidden = false; loadStatus.hidden = true; }
            return;
        }
        if (clientLoaded && !desktopFrame.contentWindow.angular) {
            loadStatus.textContent = 'LMS could not load the desktop client. Check LMS access, disconnect and Connect again. If it persists, check LMS logs.';
            clearInterval(checkTimer);
        } else if (Date.now() - started > 30000) {
            loadStatus.textContent = 'The desktop has not opened yet. Check the connection message below, or disconnect and check the target and engine logs in Hosts.';
            clearInterval(checkTimer);
        }
    } catch {
        loadStatus.textContent = 'LMS could not access the desktop client. Disconnect and reconnect from Hosts.';
        clearInterval(checkTimer);
    }
}
const checkTimer = setInterval(checkDesktop, 500);
desktopFrame?.addEventListener('load', () => {
    if (sessionEnded) return;
    clientLoaded = true;
    const body = desktopFrame.contentDocument?.body;
    if (body) {
        body.tabIndex = -1;
        desktopFrame.contentWindow.focus();
        body.focus({ preventScroll: true });
    }
    checkDesktop();
});
window.addEventListener('pagehide', () => clearInterval(checkTimer), { once: true });


// Check LMS authority separately from the stock client. A gateway login redirect
// is not a Guacamole API response, and must never become an embedded MFA screen.
const sessionStateUrl = document.body.dataset.sessionState;
const signInLink = document.querySelector('.remote-desktop-signin');
let stateRequestPending = false;
async function checkSessionState() {
    if (!sessionStateUrl || stateRequestPending) return;
    stateRequestPending = true;
    try {
        const response = await fetch(sessionStateUrl, { credentials: 'same-origin', cache: 'no-store', redirect: 'follow' });
        if (response.status === 410) {
            sessionEnded = true;
            clearInterval(sessionTimer); clearInterval(checkTimer);
            loadStatus.hidden = false;
            loadStatus.textContent = 'The desktop connection ended. Retry Reconnect above. If it still fails, check the guacd and target logs in Hosts.';
            if (desktopFrame) desktopFrame.src = 'about:blank';
            return;
        }
        if (response.redirected || response.status === 401 || response.status === 403) {
            sessionEnded = true;
            clearInterval(sessionTimer);
            clearInterval(checkTimer);
            loadStatus.hidden = false;
            loadStatus.textContent = signInLink ? 'LMS access expired or was revoked. Sign in using the separate-window link, then retry Reconnect.' : 'Trusted LMS access is unavailable or the desktop binding changed. Open Hosts separately and Connect again.';
            if (signInLink) signInLink.hidden = false;
            if (desktopFrame) desktopFrame.src = 'about:blank';
            return;
        }
        if (!response.ok || !response.headers.get('content-type')?.includes('application/json')) return;
        const state = await response.json();
        const remaining = Date.parse(state.expiresAtUtc) - Date.now();
        if (state.authenticated && remaining > 0 && remaining < 60000) {
            loadStatus.hidden = false;
            loadStatus.textContent = 'Your configured LMS sign-in lifetime is nearly over. Sign in again, then reconnect from Hosts.';
            if (signInLink) signInLink.hidden = false;
        }
    } catch {
        // A transient network failure is not proof of logout. Keep the desktop intact.
    } finally { stateRequestPending = false; }
}
const sessionTimer = setInterval(checkSessionState, 15000);
window.addEventListener('pagehide', () => clearInterval(sessionTimer), { once: true });

const desktopControls = document.querySelector('.remote-desktop-controls');
desktopControls?.addEventListener('change', () => {
    const status = desktopControls.querySelector('.remote-desktop-settings-status');
    if (status) status.hidden = false;
});
desktopControls?.addEventListener('submit', async event => {
    const settingsStatus = desktopControls.querySelector('.remote-desktop-settings-status');
    if (settingsStatus) settingsStatus.hidden = true;
    event.preventDefault();
    if (event.submitter?.value !== 'reconnect') {
        sessionEnded = true;
        clearInterval(checkTimer); clearInterval(sessionTimer);
        loadStatus.hidden = false; loadStatus.textContent = 'Disconnecting remote desktop…';
        for (const button of desktopControls.querySelectorAll('button')) button.disabled = true;
        try {
            await fetch(event.submitter?.formAction || desktopControls.action, {
                method: 'POST', body: new FormData(desktopControls), credentials: 'same-origin', cache: 'no-store'
            });
        } catch { /* Closing the client also ends its tunnel. */ }
        finally {
            if (desktopFrame) desktopFrame.src = 'about:blank';
            loadStatus.textContent = 'Remote desktop disconnected. You can close this window.';
            window.close();
        }
        return;
    }
    const buttons = [...desktopControls.querySelectorAll('button')];
    if (buttons.some(button => button.disabled)) return;
    loadStatus.hidden = false;
    loadStatus.textContent = 'Reconnecting remote desktop…';
    sessionEnded = true;
    clearInterval(checkTimer);
    clearInterval(sessionTimer);
    for (const button of buttons) button.disabled = true;
    try {
        const response = await fetch(desktopControls.action, {
            method: 'POST', body: new FormData(desktopControls),
            credentials: 'same-origin', headers: { Accept: 'application/json' }, cache: 'no-store'
        });
        if (response.redirected || !response.headers.get('content-type')?.includes('application/json')) {
            const accessDenied = response.redirected || response.status === 401 || response.status === 403;
            loadStatus.textContent = accessDenied ? signInLink ? 'LMS access expired or was revoked. Use the separate-window sign-in link, then retry Reconnect here.' : 'Trusted LMS access is unavailable or the desktop binding changed. Open Hosts separately and Connect again.' : 'The engine returned an unexpected response. Check the network or engine, then retry Reconnect here.';
            if (accessDenied && signInLink) signInLink.hidden = false;
            return;
        }
        const result = await response.json();
        if (result.connectionRevision != null) desktopControls.elements.connectionRevision.value = result.connectionRevision;
        if (!response.ok) {
            loadStatus.textContent = result.error || 'Reconnect failed. Check the engine and target, then retry here.';
            if (response.status === 401 && signInLink) signInLink.hidden = false;
            return;
        }
        if (!/^\/remote-desktop\/session\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(result.sessionUrl || ''))
            throw new Error('Unexpected desktop response');
        window.location.replace(result.sessionUrl);
    } catch {
        loadStatus.textContent = 'Reconnect could not complete. Check the network or engine, then retry here.';
    } finally {
        for (const button of buttons) button.disabled = false;
    }
});
