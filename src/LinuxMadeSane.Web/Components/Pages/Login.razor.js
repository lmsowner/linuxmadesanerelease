/* Copyright (c) Richard D. Kiernan.
 * Licensed under the Business Source License 1.1. See LICENSE for details. */

const authMethods = ["passkey", "authenticator", "email"];
const otpLength = 6;
let enhancedNavigationBound = false;

function initializeLoginForms() {
    const forms = document.querySelectorAll(".auth-login-form");

    for (const form of forms) {
        if (!(form instanceof HTMLFormElement) || form.dataset.authLoginBound === "true") {
            continue;
        }

        const methodButtons = Array.from(form.querySelectorAll("[data-auth-method]"));
        const panels = Array.from(form.querySelectorAll("[data-auth-panel]"));
        const returnUrlInput = form.querySelector("[name='returnUrl']");
        const authenticatorEmailInput = form.querySelector("[data-authenticator-email]");
        const authenticatorHiddenInput = form.querySelector("[data-otp-hidden]");
        const authenticatorDigitInputs = Array.from(form.querySelectorAll("[data-otp-digit]"));
        const authenticatorSubmit = form.querySelector("[data-authenticator-submit]");
        const passkeyButton = form.querySelector("[data-passkey-login]");
        const emailInput = form.querySelector("[data-email-login-address]");
        const emailSendButton = form.querySelector("[data-email-mfa-send]");
        const emailCodeGroup = form.querySelector("[data-email-code-group]");
        const emailCodeInputs = Array.from(form.querySelectorAll("[data-email-code-digit]"));
        const serverError = form.querySelector("[data-server-auth-error]");
        const emailMfaSendUrl = form.dataset.emailMfaSendUrl || "/api/email-mfa/login/send";
        const emailMfaCompleteUrl = form.dataset.emailMfaCompleteUrl || "/api/email-mfa/login/complete";

        if (!(returnUrlInput instanceof HTMLInputElement) ||
            !(authenticatorEmailInput instanceof HTMLInputElement) ||
            !(authenticatorHiddenInput instanceof HTMLInputElement) ||
            !(authenticatorSubmit instanceof HTMLButtonElement) ||
            !(passkeyButton instanceof HTMLButtonElement) ||
            !(emailInput instanceof HTMLInputElement) ||
            !(emailSendButton instanceof HTMLButtonElement) ||
            !(emailCodeGroup instanceof HTMLElement) ||
            methodButtons.length !== authMethods.length ||
            panels.length !== authMethods.length ||
            authenticatorDigitInputs.length !== otpLength ||
            emailCodeInputs.length !== otpLength ||
            authenticatorDigitInputs.some(input => !(input instanceof HTMLInputElement)) ||
            emailCodeInputs.some(input => !(input instanceof HTMLInputElement))) {
            continue;
        }

        form.dataset.authLoginBound = "true";

        let activeMethod = authMethods.includes(form.dataset.initialAuthMethod)
            ? form.dataset.initialAuthMethod
            : "passkey";
        let activeRequestController = null;
        let requestGeneration = 0;
        let isSubmittingAuthenticator = false;
        let isCompletingEmailCode = false;

        form.classList.add("is-scripted");

        const setStatus = (method, message, isError = false) => {
            const status = form.querySelector(`[data-auth-status='${method}']`);
            if (!(status instanceof HTMLElement)) {
                return;
            }

            status.hidden = message === "";
            status.textContent = message;
            status.classList.toggle("error", isError);
        };

        const clearStatusMessages = () => {
            for (const method of authMethods) {
                setStatus(method, "");
            }
        };

        const cancelActiveRequest = () => {
            requestGeneration += 1;
            activeRequestController?.abort();
            activeRequestController = null;
            isCompletingEmailCode = false;
        };

        const beginRequest = () => {
            cancelActiveRequest();
            const controller = new AbortController();
            activeRequestController = controller;
            return { controller, generation: requestGeneration };
        };

        const requestIsCurrent = (generation, method) =>
            generation === requestGeneration && activeMethod === method;

        const clearAuthenticatorState = () => {
            authenticatorEmailInput.value = "";
            authenticatorHiddenInput.value = "";
            isSubmittingAuthenticator = false;
            for (const input of authenticatorDigitInputs) {
                input.value = "";
                input.blur();
            }
            authenticatorEmailInput.blur();
        };

        const clearEmailState = () => {
            emailInput.value = "";
            emailInput.blur();
            emailCodeGroup.hidden = true;
            isCompletingEmailCode = false;
            for (const input of emailCodeInputs) {
                input.value = "";
                input.blur();
            }
        };

        const clearPanelState = method => {
            if (method === "authenticator") {
                clearAuthenticatorState();
            } else if (method === "email") {
                clearEmailState();
            }
        };

        const dismissServerError = () => {
            if (serverError instanceof HTMLElement) {
                serverError.hidden = true;
                serverError.textContent = "";
            }
        };

        const setPanelAvailability = method => {
            for (const button of methodButtons) {
                if (!(button instanceof HTMLButtonElement)) {
                    continue;
                }

                const selected = button.dataset.authMethod === method;
                button.classList.toggle("active", selected);
                button.setAttribute("aria-selected", selected ? "true" : "false");
                button.tabIndex = selected ? 0 : -1;
            }

            for (const panel of panels) {
                if (!(panel instanceof HTMLElement)) {
                    continue;
                }

                const selected = panel.dataset.authPanel === method;
                panel.hidden = !selected;
                for (const control of panel.querySelectorAll("input, button, select, textarea")) {
                    if (control instanceof HTMLInputElement ||
                        control instanceof HTMLButtonElement ||
                        control instanceof HTMLSelectElement ||
                        control instanceof HTMLTextAreaElement) {
                        control.disabled = !selected;
                    }
                }
            }
        };

        const focusActivePanel = () => {
            const focusTarget = activeMethod === "passkey"
                ? passkeyButton
                : activeMethod === "authenticator"
                    ? authenticatorEmailInput
                    : emailInput;
            focusTarget.focus();
        };

        const setAuthMethod = (method, { userInitiated = false, focus = false } = {}) => {
            if (!authMethods.includes(method)) {
                return;
            }

            if (userInitiated) {
                cancelActiveRequest();
                for (const inactiveMethod of authMethods.filter(candidate => candidate !== method)) {
                    clearPanelState(inactiveMethod);
                }
                clearStatusMessages();
                dismissServerError();
                cleanLoginLocation(returnUrlInput.value || "/");
            }

            activeMethod = method;
            form.dataset.authMethod = method;
            setPanelAvailability(method);

            if (focus) {
                window.requestAnimationFrame(focusActivePanel);
            }
        };

        for (const [index, button] of methodButtons.entries()) {
            if (!(button instanceof HTMLButtonElement)) {
                continue;
            }

            button.addEventListener("click", () => {
                setAuthMethod(button.dataset.authMethod, { userInitiated: true, focus: true });
            });

            button.addEventListener("keydown", event => {
                if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) {
                    return;
                }

                event.preventDefault();
                let nextIndex = index;
                if (event.key === "ArrowLeft") {
                    nextIndex = (index - 1 + methodButtons.length) % methodButtons.length;
                } else if (event.key === "ArrowRight") {
                    nextIndex = (index + 1) % methodButtons.length;
                } else if (event.key === "Home") {
                    nextIndex = 0;
                } else if (event.key === "End") {
                    nextIndex = methodButtons.length - 1;
                }

                const nextButton = methodButtons[nextIndex];
                if (nextButton instanceof HTMLButtonElement) {
                    setAuthMethod(nextButton.dataset.authMethod, { userInitiated: true });
                    nextButton.focus();
                }
            });
        }

        const authenticatorCodeValue = () => authenticatorDigitInputs.map(input => input.value).join("");

        const syncAuthenticatorCode = () => {
            authenticatorHiddenInput.value = authenticatorCodeValue();
        };

        const focusAuthenticatorDigit = index => {
            const input = authenticatorDigitInputs[index];
            input.focus();
            input.select();
        };

        const submitAuthenticatorWhenComplete = () => {
            syncAuthenticatorCode();
            if (activeMethod !== "authenticator" ||
                isSubmittingAuthenticator ||
                !/^\d{6}$/.test(authenticatorHiddenInput.value)) {
                return;
            }

            if (!form.checkValidity()) {
                return;
            }

            isSubmittingAuthenticator = true;
            form.requestSubmit(authenticatorSubmit);
        };

        const setAuthenticatorCode = (rawValue, startIndex = 0) => {
            const digits = rawValue.replace(/\D/g, "").slice(0, otpLength - startIndex).split("");
            if (digits.length === 0) {
                return;
            }

            digits.forEach((digit, offset) => {
                authenticatorDigitInputs[startIndex + offset].value = digit;
            });
            syncAuthenticatorCode();
            focusAuthenticatorDigit(Math.min(startIndex + digits.length, otpLength - 1));
            submitAuthenticatorWhenComplete();
        };

        for (const [index, input] of authenticatorDigitInputs.entries()) {
            input.addEventListener("focus", () => input.select());
            input.addEventListener("input", event => {
                if (activeMethod !== "authenticator") {
                    return;
                }

                const value = event.target.value.replace(/\D/g, "");
                if (value.length > 1) {
                    for (const digitInput of authenticatorDigitInputs) {
                        digitInput.value = "";
                    }
                    setAuthenticatorCode(value);
                    return;
                }

                event.target.value = value;
                syncAuthenticatorCode();
                if (value !== "" && index < otpLength - 1) {
                    focusAuthenticatorDigit(index + 1);
                }
                submitAuthenticatorWhenComplete();
            });

            input.addEventListener("keydown", event => {
                if (activeMethod !== "authenticator") {
                    return;
                }

                if (event.key === "Enter") {
                    event.preventDefault();
                    submitAuthenticatorWhenComplete();
                } else if (event.key === "Backspace") {
                    event.preventDefault();
                    if (input.value !== "") {
                        input.value = "";
                    } else if (index > 0) {
                        authenticatorDigitInputs[index - 1].value = "";
                    }
                    syncAuthenticatorCode();
                    focusAuthenticatorDigit(Math.max(0, index - 1));
                } else if (event.key === "Delete") {
                    event.preventDefault();
                    input.value = "";
                    syncAuthenticatorCode();
                } else if (event.key === "ArrowLeft" && index > 0) {
                    event.preventDefault();
                    focusAuthenticatorDigit(index - 1);
                } else if (event.key === "ArrowRight" && index < otpLength - 1) {
                    event.preventDefault();
                    focusAuthenticatorDigit(index + 1);
                }
            });

            input.addEventListener("paste", event => {
                event.preventDefault();
                for (const digitInput of authenticatorDigitInputs) {
                    digitInput.value = "";
                }
                setAuthenticatorCode(event.clipboardData?.getData("text") ?? "");
            });
        }

        form.addEventListener("submit", event => {
            if (activeMethod !== "authenticator") {
                event.preventDefault();
                return;
            }

            syncAuthenticatorCode();
        });

        passkeyButton.addEventListener("click", async () => {
            if (activeMethod !== "passkey") {
                return;
            }

            if (!window.PublicKeyCredential) {
                setStatus("passkey", "This browser does not support passkeys.", true);
                return;
            }

            if (!window.isSecureContext) {
                setStatus("passkey", "Passkeys require HTTPS or direct localhost access.", true);
                return;
            }

            const returnUrl = passkeyButton.dataset.passkeyReturnUrl || returnUrlInput.value || "/";
            const passkeyOptionsUrl = passkeyButton.dataset.passkeyOptionsUrl || "/api/passkeys/login/options";
            const passkeyCompleteUrl = passkeyButton.dataset.passkeyCompleteUrl || "/api/passkeys/login/complete";
            const { controller, generation } = beginRequest();
            passkeyButton.disabled = true;
            cleanLoginLocation(returnUrl);
            setStatus("passkey", "Waiting for your passkey...");

            try {
                const optionsResponse = await postJson(passkeyOptionsUrl, {}, controller.signal);
                if (!requestIsCurrent(generation, "passkey")) {
                    return;
                }
                if (!optionsResponse.succeeded) {
                    setStatus("passkey", optionsResponse.message ?? "Passkey sign-in could not start.", true);
                    return;
                }

                const publicKey = prepareAssertionOptions(optionsResponse.options);
                const credential = await navigator.credentials.get({ publicKey, signal: controller.signal });
                if (!requestIsCurrent(generation, "passkey")) {
                    return;
                }
                if (!credential) {
                    setStatus("passkey", "No passkey was selected.", true);
                    return;
                }

                const separator = passkeyCompleteUrl.includes("?") ? "&" : "?";
                const completeUrl = `${passkeyCompleteUrl}${separator}returnUrl=${encodeURIComponent(returnUrl)}`;
                const completeResponse = await postJson(completeUrl, {
                    stateId: optionsResponse.stateId,
                    credential: publicKeyCredentialToJson(credential)
                }, controller.signal);
                if (!requestIsCurrent(generation, "passkey")) {
                    return;
                }
                if (!completeResponse.succeeded) {
                    setStatus("passkey", completeResponse.message ?? "Passkey sign-in failed.", true);
                    return;
                }

                window.location.assign(completeResponse.redirectUrl || "/");
            } catch (error) {
                if (!requestIsCurrent(generation, "passkey") ||
                    (error instanceof DOMException && error.name === "AbortError")) {
                    return;
                }

                const message = error instanceof DOMException && error.name === "NotAllowedError"
                    ? "No passkey was selected. Try again when you are ready."
                    : error instanceof Error
                        ? error.message
                        : "Passkey sign-in failed.";
                setStatus("passkey", message, true);
            } finally {
                if (activeRequestController === controller) {
                    activeRequestController = null;
                }
                if (requestIsCurrent(generation, "passkey")) {
                    passkeyButton.disabled = false;
                }
            }
        });

        const emailCodeValue = () => emailCodeInputs.map(input => input.value).join("");

        const focusEmailCodeDigit = index => {
            const input = emailCodeInputs[index];
            input.focus();
            input.select();
        };

        const showEmailCodeEntry = () => {
            emailCodeGroup.hidden = false;
            focusEmailCodeDigit(0);
        };

        const completeEmailCodeWhenReady = async () => {
            const code = emailCodeValue();
            if (activeMethod !== "email" || isCompletingEmailCode || !/^\d{6}$/.test(code)) {
                return;
            }

            const email = emailInput.value.trim();
            if (email === "") {
                emailInput.focus();
                setStatus("email", "Enter your email address first.", true);
                return;
            }

            const { controller, generation } = beginRequest();
            isCompletingEmailCode = true;
            setStatus("email", "Verifying the email code...");

            try {
                const response = await postJson(emailMfaCompleteUrl, {
                    email,
                    code,
                    returnUrl: returnUrlInput.value || "/"
                }, controller.signal);
                if (!requestIsCurrent(generation, "email")) {
                    return;
                }
                if (!response.succeeded) {
                    setStatus("email", response.message ?? "Email sign-in failed.", true);
                    for (const input of emailCodeInputs) {
                        input.value = "";
                    }
                    focusEmailCodeDigit(0);
                    return;
                }

                window.location.assign(response.redirectUrl || "/");
            } catch (error) {
                if (!requestIsCurrent(generation, "email") ||
                    (error instanceof DOMException && error.name === "AbortError")) {
                    return;
                }
                setStatus("email", error instanceof Error ? error.message : "Email sign-in failed.", true);
            } finally {
                if (activeRequestController === controller) {
                    activeRequestController = null;
                }
                if (requestIsCurrent(generation, "email")) {
                    isCompletingEmailCode = false;
                }
            }
        };

        emailSendButton.addEventListener("click", async () => {
            if (activeMethod !== "email") {
                return;
            }

            const email = emailInput.value.trim();
            if (email === "") {
                emailInput.focus();
                setStatus("email", "Enter your email address first.", true);
                return;
            }

            const { controller, generation } = beginRequest();
            emailSendButton.disabled = true;
            emailCodeGroup.hidden = true;
            for (const input of emailCodeInputs) {
                input.value = "";
            }
            setStatus("email", "Sending a secure email...");

            try {
                const response = await postJson(emailMfaSendUrl, {
                    email,
                    returnUrl: returnUrlInput.value || "/"
                }, controller.signal);
                if (!requestIsCurrent(generation, "email")) {
                    return;
                }

                setStatus("email", response.message ?? "Check your inbox for a sign-in link or six-digit code.");
                showEmailCodeEntry();
            } catch (error) {
                if (!requestIsCurrent(generation, "email") ||
                    (error instanceof DOMException && error.name === "AbortError")) {
                    return;
                }
                setStatus("email", error instanceof Error ? error.message : "Email sign-in could not start.", true);
            } finally {
                if (activeRequestController === controller) {
                    activeRequestController = null;
                }
                if (requestIsCurrent(generation, "email")) {
                    emailSendButton.disabled = false;
                }
            }
        });

        emailInput.addEventListener("input", () => {
            if (!emailCodeGroup.hidden) {
                emailCodeGroup.hidden = true;
                for (const input of emailCodeInputs) {
                    input.value = "";
                }
                setStatus("email", "Email changed. Send a new sign-in code.");
            }
        });

        emailInput.addEventListener("keydown", event => {
            if (event.key === "Enter" && activeMethod === "email") {
                event.preventDefault();
                emailSendButton.click();
            }
        });

        for (const [index, input] of emailCodeInputs.entries()) {
            input.addEventListener("focus", () => input.select());
            input.addEventListener("input", event => {
                if (activeMethod !== "email") {
                    return;
                }

                const value = event.target.value.replace(/\D/g, "");
                if (value.length > 1) {
                    for (const digitInput of emailCodeInputs) {
                        digitInput.value = "";
                    }
                    value.slice(0, otpLength).split("").forEach((digit, offset) => {
                        emailCodeInputs[offset].value = digit;
                    });
                    focusEmailCodeDigit(Math.min(value.length, otpLength) - 1);
                    completeEmailCodeWhenReady();
                    return;
                }

                event.target.value = value;
                if (value !== "" && index < otpLength - 1) {
                    focusEmailCodeDigit(index + 1);
                }
                completeEmailCodeWhenReady();
            });

            input.addEventListener("keydown", event => {
                if (activeMethod !== "email") {
                    return;
                }

                if (event.key === "Enter") {
                    event.preventDefault();
                    completeEmailCodeWhenReady();
                } else if (event.key === "Backspace") {
                    event.preventDefault();
                    if (input.value !== "") {
                        input.value = "";
                    } else if (index > 0) {
                        emailCodeInputs[index - 1].value = "";
                    }
                    focusEmailCodeDigit(Math.max(0, index - 1));
                } else if (event.key === "Delete") {
                    event.preventDefault();
                    input.value = "";
                } else if (event.key === "ArrowLeft" && index > 0) {
                    event.preventDefault();
                    focusEmailCodeDigit(index - 1);
                } else if (event.key === "ArrowRight" && index < otpLength - 1) {
                    event.preventDefault();
                    focusEmailCodeDigit(index + 1);
                }
            });

            input.addEventListener("paste", event => {
                event.preventDefault();
                for (const digitInput of emailCodeInputs) {
                    digitInput.value = "";
                }
                const digits = (event.clipboardData?.getData("text") ?? "").replace(/\D/g, "").slice(0, otpLength);
                digits.split("").forEach((digit, offset) => {
                    emailCodeInputs[offset].value = digit;
                });
                if (digits.length > 0) {
                    focusEmailCodeDigit(Math.min(digits.length, otpLength) - 1);
                }
                completeEmailCodeWhenReady();
            });
        }

        setAuthMethod(activeMethod);
    }
}

function bindEnhancedNavigation() {
    if (enhancedNavigationBound) {
        return;
    }

    const blazor = window.Blazor ?? window.blazor;
    if (!blazor || typeof blazor.addEventListener !== "function") {
        window.setTimeout(bindEnhancedNavigation, 100);
        return;
    }

    blazor.addEventListener("enhancedload", initializeLoginForms);
    enhancedNavigationBound = true;
}

initializeLoginForms();

if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initializeLoginForms, { once: true });
}

window.addEventListener("pageshow", initializeLoginForms);
bindEnhancedNavigation();

function cleanLoginLocation(returnUrl) {
    const currentUrl = new URL(window.location.href);
    const cleanParameters = new URLSearchParams({ returnUrl });
    const recovery = currentUrl.searchParams.get("recovery");
    if (recovery) {
        cleanParameters.set("recovery", recovery);
    }

    currentUrl.search = cleanParameters.toString();
    currentUrl.hash = "";
    window.history.replaceState(window.history.state, "", currentUrl);
}

async function postJson(url, body, signal) {
    const response = await fetch(url, {
        method: "POST",
        headers: {
            "Content-Type": "application/json",
            "Accept": "application/json"
        },
        credentials: "same-origin",
        signal,
        body: JSON.stringify(body)
    });

    const result = await response.json().catch(() => ({}));
    if (!response.ok) {
        throw new Error(result.message ?? `Request failed with HTTP ${response.status}.`);
    }

    return result;
}

function prepareAssertionOptions(options) {
    options.challenge = base64UrlToBuffer(options.challenge);
    options.allowCredentials = (options.allowCredentials ?? []).map(credential => ({
        ...credential,
        id: base64UrlToBuffer(credential.id)
    }));

    return options;
}

function publicKeyCredentialToJson(credential) {
    return {
        id: credential.id,
        rawId: bufferToBase64Url(credential.rawId),
        type: credential.type,
        response: {
            authenticatorData: bufferToBase64Url(credential.response.authenticatorData),
            clientDataJSON: bufferToBase64Url(credential.response.clientDataJSON),
            signature: bufferToBase64Url(credential.response.signature),
            userHandle: credential.response.userHandle
                ? bufferToBase64Url(credential.response.userHandle)
                : null
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
