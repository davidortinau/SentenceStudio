let state = null;

function contains(parent, child) {
    if (typeof parent.contains === 'function') {
        return parent.contains(child);
    }

    for (let current = child; current; current = current.parentElement) {
        if (current === parent) return true;
    }
    return false;
}

function focusableElements(dialog) {
    if (typeof dialog.querySelectorAll !== 'function') return [];
    return Array.from(dialog.querySelectorAll(
        'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), ' +
        'textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'))
        .filter(element => !element.hidden);
}

export function activate(dialogId, contentId, initialFocusId) {
    deactivate();

    const dialog = document.getElementById(dialogId);
    const content = document.getElementById(contentId);
    const initialFocus = document.getElementById(initialFocusId);
    if (!dialog || !content || !initialFocus) return false;

    if (contains(content, dialog)) {
        throw new Error('Fullscreen dialog must be a sibling of the inert activity content.');
    }

    const onKeyDown = event => {
        if (event.key !== 'Tab') return;

        const focusable = focusableElements(dialog);
        if (focusable.length === 0) {
            event.preventDefault();
            dialog.focus?.();
            return;
        }

        const activeIndex = focusable.indexOf(document.activeElement);
        const nextIndex = event.shiftKey
            ? (activeIndex <= 0 ? focusable.length - 1 : activeIndex - 1)
            : (activeIndex < 0 || activeIndex === focusable.length - 1 ? 0 : activeIndex + 1);

        event.preventDefault();
        focusable[nextIndex].focus();
    };

    dialog.addEventListener('keydown', onKeyDown);
    state = { dialog, onKeyDown };
    initialFocus.focus();
    return true;
}

export function deactivate() {
    if (!state) return;
    state.dialog.removeEventListener('keydown', state.onKeyDown);
    state = null;
}
