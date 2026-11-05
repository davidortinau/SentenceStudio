// Sam overlay JS interop — keyboard shortcuts, viewport resize, focus management, and the
// floating button's hide on scroll.
// Loaded as an ES module from SamOverlayHost.razor.

let _dotNetRef = null;
let _resizeHandler = null;
let _keyHandler = null;

/**
 * The scroll and overflow state captured before entering fullscreen, so the underlying page
 * returns to exactly where the learner left it after the panel is put back to its earlier size.
 * Null when nothing is currently locked.
 *
 * @type {null | {
 *   scrollingEl: Element | null,
 *   docTop: number,
 *   docLeft: number,
 *   htmlPrevOverflow: string,
 *   bodyPrevOverflow: string,
 *   shellEl: Element | null,
 *   shellTop: number,
 *   shellLeft: number,
 *   shellPrevOverflow: string,
 * }}
 */
let _scrollLock = null;

/**
 * Marks a subtree that answers Escape itself.
 *
 * The overlay's Escape listener is on `document`, and so is Blazor's delegated one. Two listeners
 * on the same node run in registration order and `stopPropagation` in one does not stop the other,
 * so "the inner surface stops the event" is not something the inner surface can actually guarantee.
 * Reading the press's own ancestry instead makes the decision at dispatch time and independent of
 * which handler was attached first: if the press came from inside a surface that owns Escape, the
 * overlay never hears about it.
 */
const ESCAPE_OWNER_SELECTOR = '[data-sam-escape-owner]';

/** The selector for the app shell container that should be made inert when Sam is modal. */
const APP_SHELL_SELECTOR = '.main-content';

/** True when the press originated inside a surface that answers Escape itself. */
export function escapeIsOwnedByInnerSurface(target) {
    return typeof target?.closest === 'function'
        && target.closest(ESCAPE_OWNER_SELECTOR) !== null;
}

export function initSamOverlay(dotNetRef) {
    _dotNetRef = dotNetRef;

    _resizeHandler = () => {
        if (_dotNetRef) {
            _dotNetRef.invokeMethodAsync('OnViewportChanged', window.innerWidth);
        }
    };
    window.addEventListener('resize', _resizeHandler);

    _keyHandler = (e) => {
        // Escape collapses the panel
        if (e.key !== 'Escape' || !_dotNetRef) {
            return;
        }

        // An inner surface owns this press. Standing down here rather than in .NET keeps one press
        // to one layer: the surface's own handler closes it, and the overlay is not asked to.
        if (escapeIsOwnedByInnerSurface(e.target)) {
            return;
        }

        _dotNetRef.invokeMethodAsync('OnEscapePressed');
    };
    document.addEventListener('keydown', _keyHandler);

    attachSamEntryAutoHide();

    return window.innerWidth;
}

// ------------------------------------------------------------- floating button: hide on scroll
//
// docs/sam-entry.md. The rule is the shared one in
// src/SentenceStudio.Native/Sam/NativeSamEntryAutoHide.cs, written again here because the web
// runs it in the browser; a change to one must change the other and the doc.

/** CSS pixels a scroll must travel in one direction before the button moves. */
export const SAM_ENTRY_THRESHOLD = 24;

/** Set on <html> while the floating button is away. Blazor never renders that element. */
export const SAM_ENTRY_AWAY_ATTRIBUTE = 'data-sam-entry-away';

const SAM_FAB_SELECTOR = '.sam-fab';

/** Fields that bring up the on-screen keyboard. */
const TEXT_FIELD_SELECTOR = [
    'textarea',
    'input:not([type])',
    'input[type="text"]',
    'input[type="search"]',
    'input[type="email"]',
    'input[type="password"]',
    'input[type="number"]',
    'input[type="tel"]',
    'input[type="url"]',
    '[contenteditable=""]',
    '[contenteditable="true"]'
].join(', ');

/**
 * Whether the floating button is shown. The same state and steps as NativeSamEntryAutoHide.
 *
 * The web has no keyboard event, so the keyboard flag is "a text field has focus on a touch
 * device". It cannot tell whether a screen reader runs either, so it has no screen-reader flag:
 * a hidden button stays in the accessibility tree and comes back when it receives focus.
 */
export class SamEntryAutoHide {
    #travel = 0;
    #scrolledAway = false;
    #keyboardVisible = false;

    /** True when the floating button should be on screen. */
    get isShown() {
        return !this.#keyboardVisible && !this.#scrolledAway;
    }

    /**
     * One scroll step. `delta` is positive when the content moved toward its end. A page that
     * cannot scroll is at both its start and its end.
     * @returns {boolean} True when `isShown` changed.
     */
    onScroll(delta, atStart, atEnd) {
        const before = this.isShown;
        if (atStart || atEnd) {
            // The end has room left for the button, so it never covers the last item there.
            this.#scrolledAway = false;
            this.#travel = 0;
        } else if (Number.isFinite(delta) && delta !== 0) {
            if (Math.sign(delta) !== Math.sign(this.#travel)) {
                this.#travel = 0;
            }
            this.#travel += delta;
            if (this.#travel >= SAM_ENTRY_THRESHOLD) {
                this.#scrolledAway = true;
            } else if (this.#travel <= -SAM_ENTRY_THRESHOLD) {
                this.#scrolledAway = false;
            }
        }
        return before !== this.isShown;
    }

    /** @returns {boolean} True when `isShown` changed. */
    setKeyboardVisible(visible) {
        const before = this.isShown;
        this.#keyboardVisible = !!visible;
        return before !== this.isShown;
    }

    /**
     * A new page is showing, or the button received focus: it is shown. The keyboard state
     * belongs to the device and is kept.
     * @returns {boolean} True when `isShown` changed.
     */
    reset() {
        const before = this.isShown;
        this.#scrolledAway = false;
        this.#travel = 0;
        return before !== this.isShown;
    }
}

let _autoHide = null;
let _scrollHandler = null;
let _focusInHandler = null;
let _focusOutHandler = null;
let _lastScroller = null;
let _lastScrollTop = 0;

/**
 * The element that scrolls the page: the app shell's main content, or the document when the
 * shell does not scroll (both are watched, so either layout works). Any other scroller, such as
 * a horizontal strip or Sam's own transcript, is not the page and does not move the button.
 */
function pageScrollerFor(target) {
    if (typeof document === 'undefined') return null;

    if (target === document || target === document.documentElement || target === document.body) {
        return document.scrollingElement || document.documentElement;
    }

    const shell = document.querySelector(APP_SHELL_SELECTOR);
    return target && target === shell ? shell : null;
}

function applySamEntryState() {
    if (!_autoHide || typeof document === 'undefined') return;
    const root = document.documentElement;
    if (!root) return;

    if (_autoHide.isShown) {
        root.removeAttribute(SAM_ENTRY_AWAY_ATTRIBUTE);
    } else {
        root.setAttribute(SAM_ENTRY_AWAY_ATTRIBUTE, '');
    }
}

function isTouchDevice() {
    return typeof window !== 'undefined'
        && typeof window.matchMedia === 'function'
        && window.matchMedia('(pointer: coarse)').matches;
}

function isTextField(el) {
    return !!el && typeof el.matches === 'function' && el.matches(TEXT_FIELD_SELECTOR);
}

/** Starts watching the page scroll and text-field focus. Safe to call twice. */
export function attachSamEntryAutoHide() {
    detachSamEntryAutoHide();
    if (typeof document === 'undefined') return;

    _autoHide = new SamEntryAutoHide();

    _scrollHandler = (e) => {
        const scroller = pageScrollerFor(e.target);
        if (!scroller) return;

        // A horizontal scroll moves nothing vertically: a zero step, which the rule ignores
        // between the top and the end.
        const top = scroller.scrollTop;
        const delta = scroller === _lastScroller ? top - _lastScrollTop : 0;
        _lastScroller = scroller;
        _lastScrollTop = top;

        const atStart = top <= 0;
        const atEnd = top + scroller.clientHeight >= scroller.scrollHeight - 1;
        if (_autoHide.onScroll(delta, atStart, atEnd)) {
            applySamEntryState();
        }
    };
    // Scroll does not bubble; capturing on the document hears the shell's scroll and the
    // document's own.
    document.addEventListener('scroll', _scrollHandler, { capture: true, passive: true });

    _focusInHandler = (e) => {
        const target = e.target;
        const before = _autoHide.isShown;
        if (typeof target?.closest === 'function' && target.closest(SAM_FAB_SELECTOR)) {
            // A hidden button stays reachable by keyboard and screen reader; arriving brings it
            // back. Focus has left any text field, so the keyboard is going away too.
            _autoHide.setKeyboardVisible(false);
            _autoHide.reset();
        } else {
            // Focus anywhere else says whether the keyboard is up: only for a text field, and
            // only on a touch device.
            _autoHide.setKeyboardVisible(isTextField(target) && isTouchDevice());
        }
        if (before !== _autoHide.isShown) applySamEntryState();
    };
    document.addEventListener('focusin', _focusInHandler);

    _focusOutHandler = (e) => {
        // Leaving a field for nothing (a tap on the page) puts the keyboard away. Moving to
        // another field fires that field's focusin straight after, in the same task, so the
        // button never shows in between.
        if (isTextField(e.target) && _autoHide.setKeyboardVisible(false)) {
            applySamEntryState();
        }
    };
    document.addEventListener('focusout', _focusOutHandler);

    resetSamEntry();
}

/** A new page is showing: the button starts shown. */
export function resetSamEntry() {
    if (!_autoHide) return;

    const scroller = typeof document !== 'undefined'
        ? (document.querySelector(APP_SHELL_SELECTOR) || document.scrollingElement)
        : null;
    _lastScroller = scroller;
    _lastScrollTop = scroller ? scroller.scrollTop : 0;

    _autoHide.reset();
    // The keyboard is the device's, so it is read rather than cleared: a field the new page
    // focused for the learner keeps the button away.
    const active = typeof document !== 'undefined' ? document.activeElement : null;
    _autoHide.setKeyboardVisible(isTextField(active) && isTouchDevice());
    applySamEntryState();
}

/** Stops watching and leaves the button shown. Safe to call when nothing is attached. */
export function detachSamEntryAutoHide() {
    if (typeof document !== 'undefined') {
        if (_scrollHandler) document.removeEventListener('scroll', _scrollHandler, { capture: true });
        if (_focusInHandler) document.removeEventListener('focusin', _focusInHandler);
        if (_focusOutHandler) document.removeEventListener('focusout', _focusOutHandler);
        document.documentElement?.removeAttribute?.(SAM_ENTRY_AWAY_ATTRIBUTE);
    }
    _scrollHandler = null;
    _focusInHandler = null;
    _focusOutHandler = null;
    _autoHide = null;
    _lastScroller = null;
    _lastScrollTop = 0;
}

/**
 * Sets or removes the inert attribute on the app shell content when Sam is modal on mobile.
 * @param {boolean} modal - Whether the panel is in mobile-modal state.
 */
export function setAppShellInert(modal) {
    const shell = document.querySelector(APP_SHELL_SELECTOR);
    if (!shell) return;
    if (modal) {
        shell.setAttribute('inert', '');
    } else {
        shell.removeAttribute('inert');
    }
}

/**
 * Moves keyboard focus to an element by id, without letting the browser scroll the document to
 * bring it into view.
 *
 * The fullscreen panel is <c>position: fixed; inset: 0</c>, but iOS WKWebView scrolls
 * <c>document.scrollingElement</c> anyway when <c>focus()</c> is called on a nested element, and
 * the scroll then displaces the entire fixed panel upward by that many pixels — the header and
 * its controls end up behind the Dynamic Island. Passing <c>{ preventScroll: true }</c> is what
 * asks the engine not to do that. The <c>try/catch</c> is defence in depth: an old engine that
 * does not support the option would receive an object rather than nothing and could throw,
 * so the fallback restores the pre-existing behaviour rather than dropping focus.
 *
 * @param {string} elementId - The id of the element to focus.
 * @param {{ preventScroll?: boolean }} [options] - Focus options. Defaults to preventing scroll,
 *     because every caller in the Sam overlay wants focus without moving the underlying page.
 */
export function focusElement(elementId, options) {
    const el = document.getElementById(elementId);
    if (!el || typeof el.focus !== 'function') return;

    const preventScroll = options?.preventScroll !== false;

    try {
        el.focus({ preventScroll });
    } catch {
        // A legacy engine that rejects the options object — fall back to the bare call so focus
        // still lands. This is the only path that can visibly scroll the panel, and any engine
        // taking it is not iOS WKWebView, which is the surface the option exists for.
        el.focus();
    }
}

/**
 * Locks the underlying page's scroll before the panel enters fullscreen, capturing the exact
 * scroll positions of both the document (which iOS Safari uses as the fixed-position frame of
 * reference) and the app shell (which owns the dashboard's actual scroll). Idempotent: a second
 * call while already locked leaves the captured state untouched, so a repeated fullscreen
 * transition cannot lose the original scroll position.
 *
 * The document scroll is pinned to zero because that is what makes <c>position: fixed; inset: 0</c>
 * paint at the true viewport origin on iOS WKWebView. Without it, any pre-existing document scroll
 * combines with the focus-induced scroll and puts the panel header behind the status bar.
 */
export function enterFullscreenScrollLock() {
    if (_scrollLock) return;

    const doc = typeof document !== 'undefined'
        ? (document.scrollingElement || document.documentElement)
        : null;
    const html = typeof document !== 'undefined' ? document.documentElement : null;
    const body = typeof document !== 'undefined' ? document.body : null;
    const shell = typeof document !== 'undefined'
        ? document.querySelector(APP_SHELL_SELECTOR)
        : null;

    _scrollLock = {
        scrollingEl: doc,
        docTop: doc ? doc.scrollTop : 0,
        docLeft: doc ? doc.scrollLeft : 0,
        htmlPrevOverflow: html?.style?.overflow ?? '',
        bodyPrevOverflow: body?.style?.overflow ?? '',
        shellEl: shell,
        shellTop: shell ? shell.scrollTop : 0,
        shellLeft: shell ? shell.scrollLeft : 0,
        shellPrevOverflow: shell?.style?.overflow ?? ''
    };

    if (html?.style) html.style.overflow = 'hidden';
    if (body?.style) body.style.overflow = 'hidden';
    if (shell?.style) shell.style.overflow = 'hidden';

    // Pin the document to the origin: fixed positioning is measured against the document's
    // scroll on iOS WKWebView, so a non-zero scrollTop pushes an inset:0 panel off the top.
    if (doc) {
        doc.scrollTop = 0;
        doc.scrollLeft = 0;
    }
}

/**
 * Releases the fullscreen scroll lock and restores the exact scroll positions that were live
 * before entering fullscreen. Idempotent: safe to call when nothing is locked (no capture ->
 * no-op) so any teardown path — Restore, Collapse, dispose — can call it unconditionally.
 */
export function exitFullscreenScrollLock() {
    const state = _scrollLock;
    if (!state) return;
    _scrollLock = null;

    const html = typeof document !== 'undefined' ? document.documentElement : null;
    const body = typeof document !== 'undefined' ? document.body : null;

    // Restore overflow FIRST — a locked container cannot receive a scrollTop, so the assignment
    // below would be silently clamped to zero if the style change came second.
    if (html?.style) html.style.overflow = state.htmlPrevOverflow;
    if (body?.style) body.style.overflow = state.bodyPrevOverflow;
    if (state.shellEl?.style) state.shellEl.style.overflow = state.shellPrevOverflow;

    if (state.scrollingEl) {
        state.scrollingEl.scrollTop = state.docTop;
        state.scrollingEl.scrollLeft = state.docLeft;
    }
    if (state.shellEl) {
        state.shellEl.scrollTop = state.shellTop;
        state.shellEl.scrollLeft = state.shellLeft;
    }
}

export function disposeSamOverlay() {
    // Always release the scroll lock and the inert flag on teardown, in that order: a lock left
    // in place would keep the dashboard non-scrollable for the rest of the session, and an inert
    // shell left in place would deny keyboard input to every remaining surface. Both are safe to
    // call when nothing is set.
    exitFullscreenScrollLock();
    setAppShellInert(false);
    detachSamEntryAutoHide();

    if (_resizeHandler) {
        window.removeEventListener('resize', _resizeHandler);
        _resizeHandler = null;
    }
    if (_keyHandler) {
        document.removeEventListener('keydown', _keyHandler);
        _keyHandler = null;
    }
    _dotNetRef = null;
}
