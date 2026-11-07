/* Copyright (c) 2026 LMS Developer. All rights reserved.
 * Applicable licence and release scope: see LICENSE.md for details. */

/* Copyright (c) 2026 LMS Developer. All rights reserved. */
// Adapter for the pinned stock Guacamole 1.6 client; no protocol/client fork.
(() => {
    const frame = document.querySelector('.remote-desktop-viewer iframe');
    const panel = document.querySelector('.remote-desktop-clipboard');
    if (!frame || !panel) return;
    const text = panel.querySelector('textarea');
    const status = panel.querySelector('[role=status]');
    const enabled = document.body.dataset.clipboard === 'true';
    const writable = document.body.dataset.clipboardPaste === 'true';
    let api, remoteText = '', unsubscribe;
    function message(value) { status.textContent = value; }
    function connect() {
        if (api || !enabled) return;
        try {
            const win = frame.contentWindow;
            const injector = win.angular?.element(frame.contentDocument.body).injector();
            if (!injector) return;
            api = { manager: injector.get('guacClientManager'), ManagedClient: injector.get('ManagedClient'), ClipboardData: injector.get('ClipboardData'), root: injector.get('$rootScope') };
            unsubscribe = api.root.$on('guacClipboard', (_, data) => {
                if (data?.source && data.type?.startsWith('text/') && typeof data.data === 'string') {
                    remoteText = data.data;
                    if (panel.hidden || document.activeElement !== text) text.value = remoteText;
                }
            });
            for (const name of ['keydown', 'keyup']) frame.contentDocument.addEventListener(name, event => {
                // Let the browser produce a native paste event, while avoiding an early
                // remote Ctrl+V against the previous clipboard contents.
                if (writable && (event.ctrlKey || event.metaKey) && event.key?.toLowerCase() === 'v' && !event.target?.closest('input,textarea,[contenteditable=true]')) event.stopImmediatePropagation();
            }, true);
            frame.contentDocument.addEventListener('paste', event => {
                if (!writable || event.target?.closest('input,textarea,[contenteditable=true]')) return;
                const value = event.clipboardData?.getData('text/plain');
                if (value != null) { event.preventDefault(); event.stopImmediatePropagation(); try { send(value, true); } catch (e) { panel.hidden = false; message(e.message); } }
            }, true);
        } catch { api = null; }
    }
    function send(value, paste = false) {
        connect();
        if (!api || !writable) throw new Error('Clipboard is unavailable. Enable Clipboard and reconnect; read-only sessions cannot paste.');
        const clients = Object.values(api.manager.getManagedClients());
        if (clients.length !== 1) throw new Error('Wait for the desktop to connect.');
        api.ManagedClient.setClipboard(clients[0], new api.ClipboardData({ type: 'text/plain', data: value }));
        if (paste) {
            // String clipboard writes and end are synchronous in the pinned client;
            // key instructions follow them over the same ordered Guacamole tunnel.
            const client = clients[0].client;
            client.sendKeyEvent(1, 0xffe3); client.sendKeyEvent(1, 0x76);
            client.sendKeyEvent(0, 0x76); client.sendKeyEvent(0, 0xffe3);
        }
        message(paste ? 'Pasted into the remote application.' : 'Text sent to the remote clipboard. Paste in the remote application using Ctrl+V (or its Paste command).');
        frame.contentWindow.focus();
    }
    document.querySelector('[data-desktop-clipboard]')?.addEventListener('click', () => {
        panel.hidden = !panel.hidden; connect();
        if (!panel.hidden) { text.value = remoteText; text.focus(); message(enabled ? 'Paste text here to send it, or copy text received from the remote desktop.' : 'Clipboard is disabled. Enable Clipboard above and reconnect.'); }
    });
    panel.querySelector('[data-clipboard-send]')?.addEventListener('click', () => { try { send(text.value); } catch (e) { message(e.message); } });
    panel.querySelector('[data-clipboard-paste]')?.addEventListener('click', async () => {
        try { if (!navigator.clipboard?.readText) throw new Error(); const value = await navigator.clipboard.readText(); send(value, true); }
        catch { message('Browser clipboard access is blocked. Paste into the text box, then choose Send to remote.'); text.focus(); }
    });
    panel.querySelector('[data-clipboard-copy]')?.addEventListener('click', async () => {
        text.value = remoteText; text.focus(); text.select();
        try {
            if (navigator.clipboard?.writeText) await navigator.clipboard.writeText(remoteText);
            else if (!document.execCommand('copy')) throw new Error();
            message('Remote clipboard copied.');
        } catch { message('Text selected. Press Ctrl+C or ⌘C to copy.'); }
    });
    const timer = setInterval(connect, 500);
    window.addEventListener('pagehide', () => { clearInterval(timer); unsubscribe?.(); remoteText = ''; text.value = ''; }, {once:true});
})();
