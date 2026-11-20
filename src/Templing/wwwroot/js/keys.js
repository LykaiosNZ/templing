// Global keyboard shortcuts. .NET tells us which combos are bound; we preventDefault those
// and forward them. Typing in inputs never triggers shortcuts (except Escape).
let dotnet = null;
let bound = new Set();
let pendingG = 0;

function isTyping(el) {
    if (!el) return false;
    const tag = el.tagName;
    if (el.isContentEditable) return true;
    if (tag === 'TEXTAREA' || tag === 'SELECT') return true;
    if (tag === 'INPUT') {
        const t = (el.type || '').toLowerCase();
        return !['checkbox', 'radio', 'button', 'submit', 'range', 'reset', 'file', 'color'].includes(t);
    }
    return false;
}

function combo(e) {
    let k = e.key;
    if (k === ' ') k = 'Space';
    if (k.length === 1) k = k.toLowerCase();
    const parts = [];
    if (e.ctrlKey || e.metaKey) parts.push('Ctrl');
    if (e.altKey) parts.push('Alt');
    // Shift is implied by the character for symbols like ? and +
    if (e.shiftKey && (k.length > 1 || /[a-z]/.test(k))) parts.push('Shift');
    parts.push(k);
    return parts.join('+');
}

function onKeyDown(e) {
    if (!dotnet || e.isComposing || e.repeat && !['ArrowUp', 'ArrowDown'].includes(e.key)) return;
    const typing = isTyping(e.target);
    if (typing && e.key !== 'Escape') return;
    // let dialogs / menus handle their own keys
    const inOverlay = e.target.closest && e.target.closest('.mud-dialog, .mud-popover-open, [role="listbox"], [role="menu"]');
    if (inOverlay && e.key !== 'Escape') return;
    if (inOverlay) return;

    let c = combo(e);
    // "g then x" navigation chords
    if (pendingG && Date.now() - pendingG < 1200 && c.length === 1) {
        pendingG = 0;
        c = 'g ' + c;
        if (bound.has(c)) { e.preventDefault(); dotnet.invokeMethodAsync('OnShortcut', c); }
        return;
    }
    if (c === 'g' && !bound.has('g')) { pendingG = Date.now(); return; }

    if (!bound.has(c)) return;
    // Space/Enter on a focused button or link should activate that control, not the shortcut
    if ((c === 'Space' || c === 'Enter') && e.target.closest && e.target.closest('button, a, [role="button"], [role="tab"], [role="slider"], [role="switch"], [role="checkbox"]')) return;
    // Arrow keys on sliders / tabs belong to the control
    if (c.startsWith('Arrow') || c.startsWith('Shift+Arrow')) {
        if (e.target.closest && e.target.closest('[role="slider"], [role="tablist"], [role="radiogroup"], .mud-slider, .mud-toggle-group')) return;
    }
    e.preventDefault();
    dotnet.invokeMethodAsync('OnShortcut', c);
}

export function init(ref) {
    dotnet = ref;
    document.addEventListener('keydown', onKeyDown);
}

export function setBindings(list) { bound = new Set(list); }

// Focus an element, waiting up to a second for it to render (e.g. right after navigating to its page).
export async function focus(selector) {
    for (let t = 0; t < 60; t++) {
        const el = document.querySelector(selector);
        if (el) { el.focus(); if (el.select) el.select(); hold(el); return; }
        await new Promise(r => requestAnimationFrame(r));
    }
}

// A menu that opened this dialog hands focus back to its own button as it closes, which can land just after
// we focused something in the dialog. For a moment, take focus back if it leaves the dialog (moves inside it are fine).
function hold(el, ms = 400) {
    const scope = el.closest('.mud-dialog');
    if (!scope) return;
    const until = Date.now() + ms;
    const check = () => {
        if (!el.isConnected || Date.now() > until) return;
        if (!scope.contains(document.activeElement)) el.focus();
        requestAnimationFrame(check);
    };
    requestAnimationFrame(check);
}
