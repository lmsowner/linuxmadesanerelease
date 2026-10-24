// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
const bindings = new WeakMap();
export function bind(shell, menu, callback) {
    const sidebar = shell.closest('aside') || shell;
    if (bindings.has(shell)) return;
    const close = () => { if (menu.matches(':popover-open')) menu.hidePopover(); };
    const open = event => {
        event.preventDefault();
        for (const submenu of menu.querySelectorAll('.submenu-open')) submenu.classList.remove('submenu-open');
        menu.dataset.contextX = event.clientX;
        menu.dataset.contextY = event.clientY;
        if (!menu.matches(':popover-open')) menu.showPopover();
        // Reuse the File Manager's viewport-aware menu and flyout positioning.
        window.lmsFileBrowser.positionContextMenus();
        menu.focus();
    };
    const click = event => {
        if (event.target.closest('.host-file-context-submenu-trigger')) return;
        if (event.target.closest('button, a')) close();
    };
    const key = event => {
        if (event.key === 'Escape') { close(); shell.querySelector('a,button')?.focus(); return; }
        const submenu = event.target.closest('.host-file-context-submenu');
        if (event.key === 'ArrowRight' && submenu) {
            event.preventDefault();
            submenu.querySelector('.host-file-context-submenu-panel button:not(:disabled)')?.focus();
            return;
        }
        const scope = event.target.closest('.host-file-context-submenu-panel') || menu;
        const choices = [...scope.querySelectorAll('button:not(:disabled),a[href]')].filter(item => item.getClientRects().length &&
            (scope !== menu || !item.closest('.host-file-context-submenu-panel')));
        if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key) || !choices.length) return;
        event.preventDefault();
        const index = choices.indexOf(document.activeElement);
        const next = event.key === 'Home' ? 0 : event.key === 'End' ? choices.length - 1 :
            (index + (event.key === 'ArrowDown' ? 1 : -1) + choices.length) % choices.length;
        choices[next].focus();
    };
    const dismissOutside = event => { if (!menu.contains(event.target)) close(); };
    const dismissNavigation = event => {
        if (!shell.classList.contains('mobile-open')) return;
        const escape = event.type === 'keydown' && event.key === 'Escape';
        if (event.type === 'keydown' && !escape) return;
        if (!escape && shell.contains(event.target)) return;
        callback?.invokeMethodAsync('DismissMobileMenuAsync').catch(() => {});
        if (escape) shell.querySelector('.sidebar-mobile-menu-button')?.focus();
    };
    document.addEventListener('pointerdown', dismissNavigation, true);
    document.addEventListener('focusin', dismissNavigation, true);
    document.addEventListener('keydown', dismissNavigation, true);
    sidebar.addEventListener('contextmenu', open);
    document.addEventListener('pointerdown', dismissOutside, true);
    document.addEventListener('focusin', dismissOutside, true);
    menu.addEventListener('click', click);
    menu.addEventListener('keydown', key);
    // Manual dismissal avoids native light-dismiss on the right-button release
    // that follows opening a context menu. Keep the top layer to prevent clipping.
    shell.dataset.sidebarReady = "true";
    bindings.set(shell, () => {
        delete shell.dataset.sidebarReady;
        document.removeEventListener('pointerdown', dismissNavigation, true);
        document.removeEventListener('focusin', dismissNavigation, true);
        document.removeEventListener('keydown', dismissNavigation, true);
        close(); sidebar.removeEventListener('contextmenu', open);
        menu.removeEventListener('click', click); menu.removeEventListener('keydown', key);
        document.removeEventListener('pointerdown', dismissOutside, true);
        document.removeEventListener('focusin', dismissOutside, true);
    });
}
export function unbind(shell) { bindings.get(shell)?.(); bindings.delete(shell); }
