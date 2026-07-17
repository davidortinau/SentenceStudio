// photo-viewer-gestures.test.js
// Tests for gesture module attach/detach, diagnostics flag, and lifecycle cleanup.
// Run: node --test tests/js/photo-viewer-gestures.test.js
//
// Uses a minimal JSDOM-like mock since the gesture module requires DOM APIs
// (getElementById, addEventListener, etc.) that are not available in Node.
import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';

// --- Minimal DOM mock ---

class MockElement {
    constructor(id) {
        this.id = id;
        this.attributes = new Map();
        this.style = {};
        this._listeners = {};
    }
    setAttribute(name, value) { this.attributes.set(name, String(value)); }
    removeAttribute(name) { this.attributes.delete(name); }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    hasAttribute(name) { return this.attributes.has(name); }
    addEventListener(type, fn, opts) {
        if (!this._listeners[type]) this._listeners[type] = [];
        this._listeners[type].push(fn);
    }
    removeEventListener(type, fn, opts) {
        if (!this._listeners[type]) return;
        this._listeners[type] = this._listeners[type].filter(f => f !== fn);
    }
    dispatchEvent(type, event) {
        for (const listener of this._listeners[type] || []) listener(event);
    }
    setPointerCapture() {}
    releasePointerCapture() {}
    getBoundingClientRect() { return { left: 0, top: 0, width: 800, height: 600 }; }
    get naturalWidth() { return 1600; }
    get naturalHeight() { return 1200; }
}

// Mock ResizeObserver
class MockResizeObserver {
    constructor() {
        this.disconnectCount = 0;
        MockResizeObserver.instances.push(this);
    }
    observe() {}
    disconnect() { this.disconnectCount++; }
}
MockResizeObserver.instances = [];

let elements = {};

function setupGlobalDom() {
    MockResizeObserver.instances = [];
    globalThis.document = {
        getElementById(id) { return elements[id] || null; }
    };
    globalThis.window = {
        addEventListener() {},
        removeEventListener() {}
    };
    globalThis.ResizeObserver = MockResizeObserver;
    globalThis.requestAnimationFrame = (fn) => fn();
    globalThis.Date = Date;
}

function teardownGlobalDom() {
    delete globalThis.document;
    delete globalThis.window;
    delete globalThis.ResizeObserver;
    delete globalThis.requestAnimationFrame;
}

// Dynamic import the module fresh each time to reset module-level state
async function importFreshModule() {
    // Use a cache-busting query param to force a fresh module evaluation
    const cacheBuster = `?t=${Date.now()}-${Math.random()}`;
    return import(`../../src/SentenceStudio.UI/wwwroot/js/photo-viewer-gestures.js${cacheBuster}`);
}

let nextPointerId = 1;

function tap(element, x, y) {
    const pointerId = nextPointerId++;
    const event = {
        pointerId,
        clientX: x,
        clientY: y,
        preventDefault() {}
    };
    element.dispatchEvent('pointerdown', event);
    element.dispatchEvent('pointerup', event);
}

function doubleTap(element, x = 600, y = 300) {
    tap(element, x, y);
    tap(element, x, y);
}

describe('attach with diagnostics: false (default)', () => {
    beforeEach(() => {
        elements = {
            'test-image': new MockElement('test-image'),
            'test-overlay': new MockElement('test-overlay')
        };
        setupGlobalDom();
    });

    afterEach(() => {
        teardownGlobalDom();
        elements = {};
    });

    it('should attach successfully without diagnostics', async () => {
        const mod = await importFreshModule();
        const result = mod.attach('test-image', 'test-overlay');
        assert.equal(result, true);
        mod.detach();
    });

    it('should NOT set data-testid attributes without diagnostics', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay');
        assert.equal(elements['test-image'].hasAttribute('data-testid'), false);
        assert.equal(elements['test-overlay'].hasAttribute('data-testid'), false);
        mod.detach();
    });

    it('should NOT set data-debug-* attributes without diagnostics', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay');
        assert.equal(elements['test-overlay'].hasAttribute('data-debug-scale'), false);
        assert.equal(elements['test-overlay'].hasAttribute('data-debug-translate-x'), false);
        assert.equal(elements['test-overlay'].hasAttribute('data-debug-translate-y'), false);
        assert.equal(elements['test-overlay'].hasAttribute('data-debug-pointers'), false);
        assert.equal(elements['test-overlay'].hasAttribute('data-debug-reset'), false);
        mod.detach();
    });

    it('should apply touch-action:none on the image', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay');
        assert.equal(elements['test-image'].style.touchAction, 'none');
        mod.detach();
    });

    it('should apply transform on attach', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay');
        assert.equal(elements['test-image'].style.transformOrigin, 'center center');
        assert.ok(elements['test-image'].style.transform.includes('scale(1)'));
        mod.detach();
    });

    it('should clean up touch-action on detach', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay');
        mod.detach();
        assert.equal(elements['test-image'].style.touchAction, '');
    });

    it('should remove every handler and disconnect the resize observer on detach', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay');
        const observer = MockResizeObserver.instances.at(-1);

        mod.detach();

        for (const listeners of Object.values(elements['test-image']._listeners)) {
            assert.equal(listeners.length, 0);
        }
        assert.equal(observer.disconnectCount, 1);
    });

    it('should NOT try to remove data-testid on detach when diagnostics off', async () => {
        const mod = await importFreshModule();
        // Pre-set some attribute to verify it's NOT touched
        elements['test-image'].setAttribute('data-testid', 'pre-existing');
        mod.attach('test-image', 'test-overlay');
        mod.detach();
        // Since diagnostics is off, detach should NOT remove the pre-existing attribute
        assert.equal(elements['test-image'].getAttribute('data-testid'), 'pre-existing');
    });
});

describe('double-tap reset generation', () => {
    beforeEach(() => {
        elements = {
            'test-image': new MockElement('test-image'),
            'test-overlay': new MockElement('test-overlay')
        };
        nextPointerId = 1;
        setupGlobalDom();
    });

    afterEach(() => {
        teardownGlobalDom();
        elements = {};
    });

    it('increments exactly once only when a double-tap resets zoom', async () => {
        const mod = await importFreshModule();
        const image = elements['test-image'];
        mod.attach('test-image', 'test-overlay', { diagnostics: true });

        doubleTap(image);
        let current = mod.getState();
        assert.equal(current.scale, 2.5);
        assert.notEqual(current.tx, 0);
        assert.equal(current.resetGeneration, 0);

        doubleTap(image);
        current = mod.getState();
        assert.deepEqual(current, {
            scale: 1,
            tx: 0,
            ty: 0,
            pointerCount: 0,
            resetGeneration: 1
        });
        assert.equal(elements['test-overlay'].getAttribute('data-debug-reset'), '1');

        doubleTap(image);
        assert.equal(mod.getState().resetGeneration, 1);
        doubleTap(image);
        assert.deepEqual(mod.getState(), {
            scale: 1,
            tx: 0,
            ty: 0,
            pointerCount: 0,
            resetGeneration: 2
        });
        assert.equal(elements['test-overlay'].getAttribute('data-debug-reset'), '2');
        mod.detach();
    });

    it('preserves behavior without emitting diagnostic attributes', async () => {
        const mod = await importFreshModule();
        const image = elements['test-image'];
        const overlay = elements['test-overlay'];
        mod.attach('test-image', 'test-overlay', { diagnostics: false });

        doubleTap(image);
        assert.equal(mod.getState().scale, 2.5);
        assert.equal(mod.getState().resetGeneration, 0);

        doubleTap(image);
        assert.deepEqual(mod.getState(), {
            scale: 1,
            tx: 0,
            ty: 0,
            pointerCount: 0,
            resetGeneration: 1
        });
        assert.equal(
            [...overlay.attributes.keys()].some(name => name.startsWith('data-debug-')),
            false
        );
        mod.detach();
    });
});

describe('attach with diagnostics: true', () => {
    beforeEach(() => {
        elements = {
            'test-image': new MockElement('test-image'),
            'test-overlay': new MockElement('test-overlay')
        };
        setupGlobalDom();
    });

    afterEach(() => {
        teardownGlobalDom();
        elements = {};
    });

    it('should set data-testid attributes with diagnostics', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay', { diagnostics: true });
        assert.equal(elements['test-image'].getAttribute('data-testid'), 'photo-viewer-image');
        assert.equal(elements['test-overlay'].getAttribute('data-testid'), 'photo-viewer-overlay');
        mod.detach();
    });

    it('should set data-debug-* attributes with diagnostics', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay', { diagnostics: true });
        assert.equal(elements['test-overlay'].getAttribute('data-debug-scale'), '1.000');
        assert.equal(elements['test-overlay'].getAttribute('data-debug-translate-x'), '0.0');
        assert.equal(elements['test-overlay'].getAttribute('data-debug-translate-y'), '0.0');
        assert.equal(elements['test-overlay'].getAttribute('data-debug-pointers'), '0');
        assert.equal(elements['test-overlay'].getAttribute('data-debug-reset'), '0');
        mod.detach();
    });

    it('should remove data-debug-* attributes on detach', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay', { diagnostics: true });
        mod.detach();
        assert.equal(elements['test-overlay'].hasAttribute('data-debug-scale'), false);
        assert.equal(elements['test-overlay'].hasAttribute('data-debug-translate-x'), false);
        assert.equal(elements['test-overlay'].hasAttribute('data-debug-translate-y'), false);
        assert.equal(elements['test-overlay'].hasAttribute('data-debug-pointers'), false);
        assert.equal(elements['test-overlay'].hasAttribute('data-debug-reset'), false);
    });

    it('should remove data-testid attributes on detach', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay', { diagnostics: true });
        mod.detach();
        assert.equal(elements['test-image'].hasAttribute('data-testid'), false);
        assert.equal(elements['test-overlay'].hasAttribute('data-testid'), false);
    });

    it('should update debug attributes on reset', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay', { diagnostics: true });
        mod.reset();
        assert.equal(elements['test-overlay'].getAttribute('data-debug-reset'), '1');
        mod.detach();
    });
});

describe('attach edge cases', () => {
    beforeEach(() => {
        elements = {};
        setupGlobalDom();
    });

    afterEach(() => {
        teardownGlobalDom();
    });

    it('returns false when image element not found', async () => {
        const mod = await importFreshModule();
        const result = mod.attach('nonexistent-image', 'nonexistent-overlay');
        assert.equal(result, false);
    });

    it('returns false when overlay element not found', async () => {
        elements['test-image'] = new MockElement('test-image');
        const mod = await importFreshModule();
        const result = mod.attach('test-image', 'nonexistent-overlay');
        assert.equal(result, false);
    });

    it('detach is safe when not attached', async () => {
        const mod = await importFreshModule();
        // Should not throw
        mod.detach();
    });

    it('reset is safe when not attached', async () => {
        const mod = await importFreshModule();
        // Should not throw
        mod.reset();
    });

    it('getState returns null when not attached', async () => {
        const mod = await importFreshModule();
        assert.equal(mod.getState(), null);
    });

    it('getState returns state when attached', async () => {
        elements = {
            'test-image': new MockElement('test-image'),
            'test-overlay': new MockElement('test-overlay')
        };
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay');
        const s = mod.getState();
        assert.deepEqual(s, {
            scale: 1,
            tx: 0,
            ty: 0,
            pointerCount: 0,
            resetGeneration: 0
        });
        mod.detach();
    });
});

describe('lifecycle cleanup', () => {
    beforeEach(() => {
        elements = {
            'test-image': new MockElement('test-image'),
            'test-overlay': new MockElement('test-overlay')
        };
        setupGlobalDom();
    });

    afterEach(() => {
        teardownGlobalDom();
        elements = {};
    });

    it('detach clears transform styles', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay');
        // Transform was set on attach
        assert.ok(elements['test-image'].style.transform);
        mod.detach();
        assert.equal(elements['test-image'].style.transform, '');
        assert.equal(elements['test-image'].style.transformOrigin, '');
    });

    it('re-attach after detach works cleanly', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay', { diagnostics: true });
        mod.detach();
        // Re-attach
        const result = mod.attach('test-image', 'test-overlay', { diagnostics: true });
        assert.equal(result, true);
        assert.equal(elements['test-overlay'].getAttribute('data-debug-scale'), '1.000');
        mod.detach();
    });

    it('double detach is safe', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay');
        mod.detach();
        mod.detach(); // Should not throw
    });

    it('attach auto-detaches previous instance', async () => {
        const mod = await importFreshModule();
        mod.attach('test-image', 'test-overlay', { diagnostics: true });
        assert.equal(elements['test-overlay'].hasAttribute('data-debug-scale'), true);

        // Create new elements for second attach
        const img2 = new MockElement('test-image2');
        const ov2 = new MockElement('test-overlay2');
        elements['test-image2'] = img2;
        elements['test-overlay2'] = ov2;

        // Second attach should detach the first (cleaning up debug attrs on first overlay)
        mod.attach('test-image2', 'test-overlay2', { diagnostics: true });
        assert.equal(elements['test-overlay'].hasAttribute('data-debug-scale'), false);
        assert.equal(ov2.hasAttribute('data-debug-scale'), true);
        mod.detach();
    });
});
