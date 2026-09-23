/*  MyMedicalApp — auditor de layout & contrast (rulare in browser)
 *  =================================================================
 *  Instrument de DIAGNOSTIC, pur client-side: doar CITESTE geometria si
 *  culorile elementelor din pagina deja incarcata. Nu trimite nimic nicaieri,
 *  nu modifica DOM-ul, nu atinge datele.
 *
 *  CUM SE FOLOSESTE
 *  1. Deschide pagina pe care vrei sa o verifici (ex. Dashboard) si logheaza-te.
 *  2. F12 -> tab "Console".
 *  3. Lipeste linia urmatoare si apasa Enter:
 *
 *       var s=document.createElement('script');s.src='/dev/ui-audit.js';document.body.appendChild(s);
 *
 *  4. Pentru fiecare latime: Ctrl+Shift+M (device toolbar), scrie latimea
 *     (1280, 980, 768, 390), apoi ruleaza din nou:  uiAudit()
 *
 *  Raportul se afiseaza in consola SI se copiaza in clipboard.
 */
(function () {
    'use strict';

    const CAND = 'a,button,h1,h2,h3,h4,h5,h6,p,li,strong,span,small,label,td,th,option';
    const EMOJI = /[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}\u{FE0F}]/u;
    /* Decorative constructs that overlap / leave the frame BY DESIGN:
         .land-deck      - the fanned-out stack of report cards in the hero
         .doc-walker     - the walking-doctor animation, which slides in and out
         .doc-bouncer      of the hero on purpose (clipped by an ancestor, so it
                           never creates a real horizontal scrollbar)
       Reporting these produces permanent false alarms, so they are skipped. */
    const IGNORE = '.land-deck, .doc-walker, .doc-bouncer, [aria-hidden="true"]';
    const decorative = (e) => e.closest(IGNORE) !== null || !(e instanceof HTMLElement);

    const parse = (c) => {
        const p = (String(c).match(/[\d.]+/g) || []).map(Number);
        return { r: p[0] || 0, g: p[1] || 0, b: p[2] || 0, a: p.length > 3 ? p[3] : 1 };
    };
    const lin = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
    const lum = (o) => 0.2126 * lin(o.r) + 0.7152 * lin(o.g) + 0.0722 * lin(o.b);
    const over = (f, b) => ({
        r: f.r * f.a + b.r * (1 - f.a), g: f.g * f.a + b.g * (1 - f.a),
        b: f.b * f.a + b.b * (1 - f.a), a: 1
    });
    const ratio = (fg, bg) => {
        const l1 = lum(fg), l2 = lum(bg), hi = Math.max(l1, l2), lo = Math.min(l1, l2);
        return (hi + 0.05) / (lo + 0.05);
    };

    // Composites every translucent ancestor layer. Returns null when a gradient
    // or background image is involved — those cannot be measured numerically and
    // would produce false alarms (e.g. white text on a green gradient badge).
    const bgOf = (el) => {
        const stack = [];
        let n = el;
        while (n && n.nodeType === 1) {
            const cs = getComputedStyle(n);
            if (cs.backgroundImage && cs.backgroundImage !== 'none') return null;
            const c = parse(cs.backgroundColor);
            if (c.a > 0) stack.push(c);
            n = n.parentElement;
        }
        let acc = { r: 255, g: 255, b: 255, a: 1 };
        for (let i = stack.length - 1; i >= 0; i--) acc = over(stack[i], acc);
        return acc;
    };

    const label = (e) => {
        let s = e.tagName.toLowerCase();
        if (e.id) s += '#' + e.id;
        const c = (e.getAttribute('class') || '').trim().split(/\s+/).filter(Boolean).slice(0, 3);
        if (c.length) s += '.' + c.join('.');
        const t = e.getAttribute('data-testid');
        if (t) s += '[' + t + ']';
        return s;
    };

    const visible = (e) => {
        const cs = getComputedStyle(e);
        if (cs.display === 'none' || cs.visibility === 'hidden' || parseFloat(cs.opacity) < 0.2) return false;
        const r = e.getBoundingClientRect();
        return r.width > 1 && r.height > 1;
    };

    window.uiAudit = function uiAudit(opts) {
        opts = opts || {};
        const skipOverlap = opts.skipOverlap === true;
        const vw = document.documentElement.clientWidth;
        const res = { url: location.pathname, vw, overflow: [], overlaps: [], contrast: [], unmeasurable: 0 };

        const all = [...document.querySelectorAll('body *')].filter(visible);

        // 1) anything sticking out past the viewport = horizontal scrollbar
        for (const e of all) {
            if (decorative(e)) continue;
            const r = e.getBoundingClientRect();
            if (r.right > vw + 2 || r.left < -2) {
                res.overflow.push(`${label(e)}  [${Math.round(r.left)}..${Math.round(r.right)}] w=${Math.round(r.width)}`);
            }
        }
        res.hScroll = document.documentElement.scrollWidth > vw + 2;

        // 2) two independent text blocks sitting on top of each other
        const texts = all.filter(e =>
            e.matches(CAND) && e.innerText && e.innerText.trim().length > 1 &&
            !e.querySelector(CAND) && !decorative(e));
        if (!skipOverlap) {
            for (let i = 0; i < texts.length; i++) {
                for (let j = i + 1; j < texts.length; j++) {
                    const A = texts[i], B = texts[j];
                    if (A.contains(B) || B.contains(A)) continue;
                    const a = A.getBoundingClientRect(), b = B.getBoundingClientRect();
                    const ix = Math.min(a.right, b.right) - Math.max(a.left, b.left);
                    const iy = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
                    if (ix <= 2 || iy <= 2) continue;
                    const pct = Math.round(100 * ix * iy / Math.min(a.width * a.height, b.width * b.height));
                    if (pct < 30) continue;
                    res.overlaps.push(`${pct}%  ${label(A)} >< ${label(B)}   "${A.innerText.trim().slice(0, 30)}" / "${B.innerText.trim().slice(0, 30)}"`);
                }
            }
        }

        // 3) text contrast against the real composited background (WCAG AA)
        for (const e of texts) {
            const txt = e.innerText.trim();
            if (EMOJI.test(txt) && txt.length <= 3) { res.unmeasurable++; continue; }
            const bg = bgOf(e);
            if (!bg) { res.unmeasurable++; continue; }
            const cs = getComputedStyle(e);
            const r = ratio(parse(cs.color), bg);
            const size = parseFloat(cs.fontSize), bold = parseInt(cs.fontWeight, 10) >= 700;
            const floor = (size >= 24 || (size >= 18.66 && bold)) ? 3 : 4.5;
            if (r < floor - 0.05) {
                res.contrast.push(`${r.toFixed(2)}/${floor}  ${label(e)}  color=${cs.color}  "${txt.slice(0, 30)}"`);
            }
        }

        const uniq = (a) => [...new Set(a)];
        res.overflow = uniq(res.overflow);
        res.overlaps = uniq(res.overlaps);
        res.contrast = uniq(res.contrast);

        const lines = [];
        lines.push(`=== UI AUDIT  ${res.url}  @ ${vw}px ===`);
        lines.push(`bara de derulare orizontala : ${res.hScroll ? 'DA  <-- PROBLEMA' : 'nu'}`);
        lines.push(`elemente in afara cadrului  : ${res.overflow.length}`);
        res.overflow.slice(0, 15).forEach(x => lines.push('    OVF  ' + x));
        lines.push(`suprapuneri de text         : ${res.overlaps.length}`);
        res.overlaps.slice(0, 15).forEach(x => lines.push('    OVL  ' + x));
        lines.push(`texte sub pragul WCAG AA    : ${res.contrast.length}`);
        res.contrast.slice(0, 20).forEach(x => lines.push('    CTR  ' + x));
        lines.push(`nemasurabile (gradient/emoji): ${res.unmeasurable}`);
        const report = lines.join('\n');

        console.log(report);
        if (navigator.clipboard) {
            navigator.clipboard.writeText(report).then(
                () => console.log('%c(raportul a fost copiat in clipboard)', 'color:#00693C'),
                () => { });
        }
        return res;
    };

    console.log('%cui-audit incarcat. Ruleaza:  uiAudit()', 'color:#00693C;font-weight:700');
    window.uiAudit();
})();
