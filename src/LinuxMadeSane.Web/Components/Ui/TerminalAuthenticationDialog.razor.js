/* Copyright (c) Linux Made Sane.
 * Licensed under the Business Source License 1.1. See LICENSE.md for details. */

// Keep the password prompt focused; never send its value to any JavaScript API.
export function trapFocus(root) {
    const controls = () => [...root.querySelectorAll('button, a[href], input, select, textarea, summary, [tabindex="0"]')]
        .filter(element => !element.disabled && element.getClientRects().length > 0);
    root.addEventListener('keydown', event => {
        if (event.key !== 'Tab') return;
        const items = controls();
        const first = items[0], last = items[items.length - 1];
        if (!first) { event.preventDefault(); return; }
        if (event.shiftKey && (document.activeElement === first || document.activeElement === root)) {
            event.preventDefault(); last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault(); first.focus();
        }
    });
    root.querySelector('input[type=password]')?.focus();
}
