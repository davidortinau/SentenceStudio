// sam-overlay-escape.test.js
// Run: node --test tests/js/sam-overlay-escape.test.js
//
// Exercises the SHIPPED module (sam-overlay.js) against a minimal DOM double.
//
// What this covers is the half of the Escape decision that lives in the browser. The other half —
// which overlay state a press resolves to once .NET is told about it — is covered by
// SamOverlayEscapeTests. Between them the contract is: one press closes exactly one layer.
//
// The defect these were written for: the overlay's Escape listener and Blazor's delegated listener
// are both on `document`, so a press inside the report panel ran both, and one press collapsed the
// panel AND the overlay. `stopPropagation` cannot fix that — two listeners on the same node both
// run regardless — so the module reads the press's own ancestry instead, which is a decision made
// at dispatch time and independent of which listener was attached first.

import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';

// ---------------------------------------------------------------- DOM double

/**
 * Just enough of an element to answer `closest` and `setAttribute`/`removeAttribute`/`getAttribute`,
 * which is what the module asks of a press target and of the app shell element.
 */
class MockNode {
    constructor(attributes = {}) {
        this.attributes = { ...attributes };
        this.parentElement = null;
    }

    append(child) {
        child.parentElement = this;
        return child;
    }

    setAttribute(name, value) { this.attributes[name] = value; }
    removeAttribute(name) { delete this.attributes[name]; }
    getAttribute(name) { return this.attributes[name] ?? null; }
    hasAttribute(name) { return Object.prototype.hasOwnProperty.call(this.attributes, name); }

    matches(selector) {
        // Supports bare attribute presence [attr] and class selectors .class
        if (selector.startsWith('[') && selector.endsWith(']')) {
            const name = selector.slice(1, -1);
            return this.hasAttribute(name);
        }
        if (selector.startsWith('.')) {
            const cls = selector.slice(1);
            return (this.attributes['class'] || '').split(/\s+/).includes(cls);
        }
        return false;
    }

    closest(selector) {
        let node = this;
        while (node) {
            if (node.matches(selector)) {
                return node;
            }
            node = node.parentElement;
        }
        return null;
    }
}

class DotNetDouble {
    constructor() { this.calls = []; }

    invokeMethodAsync(name, ...args) {
        this.calls.push({ name, args });
        return Promise.resolve();
    }

    get escapes() { return this.calls.filter(c => c.name === 'OnEscapePressed').length; }
}

let keyListeners;
let _mainContentElement;

function installDom() {
    keyListeners = [];
    _mainContentElement = new MockNode({ class: 'main-content' });

    globalThis.document = {
        addEventListener: (type, fn) => { if (type === 'keydown') keyListeners.push(fn); },
        removeEventListener: (type, fn) => {
            if (type === 'keydown') keyListeners = keyListeners.filter(f => f !== fn);
        },
        querySelector: (selector) => {
            if (selector === '.main-content') return _mainContentElement;
            return null;
        },
        getElementById: () => null
    };

    globalThis.window = {
        addEventListener: () => {},
        removeEventListener: () => {},
        innerWidth: 1280
    };
}

/** Returns the mock `.main-content` element for assertions. */
function getMainContent() { return _mainContentElement; }

/** Dispatches a keydown the way the browser would, to every registered listener. */
function press(key, target) {
    for (const fn of [...keyListeners]) {
        fn({ key, target });
    }
}

installDom();

const { escapeIsOwnedByInnerSurface, initSamOverlay, disposeSamOverlay, setAppShellInert } =
    await import('../../src/SentenceStudio.UI/wwwroot/js/sam-overlay.js');

// ================================================================ the ownership test itself

describe('escapeIsOwnedByInnerSurface', () => {
    it('claims a press that came from inside a marked subtree', () => {
        const footer = new MockNode({ 'data-sam-escape-owner': 'report' });
        const button = footer.append(new MockNode());

        assert.equal(escapeIsOwnedByInnerSurface(button), true);
    });

    it('claims the marked element itself, not only its descendants', () => {
        const footer = new MockNode({ 'data-sam-escape-owner': 'report' });

        assert.equal(escapeIsOwnedByInnerSurface(footer), true);
    });

    it('declines a press from elsewhere in the overlay', () => {
        const panel = new MockNode();
        const elsewhere = panel.append(new MockNode());

        assert.equal(escapeIsOwnedByInnerSurface(elsewhere), false);
    });

    it('declines a press from a sibling message whose panel is closed', () => {
        const conversation = new MockNode();
        const open = conversation.append(new MockNode({ 'data-sam-escape-owner': 'report' }));
        const closed = conversation.append(new MockNode());

        assert.equal(escapeIsOwnedByInnerSurface(open), true);
        assert.equal(escapeIsOwnedByInnerSurface(closed), false,
            'the marker is per message, so one open panel does not silence the whole conversation');
    });

    it('declines a target that is not an element at all', () => {
        // `document` and `window` reach a keydown handler as targets and have no `closest`.
        assert.equal(escapeIsOwnedByInnerSurface(globalThis.document), false);
        assert.equal(escapeIsOwnedByInnerSurface(null), false);
        assert.equal(escapeIsOwnedByInnerSurface(undefined), false);
        assert.equal(escapeIsOwnedByInnerSurface({}), false);
    });
});

// ================================================================ what the listener does with it

describe('sam-overlay escape listener', () => {
    beforeEach(() => installDom());
    afterEach(() => disposeSamOverlay());

    it('does not tell .NET about a press the report panel owns', () => {
        const dotNet = new DotNetDouble();
        initSamOverlay(dotNet);

        const footer = new MockNode({ 'data-sam-escape-owner': 'report' });
        press('Escape', footer.append(new MockNode()));

        assert.equal(dotNet.escapes, 0,
            'the panel closes itself; asking the overlay too would close two layers on one press');
    });

    it('tells .NET about a press from anywhere else', () => {
        const dotNet = new DotNetDouble();
        initSamOverlay(dotNet);

        press('Escape', new MockNode());

        assert.equal(dotNet.escapes, 1);
    });

    it('ignores every other key', () => {
        const dotNet = new DotNetDouble();
        initSamOverlay(dotNet);

        press('Enter', new MockNode());
        press('a', new MockNode());
        press('Tab', new MockNode());

        assert.equal(dotNet.escapes, 0);
    });

    it('stops listening once the overlay is gone', () => {
        const dotNet = new DotNetDouble();
        initSamOverlay(dotNet);
        disposeSamOverlay();

        press('Escape', new MockNode());

        assert.equal(dotNet.escapes, 0);
    });

    it('reports one press to .NET exactly once', () => {
        const dotNet = new DotNetDouble();
        initSamOverlay(dotNet);

        press('Escape', new MockNode());
        press('Escape', new MockNode());

        assert.equal(dotNet.escapes, 2, 'two presses, two layers — never two layers on one press');
    });
});

// ================================================================ setAppShellInert

describe('setAppShellInert', () => {
    beforeEach(() => installDom());
    afterEach(() => disposeSamOverlay());

    it('sets inert attribute on .main-content when true', () => {
        setAppShellInert(true);
        const el = getMainContent();
        assert.equal(el.hasAttribute('inert'), true);
    });

    it('removes inert attribute on .main-content when false', () => {
        const el = getMainContent();
        el.setAttribute('inert', '');
        setAppShellInert(false);
        assert.equal(el.hasAttribute('inert'), false);
    });

    it('tolerates missing .main-content without throwing', () => {
        // Replace querySelector to return null
        globalThis.document.querySelector = () => null;
        assert.doesNotThrow(() => setAppShellInert(true));
        assert.doesNotThrow(() => setAppShellInert(false));
    });

    it('dispose always clears inert even if it was set', () => {
        const dotNet = new DotNetDouble();
        initSamOverlay(dotNet);
        setAppShellInert(true);
        disposeSamOverlay();
        assert.equal(getMainContent().hasAttribute('inert'), false);
    });

    it('does not leak inert state between init/dispose cycles', () => {
        const dotNet = new DotNetDouble();
        initSamOverlay(dotNet);
        setAppShellInert(true);
        disposeSamOverlay();

        // Fresh cycle
        installDom();
        initSamOverlay(new DotNetDouble());
        assert.equal(getMainContent().hasAttribute('inert'), false,
            'new cycle starts with clean inert state');
        disposeSamOverlay();
    });
});
