import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';

/*
 * Contract tests for the coach's JS behavior that Blazor cannot express:
 *   1. Ancestor-chain sibling inerting, with marker-based restoration.
 *   2. Modal lifecycle across open -> close -> open, where Blazor destroys and rebuilds the
 *      dialog element between opens.
 *   3. The composer's Enter guard (conditional preventDefault + IME composition state).
 *
 * The functions are reimplemented here against a minimal DOM double, mirroring
 * src/SentenceStudio.UI/wwwroot/js/app.js. That keeps them runnable under plain `node --test`
 * with no bundler or jsdom, matching the existing photo-viewer tests in this folder.
 */

// ---------------------------------------------------------------- DOM double

class MockElement {
    constructor(id, className = '') {
        this.id = id;
        this.parentElement = null;
        this.children = [];
        this.attributes = new Map();
        this.dataset = {};
        this._listeners = {};
        this.classList = {
            _names: new Set(className ? className.split(' ') : []),
            contains(name) { return this._names.has(name); },
            add(name) { this._names.add(name); },
            remove(name) { this._names.delete(name); }
        };
    }

    append(...children) {
        for (const child of children) {
            child.parentElement = this;
            this.children.push(child);
        }
        return this;
    }

    remove() {
        const siblings = this.parentElement?.children;
        if (siblings) {
            siblings.splice(siblings.indexOf(this), 1);
        }
        this.parentElement = null;
    }

    setAttribute(name, value) { this.attributes.set(name, value); }
    removeAttribute(name) { this.attributes.delete(name); }
    hasAttribute(name) { return this.attributes.has(name); }

    addEventListener(type, fn) { (this._listeners[type] ||= []).push(fn); }

    emit(type) { for (const fn of [...(this._listeners[type] || [])]) fn(); }

    dispatchKeyDown(event) {
        let prevented = false;
        for (const fn of this._listeners.keydown || []) {
            fn({ preventDefault() { prevented = true; }, ...event });
        }
        return prevented;
    }

    descendants() {
        return this.children.flatMap(child => [child, ...child.descendants()]);
    }
}

const COACH_INERT_MARKER = 'data-coach-inert';

let document;

function makeDocument(body) {
    return {
        body,
        getElementById(id) {
            return [body, ...body.descendants()].find(node => node.id === id) || null;
        },
        querySelectorAll(selector) {
            const name = selector.replace(/^\[|\]$/g, '');
            return body.descendants().filter(node => node.hasAttribute(name));
        }
    };
}

// ---------------------------------------------------------------- inert (mirrors app.js)

function applyBackgroundInert(modalEl) {
    let node = modalEl;

    while (node && node !== document.body && node.parentElement) {
        const parent = node.parentElement;
        for (const sibling of parent.children) {
            if (sibling === node) continue;
            if (sibling.classList && sibling.classList.contains('modal-backdrop')) continue;
            if (sibling.hasAttribute('inert')) continue;

            sibling.setAttribute('inert', '');
            sibling.setAttribute(COACH_INERT_MARKER, '');
        }
        node = parent;
    }
}

function releaseBackgroundInert() {
    for (const node of document.querySelectorAll('[' + COACH_INERT_MARKER + ']')) {
        node.removeAttribute('inert');
        node.removeAttribute(COACH_INERT_MARKER);
    }
}

/*
 * Mirrors the real tree: the coach modal is rendered by CoachWorkspaceHost inside MainLayout,
 * so it is a DESCENDANT of the Blazor app root, never a direct child of <body>.
 *
 *   body
 *     script            (sibling of the app root)
 *     div#app
 *       div.main-layout
 *         main#content        (sibling of the modal)
 *         div#coachWorkspace  <- the dialog, created and destroyed by Blazor
 */
function buildTree({ withModal = true } = {}) {
    const body = new MockElement('body');
    const script = new MockElement('analytics-script');
    const app = new MockElement('app');
    const layout = new MockElement('main-layout');
    const content = new MockElement('content');

    layout.append(content);
    app.append(layout);
    body.append(script, app);

    document = makeDocument(body);

    const modal = withModal ? mountModal(layout) : null;

    return { body, script, app, layout, content, modal };
}

/** Blazor rendering the dialog: a brand-new element that happens to reuse the same id. */
function mountModal(layout) {
    const modal = new MockElement('coachWorkspace');
    modal.append(new MockElement('workspace'));
    layout.append(modal);
    return modal;
}

describe('coach modal background inerting', () => {
    let tree;

    beforeEach(() => { tree = buildTree(); });

    it('never inerts an ancestor of the dialog', () => {
        applyBackgroundInert(tree.modal);

        // The regression: inerting every child of <body> caught the app root, which contains
        // the dialog, so the whole workspace went inert the moment it opened.
        assert.equal(tree.app.hasAttribute('inert'), false, 'app root must stay interactive');
        assert.equal(tree.layout.hasAttribute('inert'), false, 'layout must stay interactive');
    });

    it('leaves the dialog and its subtree interactive', () => {
        applyBackgroundInert(tree.modal);

        assert.equal(tree.modal.hasAttribute('inert'), false);
        assert.equal(tree.modal.children[0].hasAttribute('inert'), false);
    });

    it('inerts siblings at every level of the ancestor chain', () => {
        applyBackgroundInert(tree.modal);

        assert.equal(tree.content.hasAttribute('inert'), true, 'sibling inside the layout');
        assert.equal(tree.script.hasAttribute('inert'), true, 'sibling at body level');
    });

    it('restores every sibling it inerted', () => {
        applyBackgroundInert(tree.modal);
        releaseBackgroundInert();

        assert.equal(tree.content.hasAttribute('inert'), false);
        assert.equal(tree.script.hasAttribute('inert'), false);
    });

    it('restores the background even after Blazor destroyed the dialog element', () => {
        // The scenario that would otherwise freeze the app: the element vanishes before
        // cleanup runs, so anything keyed by that element is unreachable.
        applyBackgroundInert(tree.modal);
        tree.modal.remove();

        releaseBackgroundInert();

        assert.equal(tree.content.hasAttribute('inert'), false);
        assert.equal(tree.script.hasAttribute('inert'), false);
    });

    it('never clears an inert it did not set', () => {
        tree.content.setAttribute('inert', '');

        applyBackgroundInert(tree.modal);
        releaseBackgroundInert();

        assert.equal(tree.content.hasAttribute('inert'), true, 'another owner keeps its inert');
    });

    it('skips the Bootstrap backdrop', () => {
        const backdrop = new MockElement('backdrop', 'modal-backdrop');
        tree.body.append(backdrop);

        applyBackgroundInert(tree.modal);

        assert.equal(backdrop.hasAttribute('inert'), false);
    });
});

// ---------------------------------------------------------------- modal lifecycle

/** Bootstrap double: instances are keyed off the ELEMENT, as Bootstrap 5 does. */
const bootstrapInstances = new Map();

class MockModal {
    constructor(element) {
        this.element = element;
        this.shown = 0;
        this.hidden = 0;
        this.disposed = false;
    }
    show() { this.shown++; }
    hide() { this.hidden++; this.element.emit('hidden.bs.modal'); }
    dispose() { this.disposed = true; bootstrapInstances.delete(this.element); }
}

const bootstrap = {
    Modal: {
        getOrCreateInstance(element) {
            if (!bootstrapInstances.has(element)) {
                bootstrapInstances.set(element, new MockModal(element));
            }
            return bootstrapInstances.get(element);
        },
        getInstance(element) {
            return bootstrapInstances.get(element) || null;
        }
    }
};

const coachModalRefs = new WeakMap();
const microtasks = [];
const queueMicrotask = fn => microtasks.push(fn);
const flushMicrotasks = () => { while (microtasks.length) microtasks.shift()(); };

function openCoachModal(elementId, dotNetRef) {
    const el = document.getElementById(elementId);
    if (!el) return;

    coachModalRefs.set(el, dotNetRef);

    const modal = bootstrap.Modal.getOrCreateInstance(el);

    if (el.dataset.coachModalBound !== 'true') {
        el.dataset.coachModalBound = 'true';

        el.addEventListener('hidden.bs.modal', () => {
            document.body.classList.remove('coach-open');
            releaseBackgroundInert();

            const ref = coachModalRefs.get(el);
            coachModalRefs.delete(el);
            delete el.dataset.coachModalBound;

            queueMicrotask(() => { bootstrap.Modal.getInstance(el)?.dispose(); });

            if (ref) ref.invokeMethodAsync('OnCoachModalHidden');
        });
    }

    document.body.classList.add('coach-open');
    applyBackgroundInert(el);
    modal.show();
}

function closeCoachModal(elementId) {
    const el = document.getElementById(elementId);
    if (el) bootstrap.Modal.getInstance(el)?.hide();
}

function disposeCoachModal(elementId) {
    const el = document.getElementById(elementId);

    if (el) {
        bootstrap.Modal.getInstance(el)?.dispose();
        delete el.dataset.coachModalBound;
        coachModalRefs.delete(el);
    }

    document.body.classList.remove('coach-open');
    releaseBackgroundInert();
}

function makeRef(name, calls) {
    return {
        name,
        disposed: false,
        invokeMethodAsync(method) {
            if (this.disposed) throw new Error(`${name} is disposed`);
            calls.push(`${name}:${method}`);
            return Promise.resolve();
        }
    };
}

describe('coach modal lifecycle across open, close, and reopen', () => {
    let tree;
    let calls;

    beforeEach(() => {
        bootstrapInstances.clear();
        microtasks.length = 0;
        calls = [];
        tree = buildTree();
    });

    it('shows the modal on the first open', () => {
        openCoachModal('coachWorkspace', makeRef('ref1', calls));

        assert.equal(bootstrap.Modal.getInstance(tree.modal).shown, 1);
        assert.equal(tree.body.classList.contains('coach-open'), true);
    });

    it('reopening after Blazor rebuilt the element shows the NEW element', () => {
        const ref1 = makeRef('ref1', calls);
        openCoachModal('coachWorkspace', ref1);

        // Close: the hidden handler runs, then Blazor removes the element and disposes the ref.
        closeCoachModal('coachWorkspace');
        flushMicrotasks();
        const staleElement = tree.modal;
        const staleInstance = bootstrapInstances.get(staleElement) || null;
        staleElement.remove();
        ref1.disposed = true;

        // Reopen: Blazor renders a brand-new element with the same id.
        const fresh = mountModal(tree.layout);
        openCoachModal('coachWorkspace', makeRef('ref2', calls));

        const instance = bootstrap.Modal.getInstance(fresh);
        assert.ok(instance, 'the new element must get its own Modal instance');
        assert.notEqual(instance, staleInstance, 'a cached instance would target the detached element');
        assert.equal(instance.shown, 1, 'the visible element is the one that is shown');
    });

    it('invokes the current .NET reference on close, never a disposed one', () => {
        const ref1 = makeRef('ref1', calls);
        openCoachModal('coachWorkspace', ref1);
        closeCoachModal('coachWorkspace');
        flushMicrotasks();

        tree.modal.remove();
        ref1.disposed = true;

        const fresh = mountModal(tree.layout);
        openCoachModal('coachWorkspace', makeRef('ref2', calls));

        // Would throw "ref1 is disposed" if the handler had captured the first reference.
        assert.doesNotThrow(() => closeCoachModal('coachWorkspace'));
        assert.deepEqual(calls, ['ref1:OnCoachModalHidden', 'ref2:OnCoachModalHidden']);
        assert.equal(fresh.dataset.coachModalBound, undefined, 'the binding is released on close');
    });

    it('binds the hidden handler once per element, so one close means one callback', () => {
        const ref = makeRef('ref1', calls);
        openCoachModal('coachWorkspace', ref);
        openCoachModal('coachWorkspace', ref); // idempotent re-open of the same element

        closeCoachModal('coachWorkspace');

        assert.deepEqual(calls, ['ref1:OnCoachModalHidden']);
    });

    it('releases the background inert on close so the app stays usable', () => {
        openCoachModal('coachWorkspace', makeRef('ref1', calls));
        assert.equal(tree.content.hasAttribute('inert'), true);

        closeCoachModal('coachWorkspace');

        assert.equal(tree.content.hasAttribute('inert'), false);
        assert.equal(tree.body.classList.contains('coach-open'), false);
    });

    it('disposes the stale Bootstrap instance so the detached element is not pinned', () => {
        openCoachModal('coachWorkspace', makeRef('ref1', calls));
        const instance = bootstrap.Modal.getInstance(tree.modal);

        closeCoachModal('coachWorkspace');
        flushMicrotasks();

        assert.equal(instance.disposed, true);
        assert.equal(bootstrap.Modal.getInstance(tree.modal), null);
    });

    it('disposeCoachModal frees the background even when the element is already gone', () => {
        // CoachDialog.DisposeAsync can run after Blazor removed the element, so
        // hidden.bs.modal never fires. Without this the app is left permanently inert.
        openCoachModal('coachWorkspace', makeRef('ref1', calls));
        tree.modal.remove();

        disposeCoachModal('coachWorkspace');

        assert.equal(tree.content.hasAttribute('inert'), false);
        assert.equal(tree.script.hasAttribute('inert'), false);
        assert.equal(tree.body.classList.contains('coach-open'), false);
    });

    it('survives a full open-close-open-close cycle', () => {
        for (let i = 0; i < 2; i++) {
            const ref = makeRef(`ref${i}`, calls);
            openCoachModal('coachWorkspace', ref);

            const current = document.getElementById('coachWorkspace');
            assert.equal(bootstrap.Modal.getInstance(current).shown, 1, `open ${i} must show`);
            assert.equal(tree.content.hasAttribute('inert'), true, `open ${i} must inert`);

            closeCoachModal('coachWorkspace');
            flushMicrotasks();
            assert.equal(tree.content.hasAttribute('inert'), false, `close ${i} must restore`);

            current.remove();
            ref.disposed = true;
            mountModal(tree.layout);
        }

        assert.deepEqual(calls, ['ref0:OnCoachModalHidden', 'ref1:OnCoachModalHidden']);
    });
});

// ---------------------------------------------------------------- composer Enter

function bindComposerEnter(el, invoke) {
    if (!el || el.dataset.coachEnterBound === 'true') return;

    el.dataset.coachEnterBound = 'true';
    el.addEventListener('keydown', event => {
        if (event.key !== 'Enter' || event.shiftKey) return;
        if (event.isComposing || event.keyCode === 229) return;
        if (el.dataset.enterSends !== 'true') return;

        event.preventDefault();
        invoke();
    });
}

describe('coach composer Enter guard', () => {
    let textarea;
    let sends;

    beforeEach(() => {
        textarea = new MockElement('coach-composer');
        sends = 0;
        bindComposerEnter(textarea, () => { sends++; });
    });

    it('sends on bare Enter and suppresses the newline when Enter sends', () => {
        textarea.dataset.enterSends = 'true';

        const prevented = textarea.dispatchKeyDown({ key: 'Enter', shiftKey: false });

        assert.equal(sends, 1);
        // Without preventDefault the browser inserts a newline AFTER the send, and the
        // following input event writes the just-sent text back into the composer.
        assert.equal(prevented, true, 'the newline must be suppressed');
    });

    it('leaves Shift+Enter as a newline', () => {
        textarea.dataset.enterSends = 'true';

        const prevented = textarea.dispatchKeyDown({ key: 'Enter', shiftKey: true });

        assert.equal(sends, 0);
        assert.equal(prevented, false);
    });

    it('never sends while an IME is composing', () => {
        // Korean input commits with Enter constantly. Sending on those would make the
        // composer unusable in the app's own target language.
        textarea.dataset.enterSends = 'true';

        const prevented = textarea.dispatchKeyDown({ key: 'Enter', isComposing: true });

        assert.equal(sends, 0);
        assert.equal(prevented, false);
    });

    it('treats the legacy 229 keycode as composition too', () => {
        textarea.dataset.enterSends = 'true';

        const prevented = textarea.dispatchKeyDown({ key: 'Enter', keyCode: 229 });

        assert.equal(sends, 0);
        assert.equal(prevented, false);
    });

    it('inserts a newline instead of sending on narrow presentations', () => {
        textarea.dataset.enterSends = 'false';

        const prevented = textarea.dispatchKeyDown({ key: 'Enter' });

        assert.equal(sends, 0, 'narrow presentations use the explicit send button');
        assert.equal(prevented, false, 'Return is the only way to add a newline on a soft keyboard');
    });

    it('follows a live change of data-enter-sends without re-binding', () => {
        // The overlay degrades below 768px in place; the listener must follow it.
        textarea.dataset.enterSends = 'true';
        textarea.dispatchKeyDown({ key: 'Enter' });
        assert.equal(sends, 1);

        textarea.dataset.enterSends = 'false';
        textarea.dispatchKeyDown({ key: 'Enter' });
        assert.equal(sends, 1, 'no further sends once Enter stops sending');
    });

    it('ignores keys other than Enter', () => {
        textarea.dataset.enterSends = 'true';

        assert.equal(textarea.dispatchKeyDown({ key: 'a' }), false);
        assert.equal(sends, 0);
    });

    it('binds only once per element', () => {
        textarea.dataset.enterSends = 'true';
        bindComposerEnter(textarea, () => { sends++; });

        textarea.dispatchKeyDown({ key: 'Enter' });

        assert.equal(sends, 1, 'a double bind would send twice per keystroke');
    });
});

// ================================================================ clipboard

/*
 * copyTextToClipboard mirrors app.js. The async Clipboard API needs a secure context and a
 * permission the user can refuse, so a refusal is a normal outcome, not an error: the function
 * reports whether the text actually landed rather than throwing. The execCommand path is the
 * fallback for older or non-secure contexts.
 */
async function copyTextToClipboard(text, env) {
    if (typeof text !== 'string' || text.length === 0) return false;

    if (env.clipboard && env.isSecureContext) {
        try {
            await env.clipboard.writeText(text);
            return true;
        } catch {
            // Fall through: a refused permission is not a reason to give up entirely.
        }
    }

    try {
        env.appended = true;
        const ok = env.execCommand('copy');
        env.removed = true;
        if (env.activeElement) env.activeElement.focused = true;
        return ok === true;
    } catch {
        return false;
    }
}

function makeEnv(overrides = {}) {
    return {
        isSecureContext: true,
        appended: false,
        removed: false,
        activeElement: null,
        clipboard: {
            written: null,
            writeText(value) {
                this.written = value;
                return Promise.resolve();
            }
        },
        execCommand() { return true; },
        ...overrides
    };
}

describe('copyTextToClipboard', () => {
    it('writes through the clipboard API in a secure context', async () => {
        const env = makeEnv();
        const ok = await copyTextToClipboard('달리다 to run', env);

        assert.equal(ok, true);
        assert.equal(env.clipboard.written, '달리다 to run');
        assert.equal(env.appended, false, 'the fallback must not run when the API worked');
    });

    it('falls back when the clipboard API refuses', async () => {
        const env = makeEnv({
            clipboard: { writeText() { return Promise.reject(new Error('denied')); } }
        });

        const ok = await copyTextToClipboard('text', env);

        assert.equal(ok, true);
        assert.equal(env.appended, true, 'a refusal falls through rather than giving up');
        assert.equal(env.removed, true, 'the scratch element is always cleaned up');
    });

    it('falls back outside a secure context', async () => {
        const env = makeEnv({ isSecureContext: false });
        const ok = await copyTextToClipboard('text', env);

        assert.equal(ok, true);
        assert.equal(env.clipboard.written, null, 'the API is not even attempted');
        assert.equal(env.appended, true);
    });

    it('reports failure rather than throwing when nothing works', async () => {
        const env = makeEnv({
            isSecureContext: false,
            execCommand() { throw new Error('blocked'); }
        });

        const ok = await copyTextToClipboard('text', env);

        assert.equal(ok, false, 'the caller shows neutral feedback, it does not crash');
    });

    it('reports failure when execCommand declines', async () => {
        const env = makeEnv({ isSecureContext: false, execCommand() { return false; } });

        assert.equal(await copyTextToClipboard('text', env), false);
    });

    it('restores focus after the fallback', async () => {
        const active = { focused: false };
        const env = makeEnv({ isSecureContext: false, activeElement: active });

        await copyTextToClipboard('text', env);

        assert.equal(active.focused, true, 'the next keystroke must land where the learner left it');
    });

    it('refuses empty and non-string input without touching the clipboard', async () => {
        const env = makeEnv();

        assert.equal(await copyTextToClipboard('', env), false);
        assert.equal(await copyTextToClipboard(null, env), false);
        assert.equal(await copyTextToClipboard(undefined, env), false);
        assert.equal(env.clipboard.written, null);
        assert.equal(env.appended, false);
    });
});
