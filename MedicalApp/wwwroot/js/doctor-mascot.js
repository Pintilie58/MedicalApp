/* ============================================================
   doctor-mascot.js — API global pentru mascota animată.
   Folosit de:
     - CAM Batch Progress  → onProgress(processed, total, finished, status)
     - B2C Interpretation Upload (overlay) → auto-walking
     - Dashboard greeting → idle (CSS-only)
   ============================================================ */
(function() {
    'use strict';

    var STORAGE_KEY = 'docSoundMuted';

    function $(sel, root) { return (root || document).querySelector(sel); }
    function $$(sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); }

    function getAudioCtx(instance) {
        if (!instance._audioCtx) {
            try {
                instance._audioCtx = new (window.AudioContext || window.webkitAudioContext)();
            } catch (e) {
                instance._audioCtx = null;
            }
        }
        return instance._audioCtx;
    }

    function playDing(instance) {
        if (instance.soundMuted) return;
        var ctx = getAudioCtx(instance);
        if (!ctx) return;
        try {
            var o = ctx.createOscillator();
            var g = ctx.createGain();
            o.type = 'sine';
            o.frequency.setValueAtTime(880, ctx.currentTime);
            o.frequency.exponentialRampToValueAtTime(1320, ctx.currentTime + 0.08);
            g.gain.setValueAtTime(0.0001, ctx.currentTime);
            g.gain.exponentialRampToValueAtTime(0.28, ctx.currentTime + 0.02);
            g.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.45);
            o.connect(g).connect(ctx.destination);
            o.start();
            o.stop(ctx.currentTime + 0.45);
        } catch (e) { /* ignore */ }
    }

    function playFanfare(instance) {
        if (instance.soundMuted) return;
        var ctx = getAudioCtx(instance);
        if (!ctx) return;
        try {
            var notes = [523.25, 659.25, 783.99, 1046.5]; // C5, E5, G5, C6
            notes.forEach(function(freq, i) {
                var t = ctx.currentTime + i * 0.14;
                var o = ctx.createOscillator();
                var g = ctx.createGain();
                o.type = 'triangle';
                o.frequency.setValueAtTime(freq, t);
                g.gain.setValueAtTime(0.0001, t);
                g.gain.exponentialRampToValueAtTime(0.25, t + 0.02);
                g.gain.exponentialRampToValueAtTime(0.0001, t + 0.4);
                o.connect(g).connect(ctx.destination);
                o.start(t);
                o.stop(t + 0.45);
            });
        } catch (e) { /* ignore */ }
    }

    /**
     * Longer "success" jingle used specifically when a B2C interpretation
     * finishes (see Views/Account/Dashboard.cshtml → the destination page
     * for a successful /Interpretation/Upload POST). Runs ~2.5 seconds
     * instead of the ~0.9 s playFanfare — user Feb 2026 asked for a more
     * prominent audio cue at the end of the interpretation.
     *
     * Design: 6 rising melodic notes (C major triad + octave lift) then a
     * final held C-major chord (C6 + E6 + G6) sustained ~1 second. Uses a
     * mix of triangle waves for the melody and sine waves for the chord so
     * the finale feels warm rather than harsh.
     *
     * Accepts either a mascot instance (respects its `soundMuted` flag and
     * reuses its AudioContext) or a `{ audioCtx, muted }` bag when called
     * from a page that has no mascot instance ready yet.
     */
    function playInterpretationFinale(instance) {
        if (!instance || instance.soundMuted) return;
        var ctx = getAudioCtx(instance);
        if (!ctx) return;
        // Chrome/Edge/Firefox lock AudioContext until a user gesture; the
        // Dashboard page is loaded from a form-submit redirect so the user
        // HAS interacted with the site — resume() unblocks a suspended ctx.
        if (ctx.state === 'suspended') {
            try { ctx.resume(); } catch (e) { /* ignore */ }
        }
        try {
            // Six ascending melody notes over ~1.5s: C5 E5 G5 C6 E6 G6.
            var melody = [523.25, 659.25, 783.99, 1046.5, 1318.51, 1567.98];
            melody.forEach(function(freq, i) {
                var t = ctx.currentTime + i * 0.16;
                var o = ctx.createOscillator();
                var g = ctx.createGain();
                o.type = 'triangle';
                o.frequency.setValueAtTime(freq, t);
                g.gain.setValueAtTime(0.0001, t);
                g.gain.exponentialRampToValueAtTime(0.22, t + 0.02);
                g.gain.exponentialRampToValueAtTime(0.0001, t + 0.32);
                o.connect(g).connect(ctx.destination);
                o.start(t);
                o.stop(t + 0.36);
            });
            // Final sustained C major chord starting when the melody ends,
            // held for ~1 second → total duration ≈ 2.5 s.
            var chordStart = ctx.currentTime + melody.length * 0.16 + 0.05;
            var chordFreqs = [1046.5, 1318.51, 1567.98]; // C6, E6, G6
            chordFreqs.forEach(function(freq) {
                var o = ctx.createOscillator();
                var g = ctx.createGain();
                o.type = 'sine';
                o.frequency.setValueAtTime(freq, chordStart);
                g.gain.setValueAtTime(0.0001, chordStart);
                g.gain.exponentialRampToValueAtTime(0.18, chordStart + 0.05);
                g.gain.setValueAtTime(0.18, chordStart + 0.7);
                g.gain.exponentialRampToValueAtTime(0.0001, chordStart + 1.0);
                o.connect(g).connect(ctx.destination);
                o.start(chordStart);
                o.stop(chordStart + 1.05);
            });
        } catch (e) { /* ignore */ }
    }

    function setupSoundToggle(instance) {
        var btn = $('[data-doc-sound-toggle]', instance.root);
        var icon = $('[data-doc-sound-icon]', instance.root);
        if (!btn || !icon) return;
        function updateUi() {
            btn.classList.toggle('muted', instance.soundMuted);
            icon.textContent = instance.soundMuted ? '\uD83D\uDD07' : '\uD83D\uDD0A';
            btn.setAttribute('aria-pressed', instance.soundMuted ? 'true' : 'false');
        }
        btn.addEventListener('click', function() {
            instance.soundMuted = !instance.soundMuted;
            try { localStorage.setItem(STORAGE_KEY, instance.soundMuted ? '1' : '0'); } catch (e) { /* ignore */ }
            updateUi();
            if (!instance.soundMuted) {
                var ctx = getAudioCtx(instance);
                if (ctx && ctx.state === 'suspended') { ctx.resume(); }
                playDing(instance);
            }
        });
        updateUi();
    }

    /**
     * Battery saver: attaches an IntersectionObserver so animations pause
     * when the mascot scrolls out of view. When the mascot re-enters the
     * viewport (or if there's no IntersectionObserver support), animations
     * resume automatically. Toggles the `.doc-mascot--paused` class on the
     * root element — CSS drives the actual `animation-play-state`.
     */
    function setupVisibilityObserver(instance) {
        if (!('IntersectionObserver' in window)) return;
        try {
            instance._visibilityObserver = new IntersectionObserver(function(entries) {
                for (var i = 0; i < entries.length; i++) {
                    var entry = entries[i];
                    if (entry.isIntersecting) {
                        instance.root.classList.remove('doc-mascot--paused');
                    } else {
                        instance.root.classList.add('doc-mascot--paused');
                    }
                }
            }, {
                // Pause a hair before fully off-screen for a smoother edge.
                rootMargin: '0px',
                threshold: 0.01
            });
            instance._visibilityObserver.observe(instance.root);
        } catch (e) { /* graceful fallback */ }
    }

    function ensureConfetti(instance, active) {
        var conf = $('.doc-confetti', instance.root);
        if (!conf) return;
        if (active && conf.childElementCount === 0) {
            var colors = ['#ef4444', '#10b981', '#0ea5e9', '#f59e0b', '#a855f7', '#ec4899'];
            for (var i = 0; i < 18; i++) {
                var s = document.createElement('span');
                s.style.left = (Math.random() * 100) + '%';
                s.style.background = colors[i % colors.length];
                s.style.animationDelay = (Math.random() * 2).toFixed(2) + 's';
                s.style.animationDuration = (1.6 + Math.random() * 1.4).toFixed(2) + 's';
                conf.appendChild(s);
            }
        } else if (!active && conf.childElementCount > 0) {
            conf.innerHTML = '';
        }
    }

    function DoctorMascot(rootEl) {
        this.root = rootEl;
        this.mode = rootEl.getAttribute('data-mode') || 'greeting';
        this.walker = $('[data-doc-walker]', rootEl);
        this.checkpointsEl = $('[data-doc-checkpoints]', rootEl);
        this.soundMuted = (function() {
            try { return localStorage.getItem(STORAGE_KEY) === '1'; }
            catch (e) { return false; }
        })();
        this._lastTotal = -1;
        this._prevProcessed = -1;
        this._jumpResetTimer = null;
        this._finaleTimer = null;
        this._batchEndSoundPlayed = false;
        this._audioCtx = null;
        this._visibilityObserver = null;

        if (this.mode === 'progress' || this.mode === 'waiting') {
            setupSoundToggle(this);
        }
        if (this.mode === 'waiting') {
            // Pornește din start în mers
            this.setState('walking');
        }

        // ---- Battery saver: pause CSS animations when the mascot scrolls
        // off-screen. Uses IntersectionObserver so it is essentially free at
        // runtime (no per-frame checks). Falls back to a no-op on ancient
        // browsers that don't support it.
        setupVisibilityObserver(this);
    }

    DoctorMascot.prototype.setState = function(state) {
        if (!this.walker) return;
        if (this.walker.getAttribute('data-state') !== state) {
            this.walker.setAttribute('data-state', state);
        }
    };

    DoctorMascot.prototype.updateCheckpoints = function(total, processed) {
        if (!this.checkpointsEl) return;
        total = Math.max(0, total | 0);
        processed = Math.max(0, processed | 0);
        if (total !== this._lastTotal) {
            this.checkpointsEl.innerHTML = '';
            var visible = Math.min(total, 30);
            for (var i = 0; i < visible; i++) {
                var d = document.createElement('div');
                d.className = 'doc-checkpoint';
                this.checkpointsEl.appendChild(d);
            }
            this._lastTotal = total;
        }
        var dots = this.checkpointsEl.children;
        var ratio = total > 0 ? (processed / total) : 0;
        var doneVisible = Math.round(ratio * dots.length);
        for (var j = 0; j < dots.length; j++) {
            if (j < doneVisible) dots[j].classList.add('done');
            else dots[j].classList.remove('done');
        }
    };

    /**
     * API public pentru CAM Batch: trimite la fiecare poll.
     * processed/total/status string + finished bool.
     */
    DoctorMascot.prototype.onProgress = function(processed, total, finished, status) {
        var self = this;
        this.updateCheckpoints(total, processed);

        if (finished) {
            if (status === 'Completed') {
                this.setState('celebrate');
                ensureConfetti(this, true);
                if (!this._batchEndSoundPlayed) {
                    playFanfare(this);
                    this._batchEndSoundPlayed = true;
                }
                if (!this._finaleTimer) {
                    this._finaleTimer = setTimeout(function() {
                        self.setState('finale');
                    }, 10000);
                }
            } else {
                this.setState('idle');
                ensureConfetti(this, false);
            }
            this._prevProcessed = processed;
            return;
        }

        if (this._prevProcessed >= 0 && processed > this._prevProcessed) {
            this.setState('jumping');
            playDing(this);
            if (this._jumpResetTimer) clearTimeout(this._jumpResetTimer);
            this._jumpResetTimer = setTimeout(function() {
                if (self.walker.getAttribute('data-state') === 'jumping') {
                    self.setState('walking');
                }
            }, 1900);
        } else if (this.walker.getAttribute('data-state') !== 'jumping') {
            this.setState(status === 'Running' ? 'walking' : 'idle');
        }
        this._prevProcessed = processed;
    };

    // Auto-init pentru toate .doc-mascot din pagină
    function autoInit() {
        $$('.doc-mascot').forEach(function(el) {
            if (!el._docMascot) {
                el._docMascot = new DoctorMascot(el);
            }
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', autoInit);
    } else {
        autoInit();
    }

    // Expune API global
    window.DoctorMascot = {
        get: function(id) {
            var el = typeof id === 'string' ? document.getElementById(id) : id;
            return el && el._docMascot ? el._docMascot : null;
        },
        init: function(el) {
            if (!el._docMascot) el._docMascot = new DoctorMascot(el);
            return el._docMascot;
        },
        /**
         * Standalone finale sound (~2.5s) triggered by the B2C Dashboard when
         * arriving from a successful /Interpretation/Upload POST. Doesn't need
         * a mascot instance — it looks up an existing one on the page (to
         * respect the persisted `soundMuted` preference), and if none exists
         * it falls back to a temporary throw-away context. Silent-safe: if
         * the browser blocks autoplay the try/catch swallows the error.
         */
        playInterpretationFinishSound: function() {
            var any = null;
            var nodes = $$('.doc-mascot');
            for (var i = 0; i < nodes.length; i++) {
                if (nodes[i]._docMascot) { any = nodes[i]._docMascot; break; }
            }
            if (any) {
                playInterpretationFinale(any);
                return;
            }
            // Fallback: build a minimal instance-shaped bag so the helper
            // can create/use an AudioContext without a mascot on the page.
            var bag = { soundMuted: false, _audioCtx: null };
            try {
                bag.soundMuted = localStorage.getItem(STORAGE_KEY) === '1';
            } catch (e) { /* ignore */ }
            playInterpretationFinale(bag);
        }
    };
})();
