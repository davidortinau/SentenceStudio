/*
 * Keeps the conversation following new messages without ever taking the page away from a reader
 * who is in the middle of something.
 *
 * The decisions live in coach-autoscroll-policy.js and are unit tested there. This file is the
 * wiring: find the thing that actually scrolls, watch for the conversation getting taller, and
 * report to Blazor when the jump control should be offered.
 *
 * Why observers rather than "scroll after each render":
 *   - A coach turn does not arrive all at once. Text settles, cards mount, an evidence list
 *     expands, a receipt replaces a proposal. A single post-render scroll runs before most of
 *     that and leaves the reader short of the end.
 *   - ResizeObserver reports the content box growing for any of those reasons, including ones
 *     Blazor never re-rendered for (a web font landing, an image decoding).
 *   - MutationObserver covers the case where height did not change but the children did, which is
 *     what a same-height replacement looks like.
 *
 * Both are debounced onto one animation frame, and a short settle window re-checks afterwards so
 * late-arriving content still gets followed rather than stranding the reader one card short.
 */

import {
    captureFollowState,
    decideOnContentChange,
    decideOnReaderScroll,
    decideOnViewportChange,
    isNearBottom,
    scrollBehavior
} from './coach-autoscroll-policy.js';

/**
 * How long to keep re-checking after a change, in ms. Covers content that settles a frame or two
 * late — an image decoding, a card measuring itself — without following forever.
 */
const SETTLE_WINDOW_MS = 400;

/** Sessions keyed by the id of the conversation element, so a re-mount cannot leak the old one. */
const sessions = new Map();

/**
 * The element that actually scrolls.
 *
 * The conversation is its own scrollport in the overlay, and is `overflow: visible` inside the
 * route's `.activity-content` shell. Walking up from the element and taking the first ancestor
 * that can scroll covers both without either composition having to declare which one it is.
 *
 * Deliberately keyed on the declared overflow rather than on whether the content currently
 * overflows: a conversation with two messages in it does not scroll yet, and resolving to the
 * document at that moment would leave the observer watching the wrong element for the rest of the
 * session — precisely when the messages start arriving.
 */
function findScrollParent(element) {
    let node = element;

    while (node && node !== document.body && node !== document.documentElement) {
        const overflowY = window.getComputedStyle(node).overflowY;

        if (overflowY === 'auto' || overflowY === 'scroll' || overflowY === 'overlay') {
            return node;
        }

        node = node.parentElement;
    }

    return document.scrollingElement || document.documentElement;
}

function readMetrics(scroller) {
    return {
        scrollTop: scroller.scrollTop,
        scrollHeight: scroller.scrollHeight,
        clientHeight: scroller.clientHeight
    };
}

function prefersReducedMotion() {
    return typeof window.matchMedia === 'function'
        && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

function scrollToBottom(scroller) {
    const behavior = scrollBehavior(prefersReducedMotion());

    if (typeof scroller.scrollTo === 'function') {
        scroller.scrollTo({ top: scroller.scrollHeight, behavior });
    } else {
        scroller.scrollTop = scroller.scrollHeight;
    }
}

/**
 * Puts the reader back where a resize moved them from.
 *
 * Never animated, whatever the motion preference says. A resize correction is not a journey the
 * reader asked for; animating it turns a size change into a visible slide, which reads as the
 * conversation scrolling on its own.
 */
function scrollToPosition(scroller, top) {
    if (typeof scroller.scrollTo === 'function') {
        scroller.scrollTo({ top, behavior: 'auto' });
    } else {
        scroller.scrollTop = top;
    }
}

/**
 * Starts following a conversation element.
 *
 * @param {string} elementId The conversation container's id.
 * @param {object} dotNetRef Receives OnJumpAffordanceChanged(bool).
 * @returns {boolean} True when the element was found and is being watched.
 */
export function initCoachAutoScroll(elementId, dotNetRef) {
    disposeCoachAutoScroll(elementId);

    const element = document.getElementById(elementId);
    if (!element) {
        return false;
    }

    const scroller = findScrollParent(element);

    const session = {
        element,
        scroller,
        dotNetRef,
        previousScrollHeight: scroller.scrollHeight,
        previousClientHeight: scroller.clientHeight,
        jumpVisible: false,
        suspended: false,
        // The reader's position, kept current so it is available from BEFORE a resize. See
        // captureFollowState: after a resize the browser has already clamped scrollTop and the
        // position cannot be recovered from the numbers.
        follow: captureFollowState(readMetrics(scroller)),
        bracketed: null,
        frame: 0,
        settleUntil: 0,
        settleTimer: 0,
        disposed: false
    };

    const publishJump = (visible) => {
        if (session.jumpVisible === visible || session.disposed) {
            return;
        }

        session.jumpVisible = visible;

        try {
            session.dotNetRef?.invokeMethodAsync('OnJumpAffordanceChanged', visible);
        } catch {
            // The circuit went away between the observation and the callback. Nothing to report to.
        }
    };

    /**
     * Re-baselines after the scrollport changed size, carrying the reader's intent across.
     *
     * @param {{following:boolean, relativeAnchor:number}} before
     */
    const applyViewportChange = (before) => {
        const metrics = readMetrics(session.scroller);
        const decision = decideOnViewportChange({
            wasFollowing: before.following,
            relativeAnchor: before.relativeAnchor,
            metrics
        });

        scrollToPosition(session.scroller, decision.scrollTop);

        // Both baselines move together: the content height so the resize is not read back as a
        // new message, and the scrollport height so the next evaluation compares like with like.
        session.previousScrollHeight = session.scroller.scrollHeight;
        session.previousClientHeight = session.scroller.clientHeight;
        session.follow = captureFollowState(readMetrics(session.scroller));

        if (decision.hideJump) {
            publishJump(false);
        }
    };

    const evaluate = () => {
        if (session.disposed) {
            return;
        }

        const metrics = readMetrics(session.scroller);

        // A change in the scrollport's own height is a resize, not a message. Handled before the
        // content rules and never through them: shrinking the panel makes the same conversation
        // taller, which the content rules would report as unread messages below.
        if (!session.suspended && metrics.clientHeight !== session.previousClientHeight) {
            applyViewportChange(session.follow);
            return;
        }

        const decision = decideOnContentChange({
            metrics,
            previousScrollHeight: session.previousScrollHeight,
            suspended: session.suspended
        });

        // Recorded before scrolling, so the scroll this decision causes is not read back as a
        // second change on the next frame.
        session.previousScrollHeight = metrics.scrollHeight;
        session.previousClientHeight = metrics.clientHeight;

        if (decision.scroll) {
            scrollToBottom(session.scroller);
            publishJump(false);
            session.follow = captureFollowState(readMetrics(session.scroller));
            return;
        }

        if (decision.showJump) {
            publishJump(true);
        }
    };

    const schedule = () => {
        if (session.disposed || session.frame) {
            return;
        }

        session.frame = window.requestAnimationFrame(() => {
            session.frame = 0;
            evaluate();
        });
    };

    /**
     * Re-checks for a short window after a change, so content that settles late is still followed.
     * The window is extended rather than stacked, so a stream of small mutations produces one
     * trailing check instead of one per mutation.
     */
    const scheduleWithSettle = () => {
        schedule();

        session.settleUntil = Date.now() + SETTLE_WINDOW_MS;

        if (session.settleTimer) {
            return;
        }

        const tick = () => {
            session.settleTimer = 0;

            if (session.disposed) {
                return;
            }

            schedule();

            if (Date.now() < session.settleUntil) {
                session.settleTimer = window.setTimeout(tick, 100);
            }
        };

        session.settleTimer = window.setTimeout(tick, 100);
    };

    session.onScroll = () => {
        if (session.disposed || session.suspended) {
            return;
        }

        const metrics = readMetrics(session.scroller);
        const decision = decideOnReaderScroll({
            metrics,
            jumpVisible: session.jumpVisible
        });

        if (decision.following) {
            // Catching up by hand re-arms following: the baseline moves to here so the next turn
            // is measured against where the reader actually is.
            session.previousScrollHeight = session.scroller.scrollHeight;
        }

        // Kept current on every scroll, because this is the last honest reading of where the
        // reader is before a resize clamps scrollTop out from under them.
        session.follow = captureFollowState(metrics);

        publishJump(decision.showJump);
    };

    session.applyViewportChange = applyViewportChange;
    session.scroller.addEventListener('scroll', session.onScroll, { passive: true });

    if (typeof ResizeObserver === 'function') {
        session.resizeObserver = new ResizeObserver(scheduleWithSettle);
        session.resizeObserver.observe(element);
        // The scrollport is the thing whose height matters, and on the /coach route it is an
        // ancestor of the observed conversation — resizing it does not necessarily resize the
        // conversation, so it is watched in its own right.
        if (session.scroller !== element && session.scroller.nodeType === 1) {
            session.resizeObserver.observe(session.scroller);
        }
    }

    if (typeof MutationObserver === 'function') {
        session.mutationObserver = new MutationObserver(scheduleWithSettle);
        session.mutationObserver.observe(element, {
            childList: true,
            subtree: true,
            characterData: true
        });
    }

    sessions.set(elementId, session);

    // Open at the latest message. A conversation that opens part-way up looks like it failed to
    // load the rest.
    scrollToBottom(scroller);
    session.previousScrollHeight = scroller.scrollHeight;
    session.previousClientHeight = scroller.clientHeight;
    session.follow = { following: true, relativeAnchor: 1 };

    return true;
}

/**
 * Scrolls to the newest message and re-arms following. Invoked by the jump control.
 */
export function scrollCoachToLatest(elementId) {
    const session = sessions.get(elementId);
    if (!session || session.disposed) {
        return false;
    }

    scrollToBottom(session.scroller);
    session.previousScrollHeight = session.scroller.scrollHeight;
    session.follow = { following: true, relativeAnchor: 1 };

    if (session.jumpVisible) {
        session.jumpVisible = false;

        try {
            session.dotNetRef?.invokeMethodAsync('OnJumpAffordanceChanged', false);
        } catch {
            // Circuit gone; the control goes with it.
        }
    }

    return true;
}

/**
 * Suspends following while older messages are inserted above the reader.
 *
 * A prepend grows the content by exactly as much as a long new message does, and from the
 * scrollport's point of view they are indistinguishable. Announcing the boundary is what keeps a
 * page of history from being reported as unread material below.
 */
export function beginCoachHistoryPrepend(elementId) {
    const session = sessions.get(elementId);
    if (!session) {
        return false;
    }

    session.suspended = true;
    return true;
}

/**
 * Resumes following after a prepend, re-baselining against the taller content so the inserted page
 * is never mistaken for new turns.
 */
export function endCoachHistoryPrepend(elementId) {
    const session = sessions.get(elementId);
    if (!session) {
        return false;
    }

    session.suspended = false;
    session.previousScrollHeight = session.scroller.scrollHeight;
    session.previousClientHeight = session.scroller.clientHeight;
    session.follow = captureFollowState(readMetrics(session.scroller));
    return true;
}

/**
 * Opens a bracket around a deliberate change to the panel's size.
 *
 * The automatic path in `evaluate` already catches a scrollport that changed height, and covers
 * the cases nothing could bracket — rotation, a soft keyboard, a dragged window edge. This exists
 * for the case that CAN be bracketed: the learner pressing compact, expand, or full screen. Taking
 * the reading before the class is applied is strictly better than inferring it afterwards, and
 * suspending across the transition keeps the intermediate frames — a re-wrapped conversation that
 * is briefly the wrong height — from being read as messages arriving.
 *
 * Always pair with {@link endCoachViewportChange}, including on failure: a bracket left open stops
 * the conversation following for the rest of the session.
 */
export function beginCoachViewportChange(elementId) {
    const session = sessions.get(elementId);
    if (!session || session.disposed) {
        return false;
    }

    // Re-read rather than trusting the running value: a scroll event may not have fired since the
    // last programmatic scroll, and this is the reading the correction is built on.
    session.bracketed = captureFollowState(readMetrics(session.scroller));
    session.follow = session.bracketed;
    session.suspended = true;
    return true;
}

/**
 * Closes the bracket and puts the reader back where they were, in the new size.
 */
export function endCoachViewportChange(elementId) {
    const session = sessions.get(elementId);
    if (!session || session.disposed) {
        return false;
    }

    const before = session.bracketed;
    session.bracketed = null;
    session.suspended = false;

    if (!before) {
        // begin never ran — re-baseline rather than correcting against a reading we do not have.
        session.previousScrollHeight = session.scroller.scrollHeight;
        session.previousClientHeight = session.scroller.clientHeight;
        session.follow = captureFollowState(readMetrics(session.scroller));
        return true;
    }

    // After layout, not during it: the new size is applied by the render that just completed, but
    // the scrollport's metrics are only final once the browser has laid it out.
    const apply = () => {
        if (!session.disposed) {
            session.applyViewportChange(before);
        }
    };

    if (typeof window.requestAnimationFrame === 'function') {
        window.requestAnimationFrame(apply);
    } else {
        apply();
    }

    return true;
}

/** True when the reader is at the bottom of the conversation. Exposed for tests and diagnostics. */
export function isCoachConversationAtBottom(elementId) {
    const session = sessions.get(elementId);
    if (!session) {
        return false;
    }

    return isNearBottom(readMetrics(session.scroller));
}

export function disposeCoachAutoScroll(elementId) {
    const session = sessions.get(elementId);
    if (!session) {
        return;
    }

    session.disposed = true;

    if (session.frame) {
        window.cancelAnimationFrame(session.frame);
    }

    if (session.settleTimer) {
        window.clearTimeout(session.settleTimer);
    }

    session.resizeObserver?.disconnect();
    session.mutationObserver?.disconnect();
    session.scroller?.removeEventListener('scroll', session.onScroll);
    session.dotNetRef = null;

    sessions.delete(elementId);
}
