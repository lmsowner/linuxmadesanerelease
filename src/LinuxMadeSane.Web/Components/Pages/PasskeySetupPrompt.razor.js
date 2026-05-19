/* Copyright (c) Richard D. Kiernan.
 * Licensed under the Business Source License 1.1. See LICENSE for details. */

const enrollments = document.querySelectorAll("[data-passkey-enrollment]");

for (const enrollment of enrollments) {
    const enrollButton = enrollment.querySelector("[data-passkey-enroll]");
    const friendlyNameInput = enrollment.querySelector("[data-passkey-friendly-name]");
    const returnUrlInput = enrollment.querySelector("[data-return-url]");
    const status = enrollment.querySelector("[data-passkey-status]");

    if (!(enrollButton instanceof HTMLButtonElement) ||
        !(friendlyNameInput instanceof HTMLInputElement) ||
        !(returnUrlInput instanceof HTMLInputElement)) {
        continue;
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

    enrollButton.addEventListener("click", async () => {
        if (!window.lmsPasskeys?.enroll) {
            setStatus("Passkey setup is not ready. Refresh the page and try again.", true);
            return;
        }

        enrollButton.disabled = true;
        setStatus("Waiting for your browser to create the passkey...");

        try {
            const result = await window.lmsPasskeys.enroll(friendlyNameInput.value.trim() || "This device");
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
}
