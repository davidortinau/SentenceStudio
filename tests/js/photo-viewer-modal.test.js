import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';

class MockElement {
    constructor(id) {
        this.id = id;
        this.parentElement = null;
        this.children = [];
        this.hidden = false;
        this._listeners = {};
        this._focusable = [];
    }
    append(child) {
        child.parentElement = this;
        this.children.push(child);
    }
    contains(candidate) {
        if (candidate === this) return true;
        return this.children.some(child => child.contains(candidate));
    }
    querySelectorAll() { return this._focusable; }
    addEventListener(type, fn) { this._listeners[type] = fn; }
    removeEventListener(type, fn) {
        if (this._listeners[type] === fn) delete this._listeners[type];
    }
    dispatchKey(key, shiftKey = false) {
        let prevented = false;
        this._listeners.keydown?.({
            key,
            shiftKey,
            preventDefault() { prevented = true; }
        });
        return prevented;
    }
    focus() { globalThis.document.activeElement = this; }
}

let elements;

function setup() {
    elements = {};
    globalThis.document = {
        activeElement: null,
        getElementById(id) { return elements[id] ?? null; }
    };
}

function teardown() {
    delete globalThis.document;
    elements = {};
}

async function importFreshModule() {
    return import(`../../src/SentenceStudio.UI/wwwroot/js/photo-viewer-modal.js?${Date.now()}-${Math.random()}`);
}

describe('fullscreen modal structure and focus containment', () => {
    beforeEach(setup);
    afterEach(teardown);

    it('accepts a sibling dialog and focuses inside it', async () => {
        const host = new MockElement('host');
        const content = new MockElement('content');
        const dialog = new MockElement('dialog');
        const close = new MockElement('close');
        host.append(content);
        host.append(dialog);
        dialog.append(close);
        dialog._focusable = [close];
        Object.assign(elements, { content, dialog, close });

        const modal = await importFreshModule();
        assert.equal(modal.activate('dialog', 'content', 'close'), true);
        assert.equal(document.activeElement, close);
        modal.deactivate();
    });

    it('rejects a dialog nested under inert-able content', async () => {
        const content = new MockElement('content');
        const dialog = new MockElement('dialog');
        const close = new MockElement('close');
        content.append(dialog);
        dialog.append(close);
        Object.assign(elements, { content, dialog, close });

        const modal = await importFreshModule();
        assert.throws(
            () => modal.activate('dialog', 'content', 'close'),
            /must be a sibling/);
    });

    it('contains Tab and Shift+Tab inside the dialog', async () => {
        const host = new MockElement('host');
        const content = new MockElement('content');
        const dialog = new MockElement('dialog');
        const first = new MockElement('first');
        const last = new MockElement('last');
        host.append(content);
        host.append(dialog);
        dialog.append(first);
        dialog.append(last);
        dialog._focusable = [first, last];
        Object.assign(elements, { content, dialog, first });

        const modal = await importFreshModule();
        modal.activate('dialog', 'content', 'first');
        assert.equal(dialog.dispatchKey('Tab'), true);
        assert.equal(document.activeElement, last);
        assert.equal(dialog.dispatchKey('Tab'), true);
        assert.equal(document.activeElement, first);
        assert.equal(dialog.dispatchKey('Tab', true), true);
        assert.equal(document.activeElement, last);
        modal.deactivate();
    });

    it('removes the focus trap on deactivate', async () => {
        const host = new MockElement('host');
        const content = new MockElement('content');
        const dialog = new MockElement('dialog');
        const close = new MockElement('close');
        host.append(content);
        host.append(dialog);
        dialog.append(close);
        dialog._focusable = [close];
        Object.assign(elements, { content, dialog, close });

        const modal = await importFreshModule();
        modal.activate('dialog', 'content', 'close');
        modal.deactivate();

        assert.equal(dialog.dispatchKey('Tab'), false);
    });
});
