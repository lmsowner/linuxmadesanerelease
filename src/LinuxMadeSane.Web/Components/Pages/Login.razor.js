const forms = document.querySelectorAll(".auth-login-form");
const otpLength = 6;

for (const form of forms) {
    const emailInput = form.querySelector("[name='email']");
    const returnUrlInput = form.querySelector("[name='returnUrl']");
    const passkeyButton = form.querySelector("[data-passkey-login]");
    const passkeyStatus = form.querySelector("[data-passkey-status]");
    const hiddenInput = form.querySelector("[data-otp-hidden]");
    const digitInputs = Array.from(form.querySelectorAll("[data-otp-digit]"));
    let isSubmitting = false;

    if (!(form instanceof HTMLFormElement) ||
        !(emailInput instanceof HTMLInputElement) ||
        !(returnUrlInput instanceof HTMLInputElement) ||
        !(hiddenInput instanceof HTMLInputElement) ||
        digitInputs.length !== otpLength ||
        digitInputs.some(input => !(input instanceof HTMLInputElement))) {
        continue;
    }

    const setPasskeyStatus = (message, isError = false) => {
        if (!(passkeyStatus instanceof HTMLElement)) {
            return;
        }

        passkeyStatus.hidden = message === "";
        passkeyStatus.textContent = message;
        passkeyStatus.classList.toggle("error", isError);
    };

    const codeValue = () => digitInputs.map(input => input.value).join("");

    const syncHiddenInput = () => {
        hiddenInput.value = codeValue();
    };

    const focusDigit = index => {
        const input = digitInputs[index];
        if (!input) {
            return;
        }

        input.focus();
        input.select();
    };

    const submitWhenComplete = () => {
        syncHiddenInput();

        if (isSubmitting || !/^\d{6}$/.test(hiddenInput.value)) {
            return;
        }

        if (!form.checkValidity()) {
            return;
        }

        isSubmitting = true;
        form.requestSubmit();
    };

    const setCode = (rawValue, startIndex = 0) => {
        const digits = rawValue.replace(/\D/g, "").slice(0, otpLength - startIndex).split("");
        if (digits.length === 0) {
            return;
        }

        digits.forEach((digit, offset) => {
            digitInputs[startIndex + offset].value = digit;
        });

        syncHiddenInput();

        const nextIndex = Math.min(startIndex + digits.length, otpLength - 1);
        focusDigit(nextIndex);
        submitWhenComplete();
    };

    const clearFrom = index => {
        digitInputs.slice(index).forEach(input => {
            input.value = "";
        });

        syncHiddenInput();
    };

    digitInputs.forEach((input, index) => {
        input.addEventListener("focus", () => input.select());

        input.addEventListener("input", event => {
            const target = event.target;
            const value = target.value.replace(/\D/g, "");

            if (value.length > 1) {
                clearFrom(0);
                setCode(value, 0);
                return;
            }

            target.value = value;
            syncHiddenInput();

            if (value !== "" && index < otpLength - 1) {
                focusDigit(index + 1);
            }

            submitWhenComplete();
        });

        input.addEventListener("keydown", event => {
            if (event.key === "Backspace") {
                event.preventDefault();
                if (input.value !== "") {
                    input.value = "";
                } else if (index > 0) {
                    digitInputs[index - 1].value = "";
                }

                syncHiddenInput();
                focusDigit(Math.max(0, index - 1));
                return;
            }

            if (event.key === "Delete") {
                event.preventDefault();
                input.value = "";
                syncHiddenInput();
                focusDigit(Math.max(0, index - 1));
                return;
            }

            if (event.key === "ArrowLeft" && index > 0) {
                event.preventDefault();
                focusDigit(index - 1);
                return;
            }

            if (event.key === "ArrowRight" && index < otpLength - 1) {
                event.preventDefault();
                focusDigit(index + 1);
            }
        });

        input.addEventListener("paste", event => {
            event.preventDefault();
            const pasted = event.clipboardData?.getData("text") ?? "";
            clearFrom(0);
            setCode(pasted, 0);
        });
    });

    form.addEventListener("submit", () => {
        syncHiddenInput();
    });

    if (passkeyButton instanceof HTMLButtonElement) {
        passkeyButton.addEventListener("click", async () => {
            if (!window.PublicKeyCredential) {
                setPasskeyStatus("This browser does not support device MFA.", true);
                return;
            }

            if (!window.isSecureContext) {
                setPasskeyStatus("Device MFA needs HTTPS, or direct localhost access on this machine. Use the Edge Gateway URL or http://localhost:5080.", true);
                return;
            }

            const email = emailInput.value.trim();
            if (email === "") {
                emailInput.focus();
                setPasskeyStatus("Enter your email first.", true);
                return;
            }

            passkeyButton.disabled = true;
            setPasskeyStatus("Waiting for device MFA...");

            try {
                const optionsResponse = await postJson("/api/passkeys/login/options", { email });
                if (!optionsResponse.succeeded) {
                    setPasskeyStatus(optionsResponse.message ?? "No passkey is available for this email.", true);
                    return;
                }

                const publicKey = prepareAssertionOptions(optionsResponse.options);
                const credential = await navigator.credentials.get({ publicKey });
                if (!credential) {
                    setPasskeyStatus("Device MFA was cancelled.", true);
                    return;
                }

                const completeResponse = await postJson(
                    `/api/passkeys/login/complete?returnUrl=${encodeURIComponent(returnUrlInput.value || "/")}`,
                    {
                        stateId: optionsResponse.stateId,
                        credential: publicKeyCredentialToJson(credential)
                    });

                if (!completeResponse.succeeded) {
                    setPasskeyStatus(completeResponse.message ?? "Device MFA failed.", true);
                    return;
                }

                window.location.assign(completeResponse.redirectUrl || "/");
            } catch (error) {
                setPasskeyStatus(error instanceof Error ? error.message : "Device MFA failed.", true);
            } finally {
                passkeyButton.disabled = false;
            }
        });
    }

    if (emailInput.value.trim() !== "") {
        const firstEmptyIndex = digitInputs.findIndex(input => input.value === "");
        focusDigit(firstEmptyIndex === -1 ? 0 : firstEmptyIndex);
    }
}

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
