/*!
 * password-toggle.js — Feb 2026
 * Universal "show/hide password" (👁 eye) icon for every <input type="password">.
 *
 * Behaviour:
 *   - Auto-scans the document on DOMContentLoaded and any subsequent
 *     dynamically added password inputs (via a mini MutationObserver).
 *   - Wraps each input in a `.pwd-toggle-wrap` container (unless it is
 *     already inside a `.pwd-complex-wrap` from password-complexity.js —
 *     in that case it just reuses that wrapper so the (i) info button and
 *     the eye button coexist neatly on the right side of the field).
 *   - Adds a button with two inline SVG icons (eye / eye-slash). Click
 *     toggles input.type between "password" and "text".
 *   - Localised aria-label + title come from `window.MedicalApp.i18n`,
 *     which the _Layout injects with the current UI-culture strings for
 *     PasswordToggleShow / PasswordToggleHide.
 *   - Opt-out: any input with `data-no-toggle` attribute is skipped
 *     (currently unused, but reserved e.g. for CVV or one-time PIN fields).
 */
(function () {
    'use strict';

    var I18N_FALLBACK = {
        passwordShow: 'Show password',
        passwordHide: 'Hide password'
    };

    function i18n(key) {
        var g = (window.MedicalApp && window.MedicalApp.i18n) || {};
        return g[key] || I18N_FALLBACK[key] || key;
    }

    // Inline SVGs — no external dependency, respects `currentColor`.
    var EYE_OPEN =
        '<svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" ' +
        'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
        '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/>' +
        '<circle cx="12" cy="12" r="3"/>' +
        '</svg>';

    var EYE_OFF =
        '<svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" ' +
        'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
        '<path d="M17.94 17.94A10.94 10.94 0 0 1 12 19c-6.5 0-10-7-10-7a19.5 19.5 0 0 1 5.06-5.94"/>' +
        '<path d="M9.9 4.24A10.94 10.94 0 0 1 12 4c6.5 0 10 7 10 7a19.62 19.62 0 0 1-3.17 4.19"/>' +
        '<line x1="1" y1="1" x2="23" y2="23"/>' +
        '<path d="M14.12 14.12A3 3 0 0 1 9.88 9.88"/>' +
        '</svg>';

    function enhance(input) {
        if (input.__pwdToggleEnhanced) return;
        if (input.hasAttribute('data-no-toggle')) return;
        input.__pwdToggleEnhanced = true;

        // Reuse the wrapper produced by password-complexity.js when it exists,
        // otherwise create our own. Both wrappers use `position: relative`.
        var wrap = input.closest('.pwd-complex-wrap');
        var ownsWrap = false;
        if (!wrap) {
            wrap = document.createElement('div');
            wrap.className = 'pwd-toggle-wrap position-relative';
            input.parentNode.insertBefore(wrap, input);
            wrap.appendChild(input);
            ownsWrap = true;
        } else {
            // Flag on the shared wrapper so CSS can shift padding-right to
            // fit BOTH the (i) info button AND the eye button.
            wrap.classList.add('pwd-toggle-wrap--with-info');
        }

        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'pwd-toggle-btn';
        btn.setAttribute('tabindex', '-1'); // don't steal Tab focus from the form
        btn.setAttribute('aria-pressed', 'false');
        btn.setAttribute('aria-label', i18n('passwordShow'));
        btn.setAttribute('title', i18n('passwordShow'));
        btn.innerHTML = EYE_OPEN;
        wrap.appendChild(btn);

        // When we own a fresh wrapper, mark it so CSS knows it's the "eye-only"
        // case (no info button) → eye sits at right:8px instead of right:34px.
        if (ownsWrap) {
            wrap.classList.add('pwd-toggle-wrap--solo');
        }

        btn.addEventListener('click', function () {
            var shown = input.type === 'text';
            if (shown) {
                input.type = 'password';
                btn.innerHTML = EYE_OPEN;
                btn.setAttribute('aria-pressed', 'false');
                btn.setAttribute('aria-label', i18n('passwordShow'));
                btn.setAttribute('title', i18n('passwordShow'));
            } else {
                input.type = 'text';
                btn.innerHTML = EYE_OFF;
                btn.setAttribute('aria-pressed', 'true');
                btn.setAttribute('aria-label', i18n('passwordHide'));
                btn.setAttribute('title', i18n('passwordHide'));
            }
        });
    }

    function scan(root) {
        var nodes = (root || document).querySelectorAll('input[type="password"]');
        for (var i = 0; i < nodes.length; i++) enhance(nodes[i]);
    }

    // Watch for dynamically injected password inputs (e.g. tab-pane switch).
    function observeMutations() {
        if (!window.MutationObserver) return;
        var mo = new MutationObserver(function (records) {
            for (var i = 0; i < records.length; i++) {
                var added = records[i].addedNodes;
                for (var j = 0; j < added.length; j++) {
                    var n = added[j];
                    if (n.nodeType !== 1) continue;
                    if (n.matches && n.matches('input[type="password"]')) {
                        enhance(n);
                    } else if (n.querySelectorAll) {
                        scan(n);
                    }
                }
            }
        });
        mo.observe(document.body, { childList: true, subtree: true });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () {
            scan(document);
            observeMutations();
        });
    } else {
        scan(document);
        observeMutations();
    }
})();
