// sam-entry-autohide.test.js
// Run: node --test tests/js/sam-entry-autohide.test.js
//
// The floating Sam button's hide on scroll on the web (docs/sam-entry.md), in the SHIPPED
// sam-overlay.js module:
//
//   1. `SamEntryAutoHide` takes the same steps as the shared rule,
//      src/SentenceStudio.Native/Sam/NativeSamEntryAutoHide.cs; the cases below are the ones in
//      tests/SentenceStudio.Native.Tests/NativeSamEntryAutoHideTests.cs that apply to the web
//      (the web has no screen-reader signal).
//   2. The page wiring: the shell's scroll moves the button, other scrollers do not, focus on the
//      button brings it back, a text field on a touch device hides it, and dispose removes every
//      listener and leaves the button shown.
//
// The real layout, CSS and motion are checked in a browser against the real stylesheet; this
// file pins the rule and the listener lifecycle.

import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';

// ---------------------------------------------------------------- DOM double

class MockElement {
    constructor({ tag = 'div', classes = [], type = null } = {}) {
        this.tagName = tag.toUpperCase();
        this.classes = new Set(classes);
        this.type = type;
        this.attributes = new Map();
        this.parentElement = null;
        this.scrollTop = 0;
        this.clientHeight = 600;
        this.scrollHeight = 2000;
    }

    matches(selectorList) {
        return selectorList.split(',').map(s => s.trim()).some(s => this.#matchesOne(s));
    }

    #matchesOne(selector) {
        if (selector.startsWith('.')) return this.classes.has(selector.slice(1));
        if (selector === 'textarea') return this.tagName === 'TEXTAREA';
        if (selector === 'input:not([type])') return this.tagName === 'INPUT' && this.type === null;
        const input = /^input\[type="(\w+)"\]$/.exec(selector);
        if (input) return this.tagName === 'INPUT' && this.type === input[1];
        return false;
    }

    closest(selector) {
        let node = this;
        while (node) {
            if (node.matches(selector)) return node;
            node = node.parentElement;
        }
        return null;
    }

    setAttribute(name, value) { this.attributes.set(name, String(value)); }
    removeAttribute(name) { this.attributes.delete(name); }
    hasAttribute(name) { return this.attributes.has(name); }
}

let listeners;
let shell;
let root;
let coarse;

function installDom() {
    listeners = [];
    shell = new MockElement({ tag: 'main', classes: ['main-content'] });
    root = new MockElement({ tag: 'html' });
    coarse = false;

    globalThis.document = {
        documentElement: root,
        body: new MockElement({ tag: 'body' }),
        scrollingElement: root,
        activeElement: null,
        addEventListener: (type, fn, options) => listeners.push({ type, fn, options }),
        removeEventListener: (type, fn) => {
            listeners = listeners.filter(l => !(l.type === type && l.fn === fn));
        },
        querySelector: (selector) => (selector === '.main-content' ? shell : null),
        getElementById: () => null
    };

    globalThis.window = {
        innerWidth: 1280,
        addEventListener: () => {},
        removeEventListener: () => {},
        matchMedia: (query) => ({ matches: query === '(pointer: coarse)' && coarse })
    };
}

function fire(type, event) {
    for (const l of listeners.filter(x => x.type === type)) l.fn(event);
}

function scrollShellTo(top) {
    shell.scrollTop = top;
    fire('scroll', { target: shell });
}

const isAway = () => root.hasAttribute('data-sam-entry-away');

installDom();

const {
    SamEntryAutoHide,
    SAM_ENTRY_THRESHOLD,
    initSamOverlay,
    disposeSamOverlay,
    attachSamEntryAutoHide,
    detachSamEntryAutoHide,
    resetSamEntry
} = await import('../../src/SentenceStudio.UI/wwwroot/js/sam-overlay.js');

// ================================================================ the shared rule

function hidden() {
    const entry = new SamEntryAutoHide();
    entry.onScroll(SAM_ENTRY_THRESHOLD, false, false);
    assert.equal(entry.isShown, false);
    return entry;
}

describe('SamEntryAutoHide (same steps as NativeSamEntryAutoHide)', () => {
    it('uses the shared 24 px threshold', () => assert.equal(SAM_ENTRY_THRESHOLD, 24));

    it('starts shown', () => assert.equal(new SamEntryAutoHide().isShown, true));

    it('scrolling down past the threshold hides it', () => {
        const entry = new SamEntryAutoHide();
        assert.equal(entry.onScroll(10, false, false), false);
        assert.equal(entry.isShown, true);
        assert.equal(entry.onScroll(SAM_ENTRY_THRESHOLD - 10, false, false), true);
        assert.equal(entry.isShown, false);
    });

    it('scrolling up past the threshold brings it back', () => {
        const entry = hidden();
        assert.equal(entry.onScroll(-10, false, false), false);
        assert.equal(entry.isShown, false);
        assert.equal(entry.onScroll(-(SAM_ENTRY_THRESHOLD - 10), false, false), true);
        assert.equal(entry.isShown, true);
    });

    it('changing direction starts the travel again', () => {
        const entry = new SamEntryAutoHide();
        entry.onScroll(20, false, false);
        entry.onScroll(-2, false, false);
        entry.onScroll(20, false, false);
        assert.equal(entry.isShown, true);
    });

    for (const [atStart, atEnd] of [[true, false], [false, true], [true, true]]) {
        it(`is shown at the top or the end (start=${atStart}, end=${atEnd})`, () => {
            const entry = hidden();
            assert.equal(entry.onScroll(40, atStart, atEnd), true);
            assert.equal(entry.isShown, true);
        });
    }

    it('leaving the end needs a fresh scroll down', () => {
        const entry = new SamEntryAutoHide();
        entry.onScroll(20, false, false);
        entry.onScroll(10, false, true);
        entry.onScroll(10, false, false);
        assert.equal(entry.isShown, true);
    });

    for (const delta of [Number.NaN, Number.POSITIVE_INFINITY, 0]) {
        it(`a non-moving or bad step (${delta}) changes nothing`, () => {
            const entry = new SamEntryAutoHide();
            entry.onScroll(20, false, false);
            assert.equal(entry.onScroll(delta, false, false), false);
            entry.onScroll(4, false, false);
            assert.equal(entry.isShown, false, 'the bad step did not reset the 20 travelled');
        });
    }

    it('the keyboard hides it wherever the scroll is', () => {
        const entry = new SamEntryAutoHide();
        assert.equal(entry.setKeyboardVisible(true), true);
        assert.equal(entry.isShown, false);
        assert.equal(entry.onScroll(-40, true, false), false);
        assert.equal(entry.isShown, false);
        assert.equal(entry.setKeyboardVisible(false), true);
        assert.equal(entry.isShown, true);
    });

    it('reset shows it and keeps the keyboard', () => {
        const entry = hidden();
        assert.equal(entry.reset(), true);
        assert.equal(entry.isShown, true);
        entry.setKeyboardVisible(true);
        entry.reset();
        assert.equal(entry.isShown, false);
    });

    it('reset clears half-finished travel', () => {
        const entry = new SamEntryAutoHide();
        entry.onScroll(20, false, false);
        entry.reset();
        entry.onScroll(10, false, false);
        assert.equal(entry.isShown, true);
    });
});

// ================================================================ the page wiring

describe('page wiring', () => {
    beforeEach(() => {
        installDom();
        attachSamEntryAutoHide();
    });

    it('listens for scroll in the capture phase, passively, so the shell is heard', () => {
        const scroll = listeners.find(l => l.type === 'scroll');
        assert.ok(scroll, 'a scroll listener is attached');
        assert.equal(scroll.options?.capture, true);
        assert.equal(scroll.options?.passive, true);
    });

    it('the shell scrolling down 24 px hides the button and scrolling up brings it back', () => {
        shell.scrollTop = 100;
        resetSamEntry();
        scrollShellTo(110);
        assert.equal(isAway(), false, '10 px is not enough');
        scrollShellTo(124);
        assert.equal(isAway(), true);
        scrollShellTo(110);
        assert.equal(isAway(), true, '14 px up is not enough');
        scrollShellTo(99);
        assert.equal(isAway(), false);
    });

    it('the top and the end always show it', () => {
        scrollShellTo(300);
        scrollShellTo(400);
        assert.equal(isAway(), true);
        scrollShellTo(shell.scrollHeight - shell.clientHeight);
        assert.equal(isAway(), false, 'at the end');
        scrollShellTo(1000);
        scrollShellTo(1100);
        assert.equal(isAway(), true);
        scrollShellTo(0);
        assert.equal(isAway(), false, 'at the top');
    });

    it('another scroller (a strip, Sam\'s transcript) does not move the button', () => {
        const strip = new MockElement();
        strip.scrollTop = 500;
        fire('scroll', { target: strip });
        strip.scrollTop = 900;
        fire('scroll', { target: strip });
        assert.equal(isAway(), false);
    });

    it('focus on the hidden button brings it back', () => {
        scrollShellTo(300);
        scrollShellTo(400);
        assert.equal(isAway(), true);

        const fab = new MockElement({ tag: 'button', classes: ['sam-fab'] });
        fire('focusin', { target: fab });

        assert.equal(isAway(), false);
    });

    it('a text field hides it on a touch device only', () => {
        const field = new MockElement({ tag: 'input', type: 'text' });

        fire('focusin', { target: field });
        assert.equal(isAway(), false, 'a mouse and keyboard leave the button where it is');
        fire('focusout', { target: field });

        coarse = true;
        fire('focusin', { target: field });
        assert.equal(isAway(), true);

        const next = new MockElement({ tag: 'textarea' });
        fire('focusout', { target: field });
        fire('focusin', { target: next });
        assert.equal(isAway(), true, 'moving between fields keeps it away');

        fire('focusout', { target: next });
        assert.equal(isAway(), false, 'the keyboard went away');
    });

    it('focus on a button does not count as the keyboard', () => {
        coarse = true;
        fire('focusin', { target: new MockElement({ tag: 'input', type: 'checkbox' }) });
        assert.equal(isAway(), false);
    });

    it('a new page starts shown', () => {
        scrollShellTo(300);
        scrollShellTo(400);
        assert.equal(isAway(), true);

        shell.scrollTop = 0;
        resetSamEntry();

        assert.equal(isAway(), false);
        scrollShellTo(10);
        assert.equal(isAway(), false, 'travel restarted from the new page');
    });

    it('detach removes every listener and leaves the button shown', () => {
        scrollShellTo(300);
        scrollShellTo(400);
        assert.equal(isAway(), true);

        detachSamEntryAutoHide();

        assert.equal(listeners.length, 0);
        assert.equal(isAway(), false);
    });

    it('attaching twice does not double the listeners', () => {
        attachSamEntryAutoHide();
        assert.equal(listeners.filter(l => l.type === 'scroll').length, 1);
        assert.equal(listeners.filter(l => l.type === 'focusin').length, 1);
        assert.equal(listeners.filter(l => l.type === 'focusout').length, 1);
    });
});

describe('overlay lifecycle', () => {
    it('initSamOverlay attaches the hide and disposeSamOverlay removes it', () => {
        installDom();
        initSamOverlay({ invokeMethodAsync: () => Promise.resolve() });
        assert.ok(listeners.some(l => l.type === 'scroll'));

        disposeSamOverlay();

        assert.equal(listeners.filter(l => ['scroll', 'focusin', 'focusout'].includes(l.type)).length, 0);
        assert.equal(isAway(), false);
    });
});
