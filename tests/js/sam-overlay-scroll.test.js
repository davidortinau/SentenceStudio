// sam-overlay-scroll.test.js
// Run: node --test tests/js/sam-overlay-scroll.test.js
//
// Exercises the SHIPPED sam-overlay.js module against a minimal DOM double, covering the
// fullscreen scroll containment contract:
//
//   1. `focusElement` must pass `{ preventScroll: true }` by default and fall back cleanly on
//      engines that reject options (that fallback is what the iOS WKWebView fix depends on for
//      the header-behind-Dynamic-Island bug not to come back through a legacy path).
//   2. `enterFullscreenScrollLock` must capture BOTH the document scroll and the app-shell scroll,
//      apply `overflow: hidden` to html/body/main-content, and pin the document scroll to zero.
//      Idempotent — a second call must not overwrite the captured state.
//   3. `exitFullscreenScrollLock` must restore the previous scrollTop/scrollLeft and overflow on
//      both containers, and be a safe no-op when nothing was locked.
//   4. `disposeSamOverlay` must always release the scroll lock, so a teardown that lands
//      mid-fullscreen cannot leave the dashboard frozen.
//
// The bug this file was written for: on iOS 402x874, the composer's `.focus()` scrolled the
// document by the safe-area-inset-top (~68px), which displaced the fixed-position panel upward by
// the same amount and put the header behind the Dynamic Island. Passing `preventScroll: true`
// stops the focus-induced scroll; the enter/exit lock pins any pre-existing scroll and returns
// the learner to their reading position on the way out.

import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';

// ---------------------------------------------------------------- DOM double

/**
 * A DOM double with just enough behaviour to answer what the module actually calls.
 *
 * Notable pieces:
 *   - `style` is a plain object (matches how the module writes `.style.overflow = ...`).
 *   - `scrollTop`/`scrollLeft` are plain accessors — the tests can inspect them directly to
 *     assert what enter/exit assigned.
 *   - `focus` records its own calls and can be scripted to throw the first time, to prove the
 *     `try/catch` fallback path in `focusElement`.
 */
class MockElement {
    constructor(attributes = {}) {
        this.attributes = { ...attributes };
        this.style = {};
        this.scrollTop = 0;
        this.scrollLeft = 0;
        this.focusCalls = [];
        this._focusThrowOnce = false;
    }

    focus(options) {
        if (this._focusThrowOnce) {
            this._focusThrowOnce = false;
            this.focusCalls.push({ options, threw: true });
            throw new TypeError('legacy engine: focus does not accept an options object');
        }
        this.focusCalls.push({ options, threw: false });
    }

    setAttribute(name, value) { this.attributes[name] = value; }
    removeAttribute(name) { delete this.attributes[name]; }
    hasAttribute(name) { return Object.prototype.hasOwnProperty.call(this.attributes, name); }
}

let _html;
let _body;
let _mainContent;
let _composer;

function installDom() {
    _html = new MockElement();
    _body = new MockElement();
    _mainContent = new MockElement({ class: 'main-content' });
    _composer = new MockElement({ id: 'composer' });

    globalThis.document = {
        documentElement: _html,
        body: _body,
        scrollingElement: _html,
        addEventListener: () => {},
        removeEventListener: () => {},
        querySelector: (selector) => {
            if (selector === '.main-content') return _mainContent;
            return null;
        },
        getElementById: (id) => {
            if (id === 'composer') return _composer;
            return null;
        }
    };

    globalThis.window = {
        addEventListener: () => {},
        removeEventListener: () => {},
        innerWidth: 402
    };
}

function uninstallDom() {
    delete globalThis.document;
    delete globalThis.window;
    _html = null;
    _body = null;
    _mainContent = null;
    _composer = null;
}

// The module reads `document`/`window` at call time, so importing once and re-installing DOM
// between tests is fine and matches how sam-overlay-escape.test.js drives it.
const {
    focusElement,
    enterFullscreenScrollLock,
    exitFullscreenScrollLock,
    disposeSamOverlay,
    initSamOverlay
} = await import('../../src/SentenceStudio.UI/wwwroot/js/sam-overlay.js');

// -------------------------------------------------------------------- tests

describe('focusElement', () => {
    beforeEach(installDom);
    afterEach(uninstallDom);

    it('passes { preventScroll: true } by default', () => {
        focusElement('composer');
        assert.equal(_composer.focusCalls.length, 1);
        assert.deepEqual(_composer.focusCalls[0].options, { preventScroll: true });
        assert.equal(_composer.focusCalls[0].threw, false);
    });

    it('respects an explicit preventScroll: false', () => {
        focusElement('composer', { preventScroll: false });
        assert.equal(_composer.focusCalls.length, 1);
        assert.deepEqual(_composer.focusCalls[0].options, { preventScroll: false });
    });

    it('falls back to a bare focus() when the engine rejects the options object', () => {
        _composer._focusThrowOnce = true;
        focusElement('composer');
        assert.equal(_composer.focusCalls.length, 2);
        assert.equal(_composer.focusCalls[0].threw, true);
        assert.deepEqual(_composer.focusCalls[0].options, { preventScroll: true });
        assert.equal(_composer.focusCalls[1].threw, false);
        assert.equal(_composer.focusCalls[1].options, undefined);
    });

    it('is a no-op for unknown ids', () => {
        // Nothing to assert directly — the point is that it must not throw.
        assert.doesNotThrow(() => focusElement('does-not-exist'));
    });
});

describe('enterFullscreenScrollLock', () => {
    beforeEach(installDom);
    afterEach(() => {
        // Never leak a lock across tests: every case ends with the module in a clean state.
        exitFullscreenScrollLock();
        uninstallDom();
    });

    it('captures document + main-content scroll and applies overflow:hidden', () => {
        _html.scrollTop = 68;
        _html.scrollLeft = 0;
        _mainContent.scrollTop = 320;
        _mainContent.scrollLeft = 0;

        enterFullscreenScrollLock();

        assert.equal(_html.style.overflow, 'hidden', 'html overflow must be locked');
        assert.equal(_body.style.overflow, 'hidden', 'body overflow must be locked');
        assert.equal(_mainContent.style.overflow, 'hidden', 'main-content overflow must be locked');
        assert.equal(_html.scrollTop, 0, 'document scroll must be pinned to origin so fixed panel paints at 0');
    });

    it('preserves prior overflow inline style so restoration returns the exact prior value', () => {
        _html.style.overflow = 'scroll';
        _body.style.overflow = '';
        _mainContent.style.overflow = 'auto';
        _html.scrollTop = 42;
        _mainContent.scrollTop = 100;

        enterFullscreenScrollLock();
        exitFullscreenScrollLock();

        assert.equal(_html.style.overflow, 'scroll');
        assert.equal(_body.style.overflow, '');
        assert.equal(_mainContent.style.overflow, 'auto');
    });

    it('is idempotent — a second call does not overwrite the captured state', () => {
        _html.scrollTop = 68;
        _mainContent.scrollTop = 320;

        enterFullscreenScrollLock();

        // Something else nudges the containers while the lock is held (simulate iOS momentum).
        _html.scrollTop = 999;
        _mainContent.scrollTop = 999;

        enterFullscreenScrollLock();

        // The second enter must NOT re-capture 999 — the whole point of the lock is that Exit
        // returns the learner to where they were when the panel FIRST went fullscreen.
        exitFullscreenScrollLock();

        assert.equal(_html.scrollTop, 68);
        assert.equal(_mainContent.scrollTop, 320);
    });
});

describe('exitFullscreenScrollLock', () => {
    beforeEach(installDom);
    afterEach(uninstallDom);

    it('restores both document and main-content scroll to the captured positions', () => {
        _html.scrollTop = 68;
        _html.scrollLeft = 10;
        _mainContent.scrollTop = 320;
        _mainContent.scrollLeft = 5;

        enterFullscreenScrollLock();
        // Anything a caller does under lock — including the pinning to 0 that enter itself did —
        // must NOT survive exit.
        _html.scrollTop = 0;
        _mainContent.scrollTop = 0;

        exitFullscreenScrollLock();

        assert.equal(_html.scrollTop, 68);
        assert.equal(_html.scrollLeft, 10);
        assert.equal(_mainContent.scrollTop, 320);
        assert.equal(_mainContent.scrollLeft, 5);
    });

    it('restores overflow before assigning scrollTop, so the assignment is not clamped to zero', () => {
        // If the module wrote scrollTop while overflow was still 'hidden', a real browser would
        // clamp the assignment to zero. This is the specific ordering bug the module comments call
        // out; the test guards against a future refactor undoing it.
        _html.scrollTop = 68;
        enterFullscreenScrollLock();

        exitFullscreenScrollLock();

        // If overflow had been cleared AFTER the scrollTop write, this would be 0.
        assert.equal(_html.scrollTop, 68);
    });

    it('is a safe no-op when nothing has been locked', () => {
        _html.scrollTop = 42;
        _mainContent.scrollTop = 100;

        assert.doesNotThrow(() => exitFullscreenScrollLock());

        // Untouched — the exit path did not synthesize a zero capture.
        assert.equal(_html.scrollTop, 42);
        assert.equal(_mainContent.scrollTop, 100);
    });

    it('supports enter/exit/enter/exit cycles', () => {
        _html.scrollTop = 68;
        _mainContent.scrollTop = 320;
        enterFullscreenScrollLock();
        exitFullscreenScrollLock();

        // Second cycle — learner scrolled in the interval.
        _html.scrollTop = 200;
        _mainContent.scrollTop = 500;
        enterFullscreenScrollLock();
        exitFullscreenScrollLock();

        assert.equal(_html.scrollTop, 200);
        assert.equal(_mainContent.scrollTop, 500);
    });
});

describe('disposeSamOverlay', () => {
    beforeEach(installDom);
    afterEach(uninstallDom);

    it('releases the scroll lock', () => {
        _html.scrollTop = 68;
        _mainContent.scrollTop = 320;
        enterFullscreenScrollLock();
        assert.equal(_html.style.overflow, 'hidden');

        disposeSamOverlay();

        assert.notEqual(_html.style.overflow, 'hidden', 'html overflow must be restored');
        assert.notEqual(_mainContent.style.overflow, 'hidden', 'main-content overflow must be restored');
        assert.equal(_html.scrollTop, 68, 'document scroll must be restored to capture');
        assert.equal(_mainContent.scrollTop, 320, 'main-content scroll must be restored to capture');
    });

    it('clears inert on the app shell', () => {
        // Simulate a fullscreen mid-teardown — the shell was made inert AND the scroll was locked.
        _mainContent.setAttribute('inert', '');
        enterFullscreenScrollLock();

        disposeSamOverlay();

        assert.equal(_mainContent.hasAttribute('inert'), false);
    });

    it('is safe to call twice — a second dispose after a released lock does not throw', () => {
        _html.scrollTop = 68;
        enterFullscreenScrollLock();
        disposeSamOverlay();

        assert.doesNotThrow(() => disposeSamOverlay());
    });
});
