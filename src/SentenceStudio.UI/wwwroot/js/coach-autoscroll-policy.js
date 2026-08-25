/*
 * Pure decisions for the conversation's follow-the-latest-message behaviour.
 *
 * Kept free of the DOM so the rules can be tested directly under `node --test` rather than
 * through a hand-written element double — the same split the photo viewer already uses between
 * photo-viewer-math.js and photo-viewer-modal.js.
 *
 * The rules, stated once here so the wiring in coach-autoscroll.js does not have to restate them:
 *
 *   1. A reader sitting at the bottom of the conversation is following it, so new turns should
 *      keep up with them.
 *   2. A reader who has scrolled up is reading something. Moving them is taking the page away
 *      mid-sentence, and no amount of "but the new message is important" makes that acceptable.
 *   3. A reader at the bottom when a *substantial* block arrives — a long answer, a proposal card,
 *      a receipt — would have the beginning of that block pushed off the top by a scroll to the
 *      end. Following would mean landing them in the middle of something they never saw start.
 *      So this case is treated like (2): stay put, and offer the jump.
 *
 * In both non-following cases the reader is told there is something below rather than left to
 * discover it, which is what the jump control is for.
 */

/** How close to the bottom still counts as "following the conversation", in CSS pixels. */
export const NEAR_BOTTOM_THRESHOLD_PX = 48;

/**
 * How much of the viewport a single update may fill before it counts as substantial.
 *
 * Below 1.0 because a block that exactly fills the viewport still puts its first line at the very
 * top edge after a scroll to the end, which reads as having missed the start. Three quarters
 * leaves the opening of the new message comfortably in view when we do follow.
 */
export const SUBSTANTIAL_UPDATE_VIEWPORT_RATIO = 0.75;

/**
 * Distance from the bottom of the scrollport, in CSS pixels.
 *
 * @param {{scrollTop:number, scrollHeight:number, clientHeight:number}} metrics
 */
export function distanceFromBottom(metrics) {
    const { scrollTop = 0, scrollHeight = 0, clientHeight = 0 } = metrics ?? {};
    return Math.max(0, scrollHeight - clientHeight - scrollTop);
}

/**
 * True when the reader is close enough to the bottom to be following the conversation.
 *
 * @param {{scrollTop:number, scrollHeight:number, clientHeight:number}} metrics
 * @param {number} [threshold]
 */
export function isNearBottom(metrics, threshold = NEAR_BOTTOM_THRESHOLD_PX) {
    return distanceFromBottom(metrics) <= threshold;
}

/**
 * True when a single growth in content is large enough that following it would move the beginning
 * of the new material off the top of the viewport.
 *
 * A non-growing or shrinking update is never substantial: nothing new arrived to miss the start of.
 *
 * @param {number} growthPx How much taller the content became.
 * @param {number} clientHeight The height of the scrollport.
 * @param {number} [ratio]
 */
export function isSubstantialUpdate(growthPx, clientHeight, ratio = SUBSTANTIAL_UPDATE_VIEWPORT_RATIO) {
    if (!(growthPx > 0) || !(clientHeight > 0)) {
        return false;
    }

    return growthPx > clientHeight * ratio;
}

/**
 * What to do after the conversation's height changed.
 *
 * @param {object} input
 * @param {{scrollTop:number, scrollHeight:number, clientHeight:number}} input.metrics
 *   Metrics read AFTER the change, with scrollTop still where the reader left it.
 * @param {number} input.previousScrollHeight Content height before the change.
 * @param {boolean} [input.suspended] True while history is being prepended, when every offset
 *   below the insertion point moves for reasons that have nothing to do with new turns.
 * @param {number} [input.threshold]
 * @param {number} [input.ratio]
 * @returns {{scroll:boolean, showJump:boolean, reason:string}}
 */
export function decideOnContentChange(input) {
    const {
        metrics,
        previousScrollHeight = 0,
        suspended = false,
        threshold = NEAR_BOTTOM_THRESHOLD_PX,
        ratio = SUBSTANTIAL_UPDATE_VIEWPORT_RATIO
    } = input ?? {};

    const current = metrics ?? { scrollTop: 0, scrollHeight: 0, clientHeight: 0 };

    if (suspended) {
        // Older messages are being inserted above. The reader has not moved and nothing arrived
        // below them, so neither following nor offering to jump is honest here.
        return { scroll: false, showJump: false, reason: 'suspended' };
    }

    const growth = current.scrollHeight - previousScrollHeight;

    // The reader was following if they were at the bottom BEFORE the content grew. Measuring after
    // the growth would report every follower as scrolled-up by exactly the size of the new message.
    const wasNearBottom = isNearBottom(
        {
            scrollTop: current.scrollTop,
            scrollHeight: previousScrollHeight,
            clientHeight: current.clientHeight
        },
        threshold);

    if (!wasNearBottom) {
        return {
            scroll: false,
            showJump: growth > 0,
            reason: growth > 0 ? 'reader-scrolled-up' : 'reader-scrolled-up-no-growth'
        };
    }

    if (isSubstantialUpdate(growth, current.clientHeight, ratio)) {
        return { scroll: false, showJump: true, reason: 'substantial-update' };
    }

    return { scroll: growth > 0, showJump: false, reason: growth > 0 ? 'following' : 'no-growth' };
}

/**
 * What to do after the reader scrolled by hand.
 *
 * Reaching the bottom is the gesture that says "I am caught up", so it both dismisses the jump
 * control and re-arms following. Anything else leaves the control exactly as it was: hiding it
 * because the reader scrolled a little would take away the way back to the latest message.
 *
 * @param {object} input
 * @param {{scrollTop:number, scrollHeight:number, clientHeight:number}} input.metrics
 * @param {boolean} input.jumpVisible
 * @param {number} [input.threshold]
 * @returns {{showJump:boolean, following:boolean}}
 */
export function decideOnReaderScroll(input) {
    const { metrics, jumpVisible = false, threshold = NEAR_BOTTOM_THRESHOLD_PX } = input ?? {};
    const nearBottom = isNearBottom(metrics, threshold);

    return {
        showJump: nearBottom ? false : jumpVisible,
        following: nearBottom
    };
}

/**
 * Where the reader is, in terms that survive the scrollport changing size.
 *
 * Captured continuously so it is available from BEFORE a resize, which is the only moment at
 * which it is still true: by the time a resize has been observed the browser has already clamped
 * scrollTop to the new bounds, and the reader's position cannot be recovered from the numbers.
 *
 * @param {{scrollTop:number, scrollHeight:number, clientHeight:number}} metrics
 * @param {number} [threshold]
 * @returns {{following:boolean, relativeAnchor:number}}
 */
export function captureFollowState(metrics, threshold = NEAR_BOTTOM_THRESHOLD_PX) {
    const current = metrics ?? { scrollTop: 0, scrollHeight: 0, clientHeight: 0 };
    const maxScroll = Math.max(0, current.scrollHeight - current.clientHeight);

    return {
        following: isNearBottom(current, threshold),
        relativeAnchor: maxScroll > 0 ? Math.min(1, Math.max(0, current.scrollTop / maxScroll)) : 0
    };
}

/**
 * What to do after the scrollport itself changed size.
 *
 * A resize is not a message. Compact to full screen and back changes both the height of the
 * scrollport and, because the measure changes with it, how much the same text wraps to — so the
 * content height moves in either direction for reasons that have nothing to do with new turns.
 * Feeding that through the content rules produces a jump control announcing messages the reader
 * has already read (shrinking the panel makes the same conversation taller), or silently drops a
 * follower off the bottom (widening it makes the conversation shorter).
 *
 * So the reader's intent is carried across instead of re-derived: a follower is put back at the
 * bottom, and anyone else keeps the same relative position in the conversation.
 *
 * @param {object} input
 * @param {boolean} input.wasFollowing Follow state captured BEFORE the size changed.
 * @param {number} input.relativeAnchor Position captured before the size changed, 0..1.
 * @param {{scrollTop:number, scrollHeight:number, clientHeight:number}} input.metrics
 *   Metrics read AFTER the size changed.
 * @returns {{scrollTop:number, hideJump:boolean, reason:string}}
 */
export function decideOnViewportChange(input) {
    const { wasFollowing = false, relativeAnchor = 0, metrics } = input ?? {};
    const current = metrics ?? { scrollTop: 0, scrollHeight: 0, clientHeight: 0 };
    const maxScroll = Math.max(0, current.scrollHeight - current.clientHeight);

    if (wasFollowing) {
        return { scrollTop: maxScroll, hideJump: true, reason: 'resize-following' };
    }

    const anchor = Math.min(1, Math.max(0, relativeAnchor));

    return {
        scrollTop: Math.round(anchor * maxScroll),
        // Left exactly as it was. A reader with genuinely unread messages below still has them
        // after the panel changed size, and taking the control away would strand them.
        hideJump: false,
        reason: 'resize-anchored'
    };
}

/**
 * The scroll behaviour to use, honouring the reader's motion preference.
 *
 * @param {boolean} prefersReducedMotion
 * @returns {'auto'|'smooth'}
 */
export function scrollBehavior(prefersReducedMotion) {
    return prefersReducedMotion ? 'auto' : 'smooth';
}
