const forms = document.querySelectorAll(".auth-login-form");
const otpLength = 6;

for (const form of forms) {
    const identifierInput = form.querySelector("[name='identifier']");
    const hiddenInput = form.querySelector("[data-otp-hidden]");
    const digitInputs = Array.from(form.querySelectorAll("[data-otp-digit]"));
    let isSubmitting = false;

    if (!(form instanceof HTMLFormElement) ||
        !(identifierInput instanceof HTMLInputElement) ||
        !(hiddenInput instanceof HTMLInputElement) ||
        digitInputs.length !== otpLength ||
        digitInputs.some(input => !(input instanceof HTMLInputElement))) {
        continue;
    }

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

    if (identifierInput.value.trim() !== "") {
        const firstEmptyIndex = digitInputs.findIndex(input => input.value === "");
        focusDigit(firstEmptyIndex === -1 ? 0 : firstEmptyIndex);
    }
}
