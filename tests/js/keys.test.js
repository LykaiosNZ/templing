// Keyboard shortcuts: which key presses reach .NET, and as what combo (the format Hotkeys.cs uses).
import { describe, it, expect, beforeAll, beforeEach, afterEach, vi } from 'vitest';
import { init, setBindings } from '../../src/Templing/wwwroot/js/keys.js';

const sent = [];
let now = Date.UTC(2026, 0, 1);

beforeAll(() => init({ invokeMethodAsync: (name, combo) => { sent.push(combo); return Promise.resolve(); } }));

beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(now += 60_000); // well past any half-typed "g" chord from the last test
    sent.length = 0;
    document.body.innerHTML = '';
    setBindings(['t', '?', 'Space', 'Escape', 'ArrowUp', 'Shift+ArrowUp', 'Shift+t', 'Ctrl+s', 'g d', 'd', '+']);
});

afterEach(() => vi.useRealTimers());

function press(key, opts = {}, target = document.body) {
    const e = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...opts });
    target.dispatchEvent(e);
    return e;
}

function add(html) {
    document.body.insertAdjacentHTML('beforeend', html);
    return document.body.lastElementChild;
}

describe('combos', () => {
    it.each([
        ['t', {}, 't'],
        ['T', { shiftKey: true }, 'Shift+t'],
        ['?', { shiftKey: true }, '?'],          // Shift is part of the character
        ['+', { shiftKey: true }, '+'],
        [' ', {}, 'Space'],
        ['ArrowUp', {}, 'ArrowUp'],
        ['ArrowUp', { shiftKey: true }, 'Shift+ArrowUp'],
        ['s', { ctrlKey: true }, 'Ctrl+s'],
        ['s', { metaKey: true }, 'Ctrl+s'],      // Cmd on a Mac
    ])('%s %o is %s', (key, opts, combo) => {
        const e = press(key, opts);
        expect(sent).toEqual([combo]);
        expect(e.defaultPrevented).toBe(true);
    });

    it('leaves unbound keys alone', () => {
        const e = press('q');
        expect(sent).toEqual([]);
        expect(e.defaultPrevented).toBe(false);
    });

    it('ignores held keys, except the tempo arrows', () => {
        press('t', { repeat: true });
        press('ArrowUp', { repeat: true });
        expect(sent).toEqual(['ArrowUp']);
    });
});

describe('g chords', () => {
    it('g then a key is one combo', () => {
        press('g');
        press('d');
        expect(sent).toEqual(['g d']);
    });

    it('a slow second key is a key of its own', () => {
        press('g');
        vi.setSystemTime(now + 1500);
        press('d');
        expect(sent).toEqual(['d']);
    });
});

describe('where the focus is', () => {
    // (contenteditable is typing too, but jsdom has no isContentEditable to test it with)
    it('typing in a field is typing, but Escape still works', () => {
        const input = add('<input type="text">');
        press('t', {}, input);
        press('Escape', {}, input);
        expect(sent).toEqual(['Escape']);
        press('t', {}, add('<textarea></textarea>'));
        expect(sent).toEqual(['Escape']);
    });

    it('a checkbox or slider input is not a text field', () => {
        press('t', {}, add('<input type="checkbox">'));
        press('t', {}, add('<input type="range">'));
        expect(sent).toEqual(['t', 't']);
    });

    it('Space on a button presses the button', () => {
        const button = add('<button>Go</button>');
        const e = press(' ', {}, button);
        expect(sent).toEqual([]);
        expect(e.defaultPrevented).toBe(false);
        press('t', {}, button);
        expect(sent).toEqual(['t']);
    });

    it('arrow keys on a slider move the slider', () => {
        const slider = add('<div role="slider" tabindex="0"></div>');
        press('ArrowUp', {}, slider);
        press('ArrowUp', { shiftKey: true }, slider);
        expect(sent).toEqual([]);
    });

    it('dialogs and menus keep their keys, Escape included', () => {
        const inDialog = add('<div class="mud-dialog"><button>x</button></div>').firstElementChild;
        press('t', {}, inDialog);
        press('Escape', {}, inDialog);
        const inMenu = add('<ul role="menu"><li>a</li></ul>').firstElementChild;
        press('t', {}, inMenu);
        expect(sent).toEqual([]);
    });
});
