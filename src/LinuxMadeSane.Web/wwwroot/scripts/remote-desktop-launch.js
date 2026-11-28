/* Copyright (c) 2026 LMS Developer. All rights reserved.
 * Applicable licence and release scope: see LICENSE.md for details. */
// Guacamole parses this origin-wide cache before it can render or authenticate.
// LMS authorises every connection server-side; this cache contains only "lms".
// Always obtain fresh bridge authentication; never resume origin-wide auth state.
try { localStorage.removeItem('GUAC_AUTH_TOKEN'); }
catch { /* Storage may be disabled; the stock client falls back to memory. */ }
document.getElementById('remote-desktop-launch')?.requestSubmit();
const pendingDesktop = document.querySelector('.remote-desktop-viewer iframe[data-src]');
if (pendingDesktop) { pendingDesktop.hidden = true; pendingDesktop.src = pendingDesktop.dataset.src; }
