const forms = document.querySelectorAll(".auth-login-form");

for (const form of forms) {
    const hiddenInput = form.querySelector("[data-otp-hidden]");
    const digitInputs = Array.from(form.querySelectorAll("[data-otp-digit]"));

    if (!(hiddenInput instanceof HTMLInputElement) || digitInputs.length !== 6) {
        continue;
    }

    const syncHiddenInput = () => {
        hiddenInput.value = digitInputs.map(input => input.value).join("");
    };

    const focusDigit = index => {
        const input = digitInputs[index];
        if (!input) {
            return;
        }

        input.focus();
        input.select();
    };

    const setCode = (rawValue, startIndex = 0) => {
        const digits = rawValue.replace(/\D/g, "").slice(0, digitInputs.length - startIndex).split("");
        if (digits.length === 0) {
            return;
        }

        digits.forEach((digit, offset) => {
            digitInputs[startIndex + offset].value = digit;
        });

        syncHiddenInput();

        const nextIndex = Math.min(startIndex + digits.length, digitInputs.length - 1);
        focusDigit(nextIndex);
    };

    digitInputs.forEach((input, index) => {
        input.addEventListener("focus", () => input.select());

        input.addEventListener("input", event => {
            const value = event.target.value.replace(/\D/g, "");

            if (value.length > 1) {
                setCode(value, index);
                return;
            }

            event.target.value = value;
            syncHiddenInput();

            if (value !== "" && index < digitInputs.length - 1) {
                focusDigit(index + 1);
            }
        });

        input.addEventListener("keydown", event => {
            if (event.key === "Backspace" && input.value === "" && index > 0) {
                event.preventDefault();
                digitInputs[index - 1].value = "";
                syncHiddenInput();
                focusDigit(index - 1);
            }

            if (event.key === "ArrowLeft" && index > 0) {
                event.preventDefault();
                focusDigit(index - 1);
            }

            if (event.key === "ArrowRight" && index < digitInputs.length - 1) {
                event.preventDefault();
                focusDigit(index + 1);
            }
        });

        input.addEventListener("paste", event => {
            event.preventDefault();
            const pasted = event.clipboardData?.getData("text") ?? "";
            setCode(pasted, index);
        });
    });

    form.addEventListener("submit", () => {
        syncHiddenInput();
    });
}
