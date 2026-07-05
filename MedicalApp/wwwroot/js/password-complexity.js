/*!
 * password-complexity.js — Feb 2026
 * Client-side companion for LocalizedPasswordComplexityAttribute.
 *
 * Provides:
 *   1) jQuery Validation adapter → the same 5 rules the server enforces.
 *      Failure message is the full rule list, matching the server output.
 *   2) Live-feedback UI: as the user types, each rule row toggles between
 *      ✔ (green) and ✖ (red) so users see immediately what is missing.
 *   3) Info button (i) in the input's top-right corner — Bootstrap 5 popover
 *      configured with trigger:'click' shows the same rule list on tap/click.
 *
 * All texts come from data-* attributes rendered server-side, so the UI
 * automatically respects the current UI culture — no JS translation table.
 *
 * Wiring: any password <input> that carries `data-val-pwdcomplex-min` gets
 * wrapped in a small container with the live feedback + info button. Views
 * only need to include this script once.
 */
(function () {
    'use strict';

    // ---- 1. Rule evaluation --------------------------------------------------
    // Uses the SAME set defined in LocalizedPasswordComplexityAttribute.SpecialChars
    // and is echoed via data-val-pwdcomplex-specialset for cross-checking.
    var DEFAULT_SPECIAL = "!?@#$%^&*";

    function evaluateRules(value, specialSet) {
        var chars = specialSet || DEFAULT_SPECIAL;
        return {
            min:     (value || "").length >= 8,
            upper:   /[A-Z]/.test(value || ""),
            lower:   /[a-z]/.test(value || ""),
            digit:   /[0-9]/.test(value || ""),
            special: (function (v) {
                for (var i = 0; i < v.length; i++) {
                    if (chars.indexOf(v[i]) >= 0) return true;
                }
                return false;
            })(value || "")
        };
    }

    function allPass(r) {
        return r.min && r.upper && r.lower && r.digit && r.special;
    }

    // ---- 2. jQuery Validate adapter -----------------------------------------
    // We register a rule + adapter so unobtrusive validation picks up
    // data-val-pwdcomplex-* attributes.
    if (window.jQuery && window.jQuery.validator) {
        window.jQuery.validator.addMethod("pwdcomplex", function (value, element) {
            // Empty is treated as valid — the paired required rule owns the
            // "empty" error so we don't stack "field required" + "rules not met".
            if (!value) return true;
            return allPass(evaluateRules(value, element.getAttribute("data-val-pwdcomplex-specialset")));
        }, "");

        if (window.jQuery.validator.unobtrusive) {
            window.jQuery.validator.unobtrusive.adapters.add(
                "pwdcomplex",
                ["header", "min", "upper", "lower", "digit", "special", "specialset"],
                function (options) {
                    options.rules["pwdcomplex"] = true;
                    // Compose the multi-line message the server also emits.
                    // (Newlines get preserved by the .field-validation-error
                    // span thanks to the `white-space: pre-line` CSS we ship.)
                    options.messages["pwdcomplex"] =
                        options.params.header + ":\n"
                        + "• " + options.params.min     + "\n"
                        + "• " + options.params.upper   + "\n"
                        + "• " + options.params.lower   + "\n"
                        + "• " + options.params.digit   + "\n"
                        + "• " + options.params.special;
                }
            );
        }
    }

    // ---- 3. UI enhancement: live feedback + info button ---------------------
    function enhance(input) {
        if (input.__pwdEnhanced) return;
        input.__pwdEnhanced = true;

        var header  = input.getAttribute("data-val-pwdcomplex-header")  || "Password requirements";
        var minTxt  = input.getAttribute("data-val-pwdcomplex-min")     || "At least 8 characters";
        var upTxt   = input.getAttribute("data-val-pwdcomplex-upper")   || "At least one uppercase letter (A-Z)";
        var lowTxt  = input.getAttribute("data-val-pwdcomplex-lower")   || "At least one lowercase letter (a-z)";
        var digTxt  = input.getAttribute("data-val-pwdcomplex-digit")   || "At least one digit (0-9)";
        var speTxt  = input.getAttribute("data-val-pwdcomplex-special") || "At least one special character (!?@#$%^&*)";
        var specSet = input.getAttribute("data-val-pwdcomplex-specialset") || DEFAULT_SPECIAL;

        // Wrap input in a position-relative container so we can place the (i) button.
        var wrap = document.createElement("div");
        wrap.className = "pwd-complex-wrap position-relative";
        input.parentNode.insertBefore(wrap, input);
        wrap.appendChild(input);

        // Info (i) button — Bootstrap popover, click trigger. Uses raw HTML
        // so the rule list looks the same as in the live feedback.
        var infoBtn = document.createElement("button");
        infoBtn.type = "button";
        infoBtn.className = "pwd-complex-info-btn";
        infoBtn.setAttribute("aria-label", header);
        infoBtn.setAttribute("data-bs-toggle", "popover");
        infoBtn.setAttribute("data-bs-trigger", "click");
        infoBtn.setAttribute("data-bs-placement", "top");
        infoBtn.setAttribute("data-bs-html", "true");
        infoBtn.setAttribute("data-bs-title", header);
        infoBtn.setAttribute("data-bs-content",
            '<ul class="pwd-complex-popover-list mb-0 ps-3">' +
                '<li>' + escapeHtml(minTxt) + '</li>' +
                '<li>' + escapeHtml(upTxt)  + '</li>' +
                '<li>' + escapeHtml(lowTxt) + '</li>' +
                '<li>' + escapeHtml(digTxt) + '</li>' +
                '<li>' + escapeHtml(speTxt) + '</li>' +
            '</ul>');
        infoBtn.innerHTML =
            '<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 16 16" fill="currentColor" aria-hidden="true">' +
                '<path d="M8 15A7 7 0 1 1 8 1a7 7 0 0 1 0 14zm0 1A8 8 0 1 0 8 0a8 8 0 0 0 0 16z"/>' +
                '<path d="m8.93 6.588-2.29.287-.082.38.45.083c.294.07.352.176.288.469l-.738 3.468c-.194.897.105 1.319.808 1.319.545 0 1.178-.252 1.465-.598l.088-.416c-.2.176-.492.246-.686.246-.275 0-.375-.193-.304-.533L8.93 6.588zM9 4.5a1 1 0 1 1-2 0 1 1 0 0 1 2 0z"/>' +
            '</svg>';
        wrap.appendChild(infoBtn);

        // Instantiate the popover once Bootstrap is ready.
        function initPopover() {
            if (window.bootstrap && window.bootstrap.Popover) {
                new window.bootstrap.Popover(infoBtn);
            } else {
                setTimeout(initPopover, 200);
            }
        }
        initPopover();

        // Live feedback panel — a small card under the input.
        var panel = document.createElement("div");
        panel.className = "pwd-complex-panel d-none";
        panel.setAttribute("aria-live", "polite");
        panel.innerHTML =
            '<div class="pwd-complex-title">' + escapeHtml(header) + '</div>' +
            '<ul class="pwd-complex-rules">' +
                ruleLi("min",     minTxt) +
                ruleLi("upper",   upTxt)  +
                ruleLi("lower",   lowTxt) +
                ruleLi("digit",   digTxt) +
                ruleLi("special", speTxt) +
            '</ul>';
        wrap.parentNode.insertBefore(panel, wrap.nextSibling);

        function ruleLi(id, txt) {
            return '<li class="pwd-complex-rule" data-rule="' + id + '">' +
                        '<span class="pwd-complex-icon">•</span> ' +
                        '<span class="pwd-complex-txt">' + escapeHtml(txt) + '</span>' +
                   '</li>';
        }

        function refresh() {
            var res = evaluateRules(input.value, specSet);
            var anyTyped = input.value.length > 0;
            panel.classList.toggle("d-none", !anyTyped);

            Object.keys(res).forEach(function (k) {
                var li = panel.querySelector('[data-rule="' + k + '"]');
                if (!li) return;
                var ok = res[k];
                li.classList.toggle("ok", ok);
                li.classList.toggle("fail", !ok);
                var icon = li.querySelector(".pwd-complex-icon");
                icon.textContent = ok ? "✓" : "✗";
            });

            // Once all rules pass, keep the panel visible for a moment then
            // collapse to a compact success indicator — reduces visual noise.
            panel.classList.toggle("all-ok", allPass(res));
        }

        input.addEventListener("input",  refresh);
        input.addEventListener("focus",  refresh);
        input.addEventListener("blur",   function () {
            // Hide feedback panel on blur when the input is empty; otherwise
            // keep it visible so the user still sees pending rules.
            if (!input.value) panel.classList.add("d-none");
        });
    }

    function escapeHtml(s) {
        return String(s)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;");
    }

    function scan(root) {
        var nodes = (root || document).querySelectorAll("input[type=password][data-val-pwdcomplex-min]");
        for (var i = 0; i < nodes.length; i++) enhance(nodes[i]);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", function () { scan(document); });
    } else {
        scan(document);
    }
})();
