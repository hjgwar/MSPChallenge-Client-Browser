// Generic pointer-drag helper shared by the floating plans panel (map.js) and the KPI
// dashboard widgets (kpi-dashboard.js). It only tracks the gesture; what "moving" means
// (absolute left/top, a transform that snaps to a grid on drop, ...) is up to the caller.

/**
 * Makes `handle` start a drag gesture on primary-button pointerdown.
 *
 * @param {HTMLElement} handle
 * @param {object} callbacks
 * @param {(e: PointerEvent) => void} [callbacks.onStart]
 * @param {(dx: number, dy: number, e: PointerEvent) => void} [callbacks.onMove] dx/dy are relative to the pointerdown position.
 * @param {(dx: number, dy: number, cancelled: boolean) => void} [callbacks.onEnd] Called on pointerup, or on pointercancel with cancelled = true.
 * @param {string} [callbacks.ignore] Selector for interactive children that must keep receiving normal clicks.
 * @returns {() => void} Removes the listeners.
 */
export function makeDraggable(handle, { onStart, onMove, onEnd, ignore = 'button, a, input, select' } = {}) {
    let active = false;
    let startX = 0, startY = 0, lastDx = 0, lastDy = 0;

    const onPointerDown = e => {
        if (e.button !== 0) return;
        if (e.target.closest(ignore)) return;
        e.preventDefault();
        handle.setPointerCapture(e.pointerId);
        active = true;
        startX = e.clientX;
        startY = e.clientY;
        lastDx = lastDy = 0;
        onStart?.(e);
    };

    const onPointerMove = e => {
        if (!active) return;
        lastDx = e.clientX - startX;
        lastDy = e.clientY - startY;
        onMove?.(lastDx, lastDy, e);
    };

    const finish = cancelled => {
        if (!active) return;
        active = false;
        onEnd?.(lastDx, lastDy, cancelled);
    };
    const onPointerUp     = () => finish(false);
    const onPointerCancel = () => finish(true);

    handle.addEventListener('pointerdown', onPointerDown);
    handle.addEventListener('pointermove', onPointerMove);
    handle.addEventListener('pointerup', onPointerUp);
    handle.addEventListener('pointercancel', onPointerCancel);

    return () => {
        handle.removeEventListener('pointerdown', onPointerDown);
        handle.removeEventListener('pointermove', onPointerMove);
        handle.removeEventListener('pointerup', onPointerUp);
        handle.removeEventListener('pointercancel', onPointerCancel);
    };
}
