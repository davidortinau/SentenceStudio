// Sam overlay JS interop — keyboard shortcuts, viewport resize, focus management.
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

    return window.innerWidth;
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
