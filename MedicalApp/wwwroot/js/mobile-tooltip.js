/*!
 * mobile-tooltip.js — Feb 2026
 * Makes native `title="..."` tooltips discoverable on touch-only devices
 * (phones, tablets) where `:hover` never fires.
 *
 * Strategy:
 *   - No-op unless the browser reports `(hover: none) and (pointer: coarse)`.
 *   - On `touchstart` of any element carrying a non-empty `title`, we show a
 *     small floating overlay near the tap and auto-hide it after ~2.5 s or
 *     when the user taps elsewhere.
 *   - The element's native `title` attribute is TEMPORARILY moved to
 *     `data-title-mobile` while the tooltip is visible so the browser's own
 *     long-press tooltip does not fight ours. On dismiss we restore it.
 *   - Buttons/links keep their normal `click` behaviour — we only intercept
 *     `touchstart` to *show* the hint, we never `preventDefault()`.
 *
 * Zero external dependency; ~1.4 kB minified.
 */
(function () {
    'use strict';

    // Feature-detect touch-only devices. Desktops with touchscreens still
    // report `(hover: hover)` so they keep native hover tooltips.
    var isTouchOnly = false;
    try {
        isTouchOnly = window.matchMedia &&
            window.matchMedia('(hover: none) and (pointer: coarse)').matches;
    } catch (e) { /* older browsers → treat as desktop */ }

    if (!isTouchOnly) return;

    var HIDE_MS = 2500;
    var overlay = null;
    var hideTimer = null;
    var lastHostEl = null;

    function ensureOverlay() {
        if (overlay) return overlay;
        overlay = document.createElement('div');
        overlay.className = 'mobile-tooltip-overlay';
        overlay.setAttribute('role', 'tooltip');
        overlay.setAttribute('aria-hidden', 'true');
        document.body.appendChild(overlay);
        return overlay;
    }

    function positionOverlay(tip, x, y) {
        // Place tip ~10 px below the tap, clamped to viewport edges.
        var pad = 8;
        var vw = document.documentElement.clientWidth;
        var vh = document.documentElement.clientHeight;
        tip.style.visibility = 'hidden';
        tip.style.display = 'block';
        var w = tip.offsetWidth;
        var h = tip.offsetHeight;
        var left = Math.min(Math.max(pad, x - w / 2), vw - w - pad);
        var top = y + 14;
        if (top + h > vh - pad) top = Math.max(pad, y - h - 14);
        tip.style.left = left + 'px';
        tip.style.top = top + 'px';
        tip.style.visibility = 'visible';
    }

    function hide() {
        if (!overlay) return;
        overlay.classList.remove('is-visible');
        overlay.setAttribute('aria-hidden', 'true');
        // Restore native title on the previously-shown host, so long-press
        // and screen-readers keep working normally.
        if (lastHostEl && lastHostEl.hasAttribute('data-title-mobile')) {
            var restore = lastHostEl.getAttribute('data-title-mobile');
            lastHostEl.setAttribute('title', restore);
            lastHostEl.removeAttribute('data-title-mobile');
        }
        lastHostEl = null;
        if (hideTimer) {
            clearTimeout(hideTimer);
            hideTimer = null;
        }
    }

    function show(hostEl, text, x, y) {
        var tip = ensureOverlay();
        // Restore any previous host before switching.
        if (lastHostEl && lastHostEl !== hostEl) hide();

        tip.textContent = text;
        positionOverlay(tip, x, y);
        tip.classList.add('is-visible');
        tip.setAttribute('aria-hidden', 'false');

        // Prevent the browser's own long-press tooltip from appearing on top
        // by stashing the native title.
        if (hostEl.hasAttribute('title')) {
            hostEl.setAttribute('data-title-mobile', hostEl.getAttribute('title'));
            hostEl.setAttribute('title', '');
        }
        lastHostEl = hostEl;

        if (hideTimer) clearTimeout(hideTimer);
        hideTimer = setTimeout(hide, HIDE_MS);
    }

    document.addEventListener('touchstart', function (ev) {
        var t = ev.target;
        // Walk up to the nearest [title] host (buttons often wrap SVGs).
        while (t && t !== document && t.nodeType === 1) {
            if (t.hasAttribute && t.hasAttribute('title') && t.getAttribute('title')) {
                var text = t.getAttribute('title');
                var touch = ev.touches && ev.touches[0];
                var x = touch ? touch.clientX : 0;
                var y = touch ? touch.clientY : 0;
                show(t, text, x, y);
                return;
            }
            t = t.parentNode;
        }
        // Tap outside a [title] → dismiss.
        if (overlay && overlay.classList.contains('is-visible')) hide();
    }, { passive: true });

    // Also hide on scroll — the tooltip would otherwise stay orphaned.
    window.addEventListener('scroll', function () {
        if (overlay && overlay.classList.contains('is-visible')) hide();
    }, { passive: true });
})();
