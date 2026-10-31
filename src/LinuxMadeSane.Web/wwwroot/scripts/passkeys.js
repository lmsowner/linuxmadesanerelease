/* Copyright (c) 2026 LMS Developer. All rights reserved.
 * Applicable licence and release scope: see LICENSE.md for details. */

window.lmsPasskeys = window.lmsPasskeys || {};

window.lmsPasskeys.enroll = async (friendlyName, targetUserId, endpoints = {}) => {
    if (!window.PublicKeyCredential) {
        return {
            succeeded: false,
            message: "This browser does not support passkeys."
        };
    }

    if (!window.isSecureContext) {
        return {
            succeeded: false,
            message: "Passkey setup needs HTTPS, or direct localhost access on this machine. Use the Edge Gateway URL or http://localhost:5080."
        };
    }

    try {
        const optionsResponse = await postJson(endpoints.options || "/api/passkeys/enroll/options", {
            friendlyName,
            targetUserId: targetUserId || null
        });
        if (!optionsResponse.succeeded) {
            return {
                succeeded: false,
                message: optionsResponse.message ?? "Passkey setup could not start."
            };
        }

        const publicKey = prepareCredentialCreationOptions(optionsResponse.options);
        const credential = await navigator.credentials.create({ publicKey });
        if (!credential) {
            return {
                succeeded: false,
                message: "Passkey setup was cancelled."
            };
        }

        const completeResponse = await postJson(endpoints.complete || "/api/passkeys/register/complete", {
            stateId: optionsResponse.stateId,
            credential: publicKeyCredentialToJson(credential)
        });

        return {
            succeeded: completeResponse.succeeded === true,
            message: completeResponse.message ?? (completeResponse.succeeded ? "Passkey added." : "Passkey setup failed.")
        };
    } catch (error) {
        return {
            succeeded: false,
            message: error instanceof Error ? error.message : "Passkey setup failed."
        };
    }
};

document.addEventListener("click", async event => {
    const eventTarget = event.target;
    if (!(eventTarget instanceof Element)) {
        return;
    }

    const enrollButton = eventTarget.closest("[data-passkey-enroll]");
    if (!(enrollButton instanceof HTMLButtonElement) || enrollButton.disabled) {
        return;
    }

    const enrollment = enrollButton.closest("[data-passkey-enrollment]");
    const friendlyNameInput = enrollment?.querySelector("[data-passkey-friendly-name]");
    const returnUrlInput = enrollment?.querySelector("[data-return-url]");
    const targetUserIdInput = enrollment?.querySelector("[data-target-user-id]");
    const status = enrollment?.querySelector("[data-passkey-status]");

    if (!(friendlyNameInput instanceof HTMLInputElement) ||
        !(returnUrlInput instanceof HTMLInputElement) ||
        !(targetUserIdInput instanceof HTMLInputElement)) {
        return;
    }

    const setStatus = (message, isError = false) => {
        if (!(status instanceof HTMLElement)) {
            return;
        }

        status.hidden = message === "";
        status.textContent = message;
        status.classList.toggle("error", isError);
        status.classList.toggle("success", !isError && message !== "");
    };

    enrollButton.disabled = true;
    setStatus("Waiting for your browser to create the passkey...");

    try {
        const result = await window.lmsPasskeys.enroll(
            friendlyNameInput.value.trim() || "This device",
            targetUserIdInput.value, { options: enrollment.dataset.optionsUrl, complete: enrollment.dataset.completeUrl });
        if (!result.succeeded) {
            setStatus(result.message || "Passkey setup failed.", true);
            return;
        }

        setStatus("MFA passkey added. Continuing...");
        window.setTimeout(() => window.location.assign(returnUrlInput.value || "/"), 700);
    } catch (error) {
        setStatus(error instanceof Error ? error.message : "Passkey setup failed.", true);
    } finally {
        enrollButton.disabled = false;
    }
});

async function postJson(url, body) {
    const response = await fetch(url, {
        method: "POST",
        headers: {
            "Content-Type": "application/json",
            "Accept": "application/json"
        },
        credentials: "same-origin",
        body: JSON.stringify(body)
    });

    const result = await response.json().catch(() => ({}));
    if (!response.ok) {
        throw new Error(result.message ?? `Request failed with HTTP ${response.status}.`);
    }

    return result;
}

function prepareCredentialCreationOptions(options) {
    options.challenge = base64UrlToBuffer(options.challenge);
    options.user.id = base64UrlToBuffer(options.user.id);
    options.excludeCredentials = (options.excludeCredentials ?? []).map(credential => ({
        ...credential,
        id: base64UrlToBuffer(credential.id)
    }));

    return options;
}

function publicKeyCredentialToJson(credential) {
    const response = credential.response;
    return {
        id: credential.id,
        rawId: bufferToBase64Url(credential.rawId),
        type: credential.type,
        response: {
            attestationObject: response.attestationObject
                ? bufferToBase64Url(response.attestationObject)
                : undefined,
            authenticatorData: response.authenticatorData
                ? bufferToBase64Url(response.authenticatorData)
                : undefined,
            clientDataJSON: bufferToBase64Url(response.clientDataJSON),
            signature: response.signature
                ? bufferToBase64Url(response.signature)
                : undefined,
            userHandle: response.userHandle
                ? bufferToBase64Url(response.userHandle)
                : undefined
        },
        clientExtensionResults: credential.getClientExtensionResults()
    };
}

function base64UrlToBuffer(value) {
    const padded = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(value.length + ((4 - value.length % 4) % 4), "=");
    const binary = window.atob(padded);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index++) {
        bytes[index] = binary.charCodeAt(index);
    }

    return bytes.buffer;
}

function bufferToBase64Url(buffer) {
    const bytes = new Uint8Array(buffer);
    let binary = "";
    for (const byte of bytes) {
        binary += String.fromCharCode(byte);
    }

    return window.btoa(binary)
        .replace(/\+/g, "-")
        .replace(/\//g, "_")
        .replace(/=+$/g, "");
}

// An optional offer after successful MFA. Navigation stays on this hostname.
function startPasskeyOffers() {
    for (const offer of document.querySelectorAll("[data-passkey-offer]:not([data-countdown-started])")) {
        offer.dataset.countdownStarted = "true";
        const destination = offer.dataset.continueUrl;
        if (!destination || !destination.startsWith("/") || destination.startsWith("//") || destination.startsWith("/\\")) continue;
        const counter = offer.querySelector("[data-passkey-countdown]");
        const deadline = Date.now() + 5000;
        const timer = window.setInterval(() => {
            if (!offer.isConnected) { window.clearInterval(timer); return; }
            const seconds = Math.max(0, Math.ceil((deadline - Date.now()) / 1000));
            if (counter) counter.textContent = String(seconds);
            if (seconds === 0) { window.clearInterval(timer); window.location.assign(destination); }
        }, 100);
        offer.addEventListener("click", event => {
            if (event.target.closest("a")) window.clearInterval(timer);
        });
        window.addEventListener("pagehide", () => window.clearInterval(timer), { once: true });
    }
}
startPasskeyOffers();
document.addEventListener("DOMContentLoaded", startPasskeyOffers);
window.addEventListener("load", () => window.Blazor?.addEventListener("enhancedload", startPasskeyOffers), { once: true });
