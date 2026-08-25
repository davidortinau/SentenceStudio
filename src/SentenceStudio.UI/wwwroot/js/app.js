// SentenceStudio Blazor JS Interop Module
// Chart.js and Tom Select integration

const charts = {};
const tomSelects = {};

/**
 * Create a Chart.js doughnut chart.
 * @param {string} canvasId - Canvas element ID
 * @param {string[]} labels - Data labels
 * @param {number[]} values - Data values
 * @param {string[]} colors - Background colors
 */
export function createDoughnutChart(canvasId, labels, values, colors) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    // Destroy existing chart if any
    if (charts[canvasId]) {
        charts[canvasId].destroy();
    }

    charts[canvasId] = new Chart(canvas, {
        type: 'doughnut',
        data: {
            labels: labels,
            datasets: [{
                data: values,
                backgroundColor: colors,
                borderWidth: 0,
                hoverOffset: 4
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            cutout: '65%',
            plugins: {
                legend: {
                    position: 'bottom',
                    labels: {
                        color: getComputedStyle(document.documentElement)
                            .getPropertyValue('--ss-text-secondary').trim() || '#C6D0E7',
                        padding: 12,
                        usePointStyle: true,
                        pointStyleWidth: 10
                    }
                }
            }
        }
    });
}

/**
 * Update chart data.
 * @param {string} canvasId - Canvas element ID
 * @param {number[]} values - New data values
 */
export function updateChartData(canvasId, values) {
    if (charts[canvasId]) {
        charts[canvasId].data.datasets[0].data = values;
        charts[canvasId].update();
    }
}

/**
 * Initialize a Tom Select combobox with optional Blazor callback.
 * @param {string} elementId - Select element ID
 * @param {object[]} options - Options array [{value, text}]
 * @param {boolean} multiple - Allow multiple selection
 * @param {object} dotNetRef - Optional DotNet object reference for change callback
 * @param {string} callbackMethod - Optional method name to invoke on change
 */
export function initTomSelect(elementId, options, multiple, dotNetRef, callbackMethod) {
    const el = document.getElementById(elementId);
    if (!el) return;

    // Destroy existing instance
    if (tomSelects[elementId]) {
        tomSelects[elementId].destroy();
    }

    tomSelects[elementId] = new TomSelect(el, {
        options: options,
        maxItems: multiple ? null : 1,
        plugins: multiple ? ['remove_button'] : [],
        create: false,
        allowEmptyOption: true
    });

    if (dotNetRef && callbackMethod) {
        tomSelects[elementId].on('change', function() {
            const val = tomSelects[elementId].getValue();
            const values = Array.isArray(val) ? val : (val ? [val] : []);
            dotNetRef.invokeMethodAsync(callbackMethod, values);
        });
    }
}

/**
 * Get selected values from Tom Select.
 * @param {string} elementId - Select element ID
 * @returns {string[]} Selected values
 */
export function getTomSelectValues(elementId) {
    if (tomSelects[elementId]) {
        const val = tomSelects[elementId].getValue();
        return Array.isArray(val) ? val : [val];
    }
    return [];
}

/**
 * Set selected values on a Tom Select instance.
 * @param {string} elementId - Select element ID
 * @param {string[]} values - Values to select
 */
export function setTomSelectValues(elementId, values) {
    if (tomSelects[elementId]) {
        tomSelects[elementId].clear(true);
        if (values && values.length > 0) {
            values.forEach(v => tomSelects[elementId].addItem(v, true));
        }
    }
}

/**
 * Destroy a Tom Select instance.
 * @param {string} elementId - Select element ID
 */
export function destroyTomSelect(elementId) {
    if (tomSelects[elementId]) {
        tomSelects[elementId].destroy();
        delete tomSelects[elementId];
    }
}

/**
 * Apply theme to the HTML element.
 * Sets both data-bs-theme (light/dark) and data-ss-theme (color palette).
 * @param {string} theme - Theme name (seoul-pop, ocean, forest, sunset, monochrome)
 * @param {string} mode - Mode (light or dark)
 */
const BOOTSTRAP_CDN = 'https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css';
const BOOTSWATCH_THEMES = ['flatly', 'sketchy', 'slate', 'vapor', 'brite'];

export function applyTheme(theme, mode) {
    const html = document.documentElement;
    html.setAttribute('data-bs-theme', mode);
    html.setAttribute('data-ss-theme', theme);

    // Swap Bootstrap CSS for Bootswatch themes
    const link = document.getElementById('bootstrap-theme');
    if (link) {
        if (BOOTSWATCH_THEMES.includes(theme)) {
            link.href = `_content/SentenceStudio.UI/css/themes/${theme}.min.css`;
        } else {
            link.href = BOOTSTRAP_CDN;
        }
    }
}

export function setFontScale(scale) {
    document.documentElement.style.setProperty('--ss-font-scale', scale);
}

/**
 * Reads the per-browser appearance cookie.
 *
 * The server can only read this cookie during server-side rendering, when it arrives on the
 * request. Once an InteractiveServer circuit is running there is no HTTP request left, so the
 * browser is the only thing that can still see it and the server asks through here.
 *
 * @param {string} name - Cookie name.
 * @returns {string|null} The raw token, or null when the cookie is absent.
 */
export function readAppearanceCookie(name) {
    const prefix = `${name}=`;
    const match = document.cookie
        .split(';')
        .map(part => part.trim())
        .find(part => part.startsWith(prefix));

    if (!match) return null;

    try {
        return decodeURIComponent(match.substring(prefix.length));
    } catch {
        // A hand-mangled cookie with a stray % is not worth throwing over; the server validates
        // the value anyway and falls back to the default when it cannot parse it.
        return null;
    }
}

/**
 * Writes the per-browser appearance cookie.
 *
 * Mirrors the server's cookie options so a value written from a circuit and a value written from
 * an HTTP response are indistinguishable to the next request: same path, same SameSite, and Secure
 * whenever the page is served over HTTPS.
 *
 * The value carries only a theme id, a mode and a text-size percentage — never identity and never
 * a secret — which is why it is script-writable at all.
 *
 * @param {string} name - Cookie name.
 * @param {string} token - Bounded appearance token, e.g. "v1.seoul-pop.dark.100".
 * @param {number} lifetimeDays - How long the browser should keep it.
 */
export function writeAppearanceCookie(name, token, lifetimeDays) {
    const maxAge = Math.max(0, Math.floor(lifetimeDays * 24 * 60 * 60));
    const secure = window.location.protocol === 'https:' ? '; Secure' : '';
    document.cookie =
        `${name}=${encodeURIComponent(token)}; Path=/; Max-Age=${maxAge}; SameSite=Lax${secure}`;
}

export function resetScroll() {
    const main = document.querySelector('.main-content');
    if (main) main.scrollTop = 0;
}

/**
 * Show a confirm dialog using a Bootstrap modal instead of native confirm()
 * to avoid the ugly app://0.0.0.1 origin in WebView.
 */
export function showConfirm(message) {
    return new Promise(resolve => {
        const id = 'ss-confirm-' + Date.now();
        const html = `
            <div class="modal fade" id="${id}" tabindex="-1" data-bs-backdrop="static">
                <div class="modal-dialog modal-dialog-centered modal-sm">
                    <div class="modal-content">
                        <div class="modal-body p-4">
                            <p class="ss-body1 mb-0">${message}</p>
                        </div>
                        <div class="modal-footer border-0 pt-0">
                            <button type="button" class="btn btn-ss-secondary" data-action="cancel">Cancel</button>
                            <button type="button" class="btn btn-ss-danger" data-action="confirm">Delete</button>
                        </div>
                    </div>
                </div>
            </div>`;
        document.body.insertAdjacentHTML('beforeend', html);
        const el = document.getElementById(id);
        const modal = new bootstrap.Modal(el);

        el.querySelector('[data-action="confirm"]').addEventListener('click', () => { modal.hide(); resolve(true); });
        el.querySelector('[data-action="cancel"]').addEventListener('click', () => { modal.hide(); resolve(false); });
        el.addEventListener('hidden.bs.modal', () => { el.remove(); });

        modal.show();
    });
}


/* ============================================================
   LEARNING COACH interop
   Only four exports. Everything else stays in Blazor.
   ============================================================ */

/**
 * The .NET reference for the currently open coach dialog, kept per ELEMENT so a
 * re-rendered dialog never invokes a disposed reference from a previous open.
 */
const coachModalRefs = new WeakMap();

/**
 * Current viewport width in CSS pixels. Used once, at entry-point click, to choose
 * between the overlay and the full-screen /coach route. Never platform-sniff.
 */
export function getViewportWidth() {
    return window.innerWidth || document.documentElement.clientWidth || 0;
}

/**
 * Show the coach workspace as a real Bootstrap modal so focus containment, Escape,
 * scroll-lock and the backdrop come from the framework rather than hand-rolled markup.
 *
 * Element lifetime is the subtle part. CoachWorkspaceHost only renders the dialog while the
 * workspace is open, so Blazor DESTROYS the element on close and builds a new one — with the
 * same id — on reopen. A module-level `{ [id]: modal }` cache therefore handed back a Modal
 * bound to a detached element and a closed-over, already-disposed DotNetObjectReference: the
 * second open showed nothing, while the background was still inerted, leaving the app frozen
 * with no visible dialog.
 *
 * Bootstrap keys its instances off the element itself, so `getOrCreateInstance` on the CURRENT
 * element is inherently correct across re-renders. The hidden handler is bound once per element
 * and reads the current reference from a WeakMap rather than capturing one.
 */
export function openCoachModal(elementId, dotNetRef) {
    const el = document.getElementById(elementId);
    if (!el) return;

    // Always the current reference, even if this element was bound on an earlier open.
    coachModalRefs.set(el, dotNetRef);

    const modal = bootstrap.Modal.getOrCreateInstance(el, {
        backdrop: true,
        keyboard: true,
        focus: true
    });

    if (el.dataset.coachModalBound !== 'true') {
        el.dataset.coachModalBound = 'true';

        el.addEventListener('hidden.bs.modal', () => {
            document.body.classList.remove('coach-open');
            releaseBackgroundInert();

            const ref = coachModalRefs.get(el);
            coachModalRefs.delete(el);
            delete el.dataset.coachModalBound;

            // Drop Bootstrap's own instance so a detached element can never be reused, and
            // so its Map entry does not pin the element in memory. Deferred to avoid
            // disposing from inside Bootstrap's own event dispatch.
            queueMicrotask(() => {
                bootstrap.Modal.getInstance(el)?.dispose();
            });

            if (ref) {
                ref.invokeMethodAsync('OnCoachModalHidden');
            }
        });
    }

    document.body.classList.add('coach-open');
    applyBackgroundInert(el);
    modal.show();
}

/** Hide the coach workspace. The hidden.bs.modal handler performs the cleanup. */
export function closeCoachModal(elementId) {
    const el = document.getElementById(elementId);
    if (el) {
        bootstrap.Modal.getInstance(el)?.hide();
    }
}

/**
 * Tear down the coach modal unconditionally.
 *
 * Called from CoachDialog.DisposeAsync, which can run AFTER Blazor has already removed the
 * element — in which case `hidden.bs.modal` never fires and its cleanup never runs. Without
 * this the page keeps `coach-open` and, far worse, keeps every backgrounded sibling `inert`:
 * the app would be permanently unusable with nothing on screen to explain why.
 */
export function disposeCoachModal(elementId) {
    const el = document.getElementById(elementId);

    if (el) {
        bootstrap.Modal.getInstance(el)?.dispose();
        delete el.dataset.coachModalBound;
        coachModalRefs.delete(el);
    }

    document.body.classList.remove('coach-open');
    releaseBackgroundInert();
}

/**
 * Move focus to an element by id. Used for the announce-or-focus policy: after a
 * tapped acceptance or undo the initiating button is destroyed, so focus is moved to
 * the resulting receipt (which is tabindex="-1") instead of falling to <body>.
 */
export function focusElement(elementId) {
    const el = document.getElementById(elementId);
    if (el && typeof el.focus === 'function') {
        el.focus({ preventScroll: false });
    }
}

/*
 * Elements this feature inerted are marked with data-coach-inert. The marker, rather than a
 * collection keyed by the dialog element, is what makes restoration survivable: Blazor can
 * destroy the dialog before cleanup runs, and a keyed collection would then be unreachable
 * while the siblings stayed inert forever. The marker also keeps the "never clear an inert we
 * did not set" rule, because restoration only touches nodes carrying it.
 */
const COACH_INERT_MARKER = 'data-coach-inert';

/**
 * Make everything except the dialog unreachable, by walking the ancestor chain from the
 * dialog up to <body> and inerting the SIBLINGS at each level.
 *
 * An earlier version inerted every direct child of <body>. The coach modal is rendered by
 * CoachWorkspaceHost inside MainLayout, so it is a DESCENDANT of the Blazor app root, not a
 * child of <body> — which meant the app root was inerted and the dialog inside it went with
 * it. The whole workspace became non-interactive and hidden from assistive tech the moment
 * it opened.
 */
function applyBackgroundInert(modalEl) {
    let node = modalEl;

    while (node && node !== document.body && node.parentElement) {
        const parent = node.parentElement;
        for (const sibling of parent.children) {
            if (sibling === node) continue;
            if (sibling.classList && sibling.classList.contains('modal-backdrop')) continue;
            // Never take ownership of an inert we did not set.
            if (sibling.hasAttribute('inert')) continue;

            sibling.setAttribute('inert', '');
            sibling.setAttribute(COACH_INERT_MARKER, '');
        }
        node = parent;
    }
}

/** Restore every element this feature inerted, whatever happened to the dialog. */
function releaseBackgroundInert() {
    for (const node of document.querySelectorAll('[' + COACH_INERT_MARKER + ']')) {
        node.removeAttribute('inert');
        node.removeAttribute(COACH_INERT_MARKER);
    }
}

/**
 * Enter-to-send for the coach composer.
 *
 * Owned by JS rather than Blazor's @onkeydown for two reasons Blazor cannot express:
 *   1. preventDefault must be conditional (only bare Enter), and Blazor's
 *      `@onkeydown:preventDefault` is fixed at render time, so it would swallow every key.
 *      Without it the browser inserts a newline AFTER the send, and the following input
 *      event repopulates the composer with the text that was just sent.
 *   2. KeyboardEventArgs exposes no `isComposing`, so a Blazor handler cannot tell a real
 *      Enter from an IME commit. Korean input commits with Enter constantly; sending on
 *      those would make the composer unusable in the app's own target language.
 *
 * Whether Enter sends is read live from `data-enter-sends` on the element, so the overlay
 * degrading below 768px changes the behavior with no re-binding.
 */
export function bindComposerEnter(textareaId, dotNetRef) {
    const el = document.getElementById(textareaId);
    if (!el || el.dataset.coachEnterBound === 'true') return;

    el.dataset.coachEnterBound = 'true';
    el.addEventListener('keydown', event => {
        if (event.key !== 'Enter' || event.shiftKey) return;
        // isComposing, and the legacy 229 keycode, both mean "the IME is mid-composition".
        if (event.isComposing || event.keyCode === 229) return;
        // Narrow presentations use an explicit send button; Return inserts a newline.
        if (el.dataset.enterSends !== 'true') return;

        event.preventDefault();
        dotNetRef.invokeMethodAsync('SendFromComposer');
    });
}

/**
 * Copy text to the clipboard, reporting success rather than throwing.
 *
 * The async Clipboard API needs a secure context and a permission the user can refuse, so a
 * refusal is a normal outcome here, not an error. The execCommand path is the fallback for
 * older or non-secure contexts; it is deprecated but still the only thing that works there.
 *
 * Returns true only when the text actually reached the clipboard, so the caller can show
 * neutral feedback instead of claiming a copy that did not happen.
 */
export async function copyTextToClipboard(text) {
    if (typeof text !== 'string' || text.length === 0) return false;

    if (navigator.clipboard && window.isSecureContext) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // Fall through: a refused permission is not a reason to give up entirely.
        }
    }

    try {
        const area = document.createElement('textarea');
        area.value = text;
        // Kept out of the accessibility tree and off screen so it never steals focus visibly
        // or is announced while it exists.
        area.setAttribute('aria-hidden', 'true');
        area.setAttribute('tabindex', '-1');
        area.style.position = 'fixed';
        area.style.opacity = '0';
        area.style.pointerEvents = 'none';
        document.body.appendChild(area);

        const active = document.activeElement;
        area.select();
        const ok = document.execCommand('copy');
        document.body.removeChild(area);

        // Focus must go back where the learner left it, or the next keystroke lands nowhere.
        if (active && typeof active.focus === 'function') active.focus();

        return ok === true;
    } catch {
        return false;
    }
}

export function unbindComposerEnter(textareaId) {
    const el = document.getElementById(textareaId);
    if (el) {
        delete el.dataset.coachEnterBound;
    }
}

let coachViewportHandler = null;

/**
 * Report viewport width changes to Blazor so the overlay can swap between the >=992px
 * split layout and the 768-991px tab layout *in place*. Resizing must never navigate:
 * that would destroy the composer draft and scroll position mid-session.
 * Debounced so a drag-resize does not flood the circuit.
 */
export function observeViewport(dotNetRef) {
    unobserveViewport();

    let pending = null;
    coachViewportHandler = () => {
        if (pending) clearTimeout(pending);
        pending = setTimeout(() => {
            dotNetRef.invokeMethodAsync('OnViewportChanged', getViewportWidth());
        }, 150);
    };

    window.addEventListener('resize', coachViewportHandler);
    return getViewportWidth();
}

export function unobserveViewport() {
    if (coachViewportHandler) {
        window.removeEventListener('resize', coachViewportHandler);
        coachViewportHandler = null;
    }
}

/**
 * Streams a server response straight into the browser's download machinery.
 *
 * The bytes go response -> blob -> object URL -> click, and the URL is revoked immediately
 * afterwards. Nothing is turned into a string, written to localStorage, or kept in a cache: an
 * exported transcript is the learner's own words, and a convenience copy is a copy that would
 * outlive the delete button.
 */
export async function downloadFileFromStream(fileName, streamReference) {
    const buffer = await streamReference.arrayBuffer();
    const blob = new Blob([buffer]);
    const url = URL.createObjectURL(blob);

    try {
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = fileName ?? 'download';
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
    } finally {
        // Revoked on the same turn. A live object URL is a readable copy of the transcript.
        URL.revokeObjectURL(url);
    }
}

/**
 * Keeps the reader looking at the same message after older ones are inserted above it.
 *
 * Prepending changes every offset below the insertion point, so restoring a scroll position by
 * number lands somewhere arbitrary. Scrolling the anchor element back to where it was is the only
 * thing that means "you did not move".
 */
export function restoreScrollAnchor(elementId) {
    const anchor = document.getElementById(elementId);
    if (!anchor) return;

    // 'instant' rather than 'smooth': this is a correction, not a journey, and animating it would
    // both look like a jump and fight prefers-reduced-motion.
    anchor.scrollIntoView({ behavior: 'instant', block: 'start' });
}
