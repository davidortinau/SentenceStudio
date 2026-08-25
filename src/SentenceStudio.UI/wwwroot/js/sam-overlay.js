// Sam overlay JS interop — keyboard shortcuts, viewport resize, focus management.
// Loaded as an ES module from SamOverlayHost.razor.

let _dotNetRef = null;
let _resizeHandler = null;
let _keyHandler = null;

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

export function focusElement(elementId) {
    const el = document.getElementById(elementId);
    if (el) {
        el.focus();
    }
}

export function disposeSamOverlay() {
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
