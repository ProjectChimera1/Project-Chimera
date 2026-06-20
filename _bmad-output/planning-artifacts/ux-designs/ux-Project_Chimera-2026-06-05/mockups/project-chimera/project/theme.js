/* Project Chimera — shared theme + emblem helper.
   - Applies persisted theme ('light' | 'dark') and accent ('teal'|'amber'|'violet') on load.
   - chimeraToggleTheme() / chimeraSetAccent(a) persist + apply.
   - Injects the "Chimera Mark" — a geometric transmutation sigil — as an SVG sprite:
       <svg class="..."><use href="#chimera-seal"></use></svg>   (full sigil: two rings, fire/water triangles, nucleus, vertex nodes)
       <svg class="..."><use href="#chimera-triad"></use></svg>  (heavy-stroke small variant for < 24px — favicon scale)
   Construction is pure geometry: two concentric rings contain the working;
   up-triangle (fire) is the dominant form, down-triangle (water) the ghosted
   counter-form — two natures fused, the chimera idea. Solid center node =
   the transmutation nucleus; three vertex nodes anchor the dominant form.
   Monochrome-in-one-hue so it engraves onto any surface in the system.
   Original alchemical-geometry design — not derived from any existing IP mark. */
(function () {
  var root = document.documentElement;
  try {
    var t = localStorage.getItem('chimera-theme');
    if (t === 'light') root.setAttribute('data-theme', 'light');
    var a = localStorage.getItem('chimera-accent');
    if (a === 'teal' || a === 'amber' || a === 'violet') root.setAttribute('data-accent', a);
  } catch (e) {}

  window.chimeraToggleTheme = function () {
    var light = root.getAttribute('data-theme') === 'light';
    if (light) { root.removeAttribute('data-theme'); } else { root.setAttribute('data-theme', 'light'); }
    try { localStorage.setItem('chimera-theme', light ? 'dark' : 'light'); } catch (e) {}
    document.dispatchEvent(new CustomEvent('chimera-theme', { detail: light ? 'dark' : 'light' }));
  };
  window.chimeraSetAccent = function (a) {
    root.setAttribute('data-accent', a);
    try { localStorage.setItem('chimera-accent', a); } catch (e) {}
    document.dispatchEvent(new CustomEvent('chimera-accent', { detail: a }));
  };

  function injectSprite() {
    if (document.getElementById('chimera-sprite')) return;
    var div = document.createElement('div');
    div.innerHTML =
      '<svg id="chimera-sprite" xmlns="http://www.w3.org/2000/svg" style="position:absolute;width:0;height:0;overflow:hidden" aria-hidden="true">' +
      '<symbol id="chimera-triad" viewBox="0 0 48 48">' +
        /* heavy-stroke variant for small sizes (≤24px): ring + fire triangle + nucleus */
        '<circle cx="24" cy="24" r="20" fill="none" stroke="var(--accent)" stroke-width="2.6"></circle>' +
        '<path d="M24 9.5 L36.5 31 L11.5 31 Z" fill="none" stroke="var(--accent)" stroke-width="2.6" stroke-linejoin="miter"></path>' +
        '<circle cx="24" cy="24" r="4.4" fill="var(--accent)"></circle>' +
      '</symbol>' +
      '<symbol id="chimera-seal" viewBox="0 0 96 96">' +
        /* two concentric rings — the working circle + inner guide */
        '<circle cx="48" cy="48" r="42" fill="none" stroke="var(--accent)" stroke-width="2.8"></circle>' +
        '<circle cx="48" cy="48" r="33" fill="none" stroke="var(--accent-dim)" stroke-width="2"></circle>' +
        /* dominant up-triangle (fire) */
        '<path d="M48 16 L75.72 64 L20.28 64 Z" fill="none" stroke="var(--accent)" stroke-width="2.8" stroke-linejoin="miter"></path>' +
        /* ghosted down-triangle (water) — counter-form */
        '<path d="M48 80 L20.28 32 L75.72 32 Z" fill="none" stroke="var(--accent-dim)" stroke-width="2" stroke-linejoin="miter"></path>' +
        /* solid center node — transmutation nucleus */
        '<circle cx="48" cy="48" r="7.2" fill="var(--accent)"></circle>' +
        /* three anchor nodes at the dominant triangle\'s vertices */
        '<circle cx="48" cy="16" r="3.8" fill="var(--accent-bright)"></circle>' +
        '<circle cx="75.72" cy="64" r="3.8" fill="var(--accent-bright)"></circle>' +
        '<circle cx="20.28" cy="64" r="3.8" fill="var(--accent-bright)"></circle>' +
      '</symbol>' +
      /* --- transmute spinner layers (96 viewBox, share seal geometry) --- */
      '<symbol id="chimera-spin-ring" viewBox="0 0 96 96">' +
        '<circle cx="48" cy="48" r="44" fill="none" stroke="var(--accent-dim)" stroke-width="2" opacity="0.4"></circle>' +
        '<circle cx="48" cy="48" r="44" fill="none" stroke="var(--accent)" stroke-width="2.5" stroke-dasharray="60 216" stroke-linecap="square"></circle>' +
        '<g stroke="var(--accent)" stroke-width="2" opacity="0.85">' +
          '<path d="M48 0 L48 7"></path><path d="M48 89 L48 96"></path><path d="M0 48 L7 48"></path><path d="M89 48 L96 48"></path>' +
        '</g>' +
      '</symbol>' +
      '<symbol id="chimera-spin-tri" viewBox="0 0 96 96">' +
        /* fire / water pair with vertex anchors — rotates as one body */
        '<path d="M48 19 L73.4 63 L22.6 63 Z" fill="none" stroke="var(--accent)" stroke-width="2.4" stroke-linejoin="miter"></path>' +
        '<path d="M48 77 L22.6 33 L73.4 33 Z" fill="none" stroke="var(--accent-dim)" stroke-width="1.7" stroke-linejoin="miter" opacity="0.75"></path>' +
        '<circle cx="48" cy="19" r="3.2" fill="var(--accent-bright)"></circle>' +
        '<circle cx="73.4" cy="63" r="3.2" fill="var(--accent-bright)"></circle>' +
        '<circle cx="22.6" cy="63" r="3.2" fill="var(--accent-bright)"></circle>' +
      '</symbol>' +
      '<symbol id="chimera-spin-core" viewBox="0 0 96 96">' +
        /* solid nucleus */
        '<circle cx="48" cy="48" r="6.4" fill="var(--accent)"></circle>' +
      '</symbol>' +
      /* --- grand seal layers — large watermark with animated dashed rings.
         Stack three svgs (.gs-base / .gs-dash-a / .gs-dash-b) and rotate the
         dash layers in CSS; base stays static. --- */
      '<symbol id="chimera-grand-base" viewBox="0 0 192 192">' +
        '<circle cx="96" cy="96" r="82" fill="none" stroke="var(--accent)" stroke-width="1.6"></circle>' +
        '<path d="M96 22 L160 133 L32 133 Z" fill="none" stroke="var(--accent)" stroke-width="1.6" stroke-linejoin="miter"></path>' +
        '<path d="M96 170 L32 59 L160 59 Z" fill="none" stroke="var(--accent-dim)" stroke-width="1.2" stroke-linejoin="miter"></path>' +
        '<circle cx="96" cy="22" r="5" fill="var(--accent-bright)"></circle>' +
        '<circle cx="160" cy="133" r="5" fill="var(--accent-bright)"></circle>' +
        '<circle cx="32" cy="133" r="5" fill="var(--accent-bright)"></circle>' +
        '<circle cx="96" cy="170" r="4" fill="var(--accent-dim)"></circle>' +
        '<circle cx="32" cy="59" r="4" fill="var(--accent-dim)"></circle>' +
        '<circle cx="160" cy="59" r="4" fill="var(--accent-dim)"></circle>' +
        '<circle cx="96" cy="96" r="38" fill="none" stroke="var(--accent)" stroke-width="1.3"></circle>' +
        '<path d="M96 58 L134 96 L96 134 L58 96 Z" fill="none" stroke="var(--accent-dim)" stroke-width="1.1"></path>' +
        '<circle cx="96" cy="96" r="9" fill="var(--accent)"></circle>' +
      '</symbol>' +
      '<symbol id="chimera-grand-dash-a" viewBox="0 0 192 192">' +
        '<circle cx="96" cy="96" r="92" fill="none" stroke="var(--accent)" stroke-width="1.2" stroke-dasharray="3 9" opacity="0.8"></circle>' +
      '</symbol>' +
      '<symbol id="chimera-grand-dash-b" viewBox="0 0 192 192">' +
        '<circle cx="96" cy="96" r="50" fill="none" stroke="var(--accent)" stroke-width="1.1" stroke-dasharray="2 10" opacity="0.75"></circle>' +
      '</symbol>' +
      '</svg>';
    document.body.insertBefore(div.firstChild, document.body.firstChild);
  }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', injectSprite);
  } else { injectSprite(); }
})();
