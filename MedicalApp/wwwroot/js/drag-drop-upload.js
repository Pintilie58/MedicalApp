/*!
 * drag-drop-upload.js — Feb 2026
 * Progressive enhancement for file inputs. Any element with
 * `data-dnd-target="pdf"` becomes a PDF drop zone that feeds the FIRST
 * `<input type="file">` found inside it.
 *
 * Design goals:
 *   - ZERO impact when the attribute is absent (100% opt-in).
 *   - ZERO changes to the existing form submit flow — we just populate
 *     `input.files` and dispatch a synthetic `change` event so any legacy
 *     listener (filename label updater etc.) keeps working unchanged.
 *   - Respects the `multiple` attribute of the underlying input:
 *       • not-multiple  → keeps only the FIRST dropped PDF
 *       • multiple      → keeps ALL dropped PDFs (rejects non-PDFs)
 *   - Non-PDF drops trigger a brief visual "reject" state, no file set.
 *   - Falls back gracefully on browsers without DataTransfer.items (very old
 *     Safari) — the drop is ignored and the user can still click the button.
 *
 * Rollback recipe (in case anything breaks):
 *   1. Delete this file.
 *   2. Delete wwwroot/css/drag-drop-upload.css.
 *   3. Remove the two <link>/<script> lines added to _Layout.cshtml.
 *   4. Remove `data-dnd-target="pdf"` and the `.dnd-hint` span from any view.
 *   → The app returns to its original click-only file picker behaviour.
 */
(function () {
    'use strict';

    function isPdf(file) {
        // Some browsers report empty `type` for drag-dropped files → rely on
        // the file extension as a robust fallback.
        return file.type === 'application/pdf' || /\.pdf$/i.test(file.name);
    }

    function preventDefaults(e) {
        e.preventDefault();
        e.stopPropagation();
    }

    function showRejectFeedback(wrap) {
        wrap.classList.add('dnd-reject');
        setTimeout(function () { wrap.classList.remove('dnd-reject'); }, 1200);
    }

    function attach(wrap) {
        if (wrap.__dndAttached) return;
        var input = wrap.querySelector('input[type="file"]');
        if (!input) return;              // wrapper without a file input → skip
        wrap.__dndAttached = true;

        var acceptsMultiple = input.hasAttribute('multiple');

        // Block the browser's default "open the file" behaviour anywhere in
        // the wrapper — otherwise dropping a PDF navigates away from the page.
        ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(function (evt) {
            wrap.addEventListener(evt, preventDefaults);
        });

        // `dragenter`/`dragleave` fire for every descendant, so we count depth
        // to know when the pointer truly LEAVES the wrapper (depth back to 0).
        var dragDepth = 0;

        wrap.addEventListener('dragenter', function () {
            dragDepth++;
            wrap.classList.add('dnd-drag-over');
        });

        wrap.addEventListener('dragleave', function () {
            dragDepth--;
            if (dragDepth <= 0) {
                dragDepth = 0;
                wrap.classList.remove('dnd-drag-over');
            }
        });

        wrap.addEventListener('drop', function (e) {
            dragDepth = 0;
            wrap.classList.remove('dnd-drag-over');

            var dt = e.dataTransfer;
            var files = (dt && dt.files) ? Array.prototype.slice.call(dt.files) : [];
            if (files.length === 0) return;

            var pdfFiles = files.filter(isPdf);
            var rejected = files.length - pdfFiles.length;

            if (pdfFiles.length === 0) {
                showRejectFeedback(wrap);
                return;
            }
            if (!acceptsMultiple) {
                // Keep only the first PDF when the input doesn't allow multi.
                pdfFiles = [pdfFiles[0]];
            }

            // Populate the native <input type="file"> via DataTransfer API.
            // This is the ONLY reliable way to set files programmatically
            // and still have the form submit them as multipart/form-data.
            try {
                var newDt = new DataTransfer();
                for (var i = 0; i < pdfFiles.length; i++) {
                    newDt.items.add(pdfFiles[i]);
                }
                input.files = newDt.files;
            } catch (err) {
                // Older Safari (< 14.5) doesn't support DataTransfer() ctor.
                // The user will have to click the button — no data corruption.
                showRejectFeedback(wrap);
                return;
            }

            // Fire `change` so pre-existing UI (filename label, custom
            // validity clearing) reacts exactly as if the user picked files
            // through the native dialog.
            var changeEvt;
            try {
                changeEvt = new Event('change', { bubbles: true });
            } catch (e2) {
                // IE11-style fallback (unused in .NET 9 stack, but safe).
                changeEvt = document.createEvent('Event');
                changeEvt.initEvent('change', true, true);
            }
            input.dispatchEvent(changeEvt);

            // If SOME files were rejected because they weren't PDFs, flash a
            // brief reject state too so the user notices (rather than
            // silently dropping them).
            if (rejected > 0) showRejectFeedback(wrap);
        });
    }

    function scan() {
        var wraps = document.querySelectorAll('[data-dnd-target="pdf"]');
        for (var i = 0; i < wraps.length; i++) attach(wraps[i]);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', scan);
    } else {
        scan();
    }
})();
