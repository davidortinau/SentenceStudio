// photo-viewer-gestures.js
// Dependency-free Pointer Events zoom/pan for the fullscreen photo viewer.
// ES module — lifecycle-safe Blazor interop: attach while open, detach on close/dispose/image-change.
"use strict";

import {
    clampScale,
    clampTranslation,
    distance,
    midpoint,
    anchoredZoomTranslation,
    fitDimensions
} from './photo-viewer-math.js';

const MIN_SCALE = 1;
const MAX_SCALE = 4;
const DOUBLE_TAP_THRESHOLD_MS = 300;
const DOUBLE_TAP_DISTANCE_PX = 30;
const DOUBLE_TAP_ZOOM = 2.5;
const WHEEL_ZOOM_FACTOR = 0.002;

/**
 * Internal state for one attached viewer instance.
 * @typedef {Object} ViewerState
 */

/** @type {ViewerState|null} */
let state = null;

/**
 * Attach gesture handling to the fullscreen image element.
 * Call this after the overlay is rendered (from Blazor OnAfterRenderAsync).
 *
 * @param {string} imageId - DOM id of the <img> element
 * @param {string} overlayId - DOM id of the overlay container (for resize observation)
 * @param {Object} [options] - Optional configuration
 * @param {boolean} [options.diagnostics=false] - Emit data-debug-* attributes for automation
 * @returns {boolean} true if attached successfully
 */
export function attach(imageId, overlayId, options) {
    // Detach any previous instance (defensive)
    detach();

    const img = document.getElementById(imageId);
    const overlay = document.getElementById(overlayId);
    if (!img || !overlay) return false;

    const diagnostics = !!(options && options.diagnostics);
    state = createState(img, overlay, diagnostics);
    bindEvents(state);
    applyTransform(state);
    if (state.diagnostics) updateDebugAttributes(state);
    return true;
}

/**
 * Detach gesture handling and clean up.
 * Safe to call multiple times or when not attached.
 */
export function detach() {
    if (!state) return;
    unbindEvents(state);
    resetTransform(state);
    state = null;
}

/**
 * Reset zoom/pan to fit (1x, centered). Called on image change.
 */
export function reset() {
    if (!state) return;
    state.scale = 1;
    state.tx = 0;
    state.ty = 0;
    state.resetGeneration++;
    state.rafPending = false;
    applyTransform(state);
    if (state.diagnostics) updateDebugAttributes(state);
}

/**
 * Get current viewer state for DEBUG observability.
 * @returns {{scale: number, tx: number, ty: number, pointerCount: number, resetGeneration: number}|null}
 */
export function getState() {
    if (!state) return null;
    return {
        scale: state.scale,
        tx: state.tx,
        ty: state.ty,
        pointerCount: state.pointers.size,
        resetGeneration: state.resetGeneration
    };
}

// --- Internal implementation ---

function createState(img, overlay, diagnostics) {
    return {
        img,
        overlay,
        diagnostics,
        scale: 1,
        tx: 0,
        ty: 0,
        pointers: new Map(), // pointerId -> {x, y}
        pinchStartDist: 0,
        pinchStartScale: 1,
        panStartTx: 0,
        panStartTy: 0,
        panStartPointerX: 0,
        panStartPointerY: 0,
        isPanning: false,
        lastTapTime: 0,
        lastTapX: 0,
        lastTapY: 0,
        resetGeneration: 0,
        rafPending: false,
        resizeObserver: null,
        // Bound event handlers for cleanup
        handlers: {}
    };
}

function bindEvents(s) {
    const img = s.img;
    const overlay = s.overlay;

    // Prevent default touch behavior ONLY on the image surface
    img.style.touchAction = 'none';

    if (s.diagnostics) {
        img.setAttribute('data-testid', 'photo-viewer-image');
    }

    s.handlers.pointerdown = (e) => onPointerDown(s, e);
    s.handlers.pointermove = (e) => onPointerMove(s, e);
    s.handlers.pointerup = (e) => onPointerUp(s, e);
    s.handlers.pointercancel = (e) => onPointerCancel(s, e);
    s.handlers.lostpointercapture = (e) => onLostPointerCapture(s, e);
    s.handlers.wheel = (e) => onWheel(s, e);
    s.handlers.resize = () => onResize(s);
    // Prevent click events on the image from bubbling to the backdrop dismiss
    // handler. Pointer Events + touch-action:none suppresses synthetic clicks on
    // most platforms, but some desktop browsers still fire click after a
    // non-moving pointerdown/up sequence. This DOM-level guard is the narrowest
    // fix that never interferes with double-tap (handled via pointerdown timing).
    s.handlers.click = (e) => { e.stopPropagation(); };

    img.addEventListener('pointerdown', s.handlers.pointerdown);
    img.addEventListener('pointermove', s.handlers.pointermove);
    img.addEventListener('pointerup', s.handlers.pointerup);
    img.addEventListener('pointercancel', s.handlers.pointercancel);
    img.addEventListener('lostpointercapture', s.handlers.lostpointercapture);
    img.addEventListener('wheel', s.handlers.wheel, { passive: false });
    img.addEventListener('click', s.handlers.click);

    // Observe container resize (orientation changes, window resize)
    if (typeof ResizeObserver !== 'undefined') {
        s.resizeObserver = new ResizeObserver(s.handlers.resize);
        s.resizeObserver.observe(overlay);
    } else {
        window.addEventListener('resize', s.handlers.resize);
    }

    // Set initial debug attributes
    if (s.diagnostics) {
        overlay.setAttribute('data-testid', 'photo-viewer-overlay');
    }
}

function unbindEvents(s) {
    const img = s.img;

    img.removeEventListener('pointerdown', s.handlers.pointerdown);
    img.removeEventListener('pointermove', s.handlers.pointermove);
    img.removeEventListener('pointerup', s.handlers.pointerup);
    img.removeEventListener('pointercancel', s.handlers.pointercancel);
    img.removeEventListener('lostpointercapture', s.handlers.lostpointercapture);
    img.removeEventListener('wheel', s.handlers.wheel);
    img.removeEventListener('click', s.handlers.click);

    if (s.resizeObserver) {
        s.resizeObserver.disconnect();
        s.resizeObserver = null;
    } else {
        window.removeEventListener('resize', s.handlers.resize);
    }

    img.style.touchAction = '';
    if (s.diagnostics) {
        img.removeAttribute('data-testid');
        s.overlay.removeAttribute('data-testid');
        s.overlay.removeAttribute('data-debug-scale');
        s.overlay.removeAttribute('data-debug-translate-x');
        s.overlay.removeAttribute('data-debug-translate-y');
        s.overlay.removeAttribute('data-debug-pointers');
        s.overlay.removeAttribute('data-debug-reset');
    }
}

function resetTransform(s) {
    s.img.style.transform = '';
    s.img.style.transformOrigin = '';
}

function applyTransform(s) {
    // Using translate + scale from center of container
    s.img.style.transformOrigin = 'center center';
    s.img.style.transform = `translate(${s.tx}px, ${s.ty}px) scale(${s.scale})`;
}

function updateDebugAttributes(s) {
    const o = s.overlay;
    o.setAttribute('data-debug-scale', s.scale.toFixed(3));
    o.setAttribute('data-debug-translate-x', s.tx.toFixed(1));
    o.setAttribute('data-debug-translate-y', s.ty.toFixed(1));
    o.setAttribute('data-debug-pointers', s.pointers.size.toString());
    o.setAttribute('data-debug-reset', s.resetGeneration.toString());
}

function scheduleRender(s) {
    if (s.rafPending) return;
    s.rafPending = true;
    requestAnimationFrame(() => {
        s.rafPending = false;
        applyTransform(s);
        if (s.diagnostics) updateDebugAttributes(s);
    });
}

function getContainerRect(s) {
    return s.overlay.getBoundingClientRect();
}

function getImageFitSize(s) {
    const img = s.img;
    const rect = getContainerRect(s);
    return fitDimensions(img.naturalWidth, img.naturalHeight, rect.width, rect.height);
}

function clampCurrentTranslation(s) {
    const fit = getImageFitSize(s);
    const rect = getContainerRect(s);
    const clamped = clampTranslation(s.tx, s.ty, s.scale, fit.width, fit.height, rect.width, rect.height);
    s.tx = clamped.tx;
    s.ty = clamped.ty;
}

// --- Pointer event handlers ---

function onPointerDown(s, e) {
    // Capture this pointer
    try {
        s.img.setPointerCapture(e.pointerId);
    } catch (_) {
        // Some browsers may reject capture on touch
    }
    s.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });

    if (s.pointers.size === 1) {
        // Check for double-tap
        const now = Date.now();
        const dt = now - s.lastTapTime;
        const dd = distance(e.clientX, e.clientY, s.lastTapX, s.lastTapY);

        if (dt < DOUBLE_TAP_THRESHOLD_MS && dd < DOUBLE_TAP_DISTANCE_PX) {
            // Double-tap: toggle between fit and zoomed
            handleDoubleTap(s, e.clientX, e.clientY);
            s.lastTapTime = 0; // Prevent triple-tap
            return;
        }

        s.lastTapTime = now;
        s.lastTapX = e.clientX;
        s.lastTapY = e.clientY;

        // Start pan (only effective above 1x)
        s.isPanning = true;
        s.panStartTx = s.tx;
        s.panStartTy = s.ty;
        s.panStartPointerX = e.clientX;
        s.panStartPointerY = e.clientY;
    } else if (s.pointers.size === 2) {
        // Start pinch
        s.isPanning = false;
        const pts = Array.from(s.pointers.values());
        s.pinchStartDist = distance(pts[0].x, pts[0].y, pts[1].x, pts[1].y);
        s.pinchStartScale = s.scale;
    }

    if (s.diagnostics) updateDebugAttributes(s);
    e.preventDefault();
}

function onPointerMove(s, e) {
    if (!s.pointers.has(e.pointerId)) return;
    s.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });

    if (s.pointers.size === 2) {
        // Pinch zoom
        const pts = Array.from(s.pointers.values());
        const currentDist = distance(pts[0].x, pts[0].y, pts[1].x, pts[1].y);
        if (s.pinchStartDist > 0) {
            const ratio = currentDist / s.pinchStartDist;
            const newScale = clampScale(s.pinchStartScale * ratio, MIN_SCALE, MAX_SCALE);

            // Anchor at pinch midpoint
            const mid = midpoint(pts[0].x, pts[0].y, pts[1].x, pts[1].y);
            const rect = getContainerRect(s);
            const anchorX = mid.x - rect.left;
            const anchorY = mid.y - rect.top;

            // Calculate anchored translation from the pinch start state
            const prevScaleForAnchor = s.scale;
            const anchored = anchoredZoomTranslation(
                anchorX, anchorY,
                prevScaleForAnchor, newScale,
                s.tx, s.ty,
                rect.width, rect.height
            );

            s.scale = newScale;
            s.tx = anchored.tx;
            s.ty = anchored.ty;

            clampCurrentTranslation(s);
            scheduleRender(s);
        }
    } else if (s.pointers.size === 1 && s.isPanning && s.scale > 1) {
        // Pan (only above 1x)
        const dx = e.clientX - s.panStartPointerX;
        const dy = e.clientY - s.panStartPointerY;
        s.tx = s.panStartTx + dx;
        s.ty = s.panStartTy + dy;

        clampCurrentTranslation(s);
        scheduleRender(s);
    }

    e.preventDefault();
}

function onPointerUp(s, e) {
    s.pointers.delete(e.pointerId);
    try {
        s.img.releasePointerCapture(e.pointerId);
    } catch (_) { }

    if (s.pointers.size === 1) {
        // Transition from pinch to pan: reset pan start to current single pointer
        const remaining = Array.from(s.pointers.values())[0];
        s.isPanning = true;
        s.panStartTx = s.tx;
        s.panStartTy = s.ty;
        s.panStartPointerX = remaining.x;
        s.panStartPointerY = remaining.y;
    } else if (s.pointers.size === 0) {
        s.isPanning = false;
    }

    if (s.diagnostics) updateDebugAttributes(s);
    e.preventDefault();
}

function onPointerCancel(s, e) {
    s.pointers.delete(e.pointerId);
    if (s.pointers.size === 0) {
        s.isPanning = false;
        // Snap back if scale ended below 1 (shouldn't normally happen due to clamping)
        if (s.scale < MIN_SCALE) {
            s.scale = MIN_SCALE;
            s.tx = 0;
            s.ty = 0;
            scheduleRender(s);
        }
    }
    if (s.diagnostics) updateDebugAttributes(s);
}

function onLostPointerCapture(s, e) {
    // Treat as pointer cancel for safety
    if (s.pointers.has(e.pointerId)) {
        s.pointers.delete(e.pointerId);
        if (s.pointers.size === 0) {
            s.isPanning = false;
        }
        if (s.diagnostics) updateDebugAttributes(s);
    }
}

function onWheel(s, e) {
    e.preventDefault();

    // deltaY: positive = scroll down = zoom out; negative = scroll up = zoom in
    const zoomDelta = -e.deltaY * WHEEL_ZOOM_FACTOR;
    const newScale = clampScale(s.scale * (1 + zoomDelta), MIN_SCALE, MAX_SCALE);

    if (newScale === s.scale) return;

    // Anchor at cursor position relative to container
    const rect = getContainerRect(s);
    const anchorX = e.clientX - rect.left;
    const anchorY = e.clientY - rect.top;

    const anchored = anchoredZoomTranslation(
        anchorX, anchorY,
        s.scale, newScale,
        s.tx, s.ty,
        rect.width, rect.height
    );

    s.scale = newScale;
    s.tx = anchored.tx;
    s.ty = anchored.ty;

    clampCurrentTranslation(s);
    scheduleRender(s);
}

function onResize(s) {
    // On viewport/orientation change, re-clamp translation to avoid overscroll
    clampCurrentTranslation(s);
    scheduleRender(s);
}

function handleDoubleTap(s, clientX, clientY) {
    const rect = getContainerRect(s);
    const anchorX = clientX - rect.left;
    const anchorY = clientY - rect.top;

    if (s.scale > 1.05) {
        // Currently zoomed: reset to fit
        s.scale = 1;
        s.tx = 0;
        s.ty = 0;
    } else {
        // Currently at fit: zoom to useful level, anchored at tap point
        const newScale = DOUBLE_TAP_ZOOM;
        const anchored = anchoredZoomTranslation(
            anchorX, anchorY,
            s.scale, newScale,
            s.tx, s.ty,
            rect.width, rect.height
        );
        s.scale = newScale;
        s.tx = anchored.tx;
        s.ty = anchored.ty;
        clampCurrentTranslation(s);
    }

    applyTransform(s);
    if (s.diagnostics) updateDebugAttributes(s);
}
