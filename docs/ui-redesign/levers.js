// Chimera ornament + motion levers (Round 2). window.CHI_LV(React, props, {replay, rm, mode}) returns ready elements.
// Every lever: on/off + level 1–5, scaled by master (default 5, approved Round 2). Motion is 0 under Reduced motion. Player option Off/Subtle/Full caps all.
(function () {
  var G = '#C9A86A', GB = '#E3C887', GD = '#8A6E3C', SUB = '#14161A', BIO = '#7FE3A1';
  if (typeof document !== 'undefined' && !document.getElementById('chi-lv-kf')) {
    var s = document.getElementById('chi-lv-kf') || document.createElement('style'); s.id = 'chi-lv-kf';
    s.textContent = '@keyframes chiRevL{from{clip-path:inset(0 100% 0 0)}to{clip-path:inset(0 0 0 0)}}@keyframes chiRevR{from{clip-path:inset(0 0 0 100%)}to{clip-path:inset(0 0 0 0)}}@keyframes chiRevT{from{clip-path:inset(0 0 100% 0)}to{clip-path:inset(0 0 0 0)}}@keyframes chiRevB{from{clip-path:inset(100% 0 0 0)}to{clip-path:inset(0 0 0 0)}}@keyframes chiRevC{from{clip-path:inset(0 50% 0 50%)}to{clip-path:inset(0 0 0 0)}}@keyframes chiRevTL{from{clip-path:inset(0 100% 100% 0)}to{clip-path:inset(0 0 0 0)}}@keyframes chiFade{from{opacity:0}}@keyframes chiSigIn{from{opacity:0;transform:rotate(-120deg) scale(.5)}}@keyframes chiPulse{0%,100%{opacity:var(--lo)}50%{opacity:var(--hi)}}@keyframes chiRunX{0%{background-position:-40% 0}20%,100%{background-position:140% 0}}@keyframes chiRunXr{0%{background-position:140% 0}20%,100%{background-position:-40% 0}}@keyframes chiRunY{0%{background-position:0 -40%}20%,100%{background-position:0 140%}}@keyframes chiRunYr{0%{background-position:0 140%}20%,100%{background-position:0 -40%}}@keyframes chiEtchX{0%{clip-path:inset(0 100% 0 0);opacity:1}20%{clip-path:inset(0 0 0 0);opacity:1}55%{opacity:.8}92%,100%{clip-path:inset(0 0 0 0);opacity:0}}@keyframes chiEtchXr{0%{clip-path:inset(0 0 0 100%);opacity:1}20%{clip-path:inset(0 0 0 0);opacity:1}55%{opacity:.8}92%,100%{clip-path:inset(0 0 0 0);opacity:0}}@keyframes chiEtchY{0%{clip-path:inset(0 0 100% 0);opacity:1}20%{clip-path:inset(0 0 0 0);opacity:1}55%{opacity:.8}92%,100%{clip-path:inset(0 0 0 0);opacity:0}}@keyframes chiEtchYr{0%{clip-path:inset(100% 0 0 0);opacity:1}20%{clip-path:inset(0 0 0 0);opacity:1}55%{opacity:.8}92%,100%{clip-path:inset(0 0 0 0);opacity:0}}@keyframes chiShim{0%{background-position:-60% 0}40%,100%{background-position:160% 0}}@keyframes chiTurn{to{transform:rotate(360deg)}}@keyframes chiTurnR{to{transform:rotate(-360deg)}}@keyframes chiSeal{0%{opacity:0;transform:scale(var(--s0))}60%{opacity:1;transform:scale(.97)}100%{opacity:1;transform:scale(1)}}@keyframes chiSealRing{0%{opacity:0;transform:scale(.92)}35%{opacity:var(--hi)}100%{opacity:0;transform:scale(1.4)}}';
    document.head.appendChild(s);
  }
  function enc(svg) { return 'url("data:image/svg+xml,' + svg.replace(/#/g, '%23').replace(/</g, '%3C').replace(/>/g, '%3E').replace(/"/g, "'") + '")'; }
  var RH = "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='8' viewBox='0 0 16 8'><path d='M0 4C2.6 7.8 5.4 7.8 8 4S13.4 .2 16 4' fill='none' stroke='#8A6E3C' stroke-width='1.1'/><path d='M0 4C2.6 .2 5.4 .2 8 4S13.4 7.8 16 4' fill='none' stroke='#E3C887' stroke-width='1.1'/><line x1='6.4' y1='6.2' x2='9.6' y2='1.8' stroke='#14161A' stroke-width='2.6'/><line x1='6.4' y1='6.2' x2='9.6' y2='1.8' stroke='#8A6E3C' stroke-width='1.1'/></svg>";
  var RV = "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='16' viewBox='0 0 8 16'><path d='M4 0C7.8 2.6 7.8 5.4 4 8S.2 13.4 4 16' fill='none' stroke='#8A6E3C' stroke-width='1.1'/><path d='M4 0C.2 2.6 .2 5.4 4 8S7.8 13.4 4 16' fill='none' stroke='#E3C887' stroke-width='1.1'/><line y1='6.4' x1='6.2' y2='9.6' x2='1.8' stroke='#14161A' stroke-width='2.6'/><line y1='6.4' x1='6.2' y2='9.6' x2='1.8' stroke='#8A6E3C' stroke-width='1.1'/></svg>";
  var ROPE_H = enc(RH), ROPE_V = enc(RV);

  window.CHI_LV_NOTES = {
    border: 'One 9-slice border brush per style, swapped by an enum; intensity is the brush tint alpha.',
    rope: 'Tiled rope texture on four edge images; a linear opacity mask sets how far it runs from each corner.',
    growth: 'Material scalar "Reveal" 0→1 on the edge images, keyed in a UMG animation when the panel opens.',
    sigils: 'Icons from one sigil atlas placed in fixed slots; the count only toggles slot visibility.',
    pulse: 'Looping UMG animation on a halo image’s opacity, sine curve, period 2.4–6 s.',
    shimmer: 'Panner node runs a soft gradient head around the border UVs (19–40 s per lap); on hover a Reveal mask etches a bright, slowly fading line behind it.',
    turn: 'Render-transform angle on a ring image, looped linear UMG animation, 32–160 s per turn.',
    draw: 'The same Reveal scalar driven on Construct, staggered per panel; low levels only fade opacity.',
    hover: 'OnHovered lerps the border tint alpha and the rope tile scale over 0.18–0.6 s.',
    seal: 'One-shot UMG animation: scale 1.1–1.5 → 1 with opacity, plus a fading ring image.',
    r1: 'Static images from the Round 1 kit; intensity picks the image variant and tint alpha.',
    rm: 'Reduced motion sets every widget animation’s play rate to 0 and Reveal to 1: ornaments stay, still.'
  };

  // Approved Round 2 defaults (Browse review): used whenever a prop is unset.
  window.CHI_LV_DEFAULTS = {"lvBorderStyle":"Woven rope","lvPulseSpeed":1,"lvPulseStr":5,"lvShimmerSpeed":5,"lvShimmerStr":5,"lvTurnSpeed":5,"lvDrawSpeed":1,"lvDrawStr":5,"lvHoverSpeed":1,"lvSealSpeed":1,"lvSealStr":1,"ornCornersLvl":3};
  window.CHI_LV = function (React, p, x) {
    p = Object.assign({}, window.CHI_LV_DEFAULTS, p || {}); x = x || {};
    var e = React.createElement, b = function (v, d) { return v === undefined || v === null ? d : v; };
    var master = b(p.lvMaster, 5), mode = x.mode || b(p.uiOrnament, 'Full'), rm = !!(x.rm || p.reducedMotion);
    var n = x.replay || 0, kc = 0; function K(s) { return (s || 'l') + n + '_' + (kc++); }
    function lvl(on, v) { if (!b(on, true) || mode === 'Off') return 0; var r = Math.round(b(v, 2) * master / 2); r = Math.max(1, Math.min(5, r)); return mode === 'Subtle' ? Math.min(r, 2) : r; }
    function mot(on, v) { return rm ? 0 : lvl(on, v); }
    var c = {
      corners: lvl(p.ornCorners, b(p.ornCornersLvl, 3)), headers: lvl(p.ornHeaders, p.ornHeadersLvl), dividers: lvl(p.ornDividers, p.ornDividersLvl),
      texture: lvl(p.ornTexture, p.ornTextureLvl), glow: lvl(p.ornGlow, p.ornGlowLvl), topBar: lvl(p.ornTopBar, p.ornTopBarLvl),
      rail: lvl(p.ornRail, p.ornRailLvl), buttons: lvl(p.ornButtons, p.ornButtonsLvl), minimap: lvl(p.ornMinimap, p.ornMinimapLvl),
      border: lvl(p.lvBorder, p.lvBorderLvl), borderStyle: b(p.lvBorderStyle, 'Engraved line'), rope: lvl(p.lvRope, p.lvRopeLvl),
      growth: mot(p.lvGrowth, p.lvGrowthLvl), sigil: lvl(p.lvSigil, p.lvSigilLvl), sigilSet: b(p.lvSigilSet, 'Seals'),
      pulseS: mot(p.lvPulse, p.lvPulseSpeed), pulseA: mot(p.lvPulse, p.lvPulseStr), shimS: mot(p.lvShimmer, p.lvShimmerSpeed), shimA: mot(p.lvShimmer, p.lvShimmerStr),
      turnS: mot(p.lvTurn, p.lvTurnSpeed), turnA: mot(p.lvTurn, p.lvTurnStr), drawS: mot(p.lvDraw, p.lvDrawSpeed), drawA: mot(p.lvDraw, p.lvDrawStr),
      hovS: mot(p.lvHover, p.lvHoverSpeed), hovA: mot(p.lvHover, p.lvHoverStr), sealS: mot(p.lvSeal, p.lvSealSpeed), sealA: mot(p.lvSeal, p.lvSealStr),
      mode: mode, rm: rm, master: master
    };
    function abs(st, kids, k) { return e('div', { key: k || K(), style: Object.assign({ position: 'absolute', pointerEvents: 'none' }, st) }, kids); }
    function R(flag, L) { return L && window.CHI_ORN ? window.CHI_ORN(React, Object.assign({ intensity: L, glow: c.glow > 0 }, flag)) : {}; }
    var OFF = window.CHI_ORN ? window.CHI_ORN(React, { intensity: 2 }) : {};
    var oc = R({ corners: true }, c.corners), oh = R({ headers: true }, c.headers), ot = R({ texture: true }, c.texture), otb = R({ topBar: true }, c.topBar),
      ora = R({ rail: true }, c.rail), obt = R({ buttons: true }, c.buttons), omm = R({ minimap: true, inspector: true }, c.minimap || 2);

    var drawDur = [0, 2.4, 1.8, 1.3, 1, .7][c.drawS];
    function drawAnim(dir, delay) { if (!c.drawS) return {}; return { animation: (c.drawA <= 2 ? 'chiFade' : 'chiRev' + dir) + ' ' + drawDur + 's cubic-bezier(.3,.6,.3,1) ' + (delay || 0) + 's both' }; }
    var gDur = [0, 3.4, 2.6, 1.9, 1.4, 1][c.growth];
    var hA = [0, .18, .28, .38, .5, .65][c.hovA], hT = [.3, .6, .45, .35, .25, .18][c.hovS];
    function hovOp(base) { return hA ? 'calc(' + base + ' + var(--chiH, 0) * ' + hA + ')' : base; }
    var trans = 'opacity ' + hT + 's ease, background-size ' + hT + 's ease';

    function node(r, fill) { return e('svg', { key: K('n'), width: r * 2 + 2, height: r * 2 + 2, viewBox: '0 0 ' + (r * 2 + 2) + ' ' + (r * 2 + 2), style: { display: 'block', flex: 'none' } }, e('circle', { cx: r + 1, cy: r + 1, r: r, fill: fill || SUB, stroke: G, strokeWidth: 1 })); }
    function hexa(cx, cy, r, sw, col) {
      var a = [], bb = [];
      for (var i = 0; i < 3; i++) { var t = (-90 + i * 120) * Math.PI / 180, u = (90 + i * 120) * Math.PI / 180; a.push((cx + r * Math.cos(t)).toFixed(2) + ',' + (cy + r * Math.sin(t)).toFixed(2)); bb.push((cx + r * Math.cos(u)).toFixed(2) + ',' + (cy + r * Math.sin(u)).toFixed(2)); }
      return [e('polygon', { key: 'h1', points: a.join(' '), stroke: col, strokeWidth: sw, fill: 'none' }), e('polygon', { key: 'h2', points: bb.join(' '), stroke: col, strokeWidth: sw, fill: 'none' })];
    }
    function tri(cx, cy, r, down, col, sw) { var pts = [0, 1, 2].map(function (i) { var t = ((down ? 90 : -90) + i * 120) * Math.PI / 180; return (cx + r * Math.cos(t)).toFixed(2) + ',' + (cy + r * Math.sin(t)).toFixed(2); }); return e('polygon', { key: 't' + (down ? 'd' : 'u'), points: pts.join(' '), fill: 'none', stroke: col, strokeWidth: sw }); }
    function sigil(size, i, col, set) {
      i = i || 0; col = col || G; set = set || c.sigilSet; var k = [e('circle', { key: 'o', cx: 10, cy: 10, r: 9, fill: SUB, stroke: col, strokeWidth: 1 })];
      if (set === 'Circles') {
        k.push(e('circle', { key: 'a', cx: 10, cy: 10, r: 6.3, fill: 'none', stroke: GD, strokeWidth: .8, strokeDasharray: '.8 1.4' }), e('circle', { key: 'b', cx: 10, cy: 10, r: 3.6, fill: 'none', stroke: col, strokeWidth: .8 }));
        if (i % 3 === 0) k.push(e('circle', { key: 'c', cx: 10, cy: 10, r: 1.3, fill: GB }));
        if (i % 3 === 1) k.push(e('line', { key: 'c', x1: 10, y1: 1, x2: 10, y2: 19, stroke: GD, strokeWidth: .7 }), e('line', { key: 'd', x1: 1, y1: 10, x2: 19, y2: 10, stroke: GD, strokeWidth: .7 }));
        if (i % 3 === 2) [0, 1, 2].forEach(function (j) { var t = (-90 + j * 120) * Math.PI / 180; k.push(e('circle', { key: 'q' + j, cx: 10 + 6.3 * Math.cos(t), cy: 10 + 6.3 * Math.sin(t), r: 1.5, fill: SUB, stroke: col, strokeWidth: .7 })); });
      } else if (set === 'Triangles') {
        var down = i % 2 === 1; k.push(tri(10, 10.6 - (down ? 1.2 : 0), 6.4, down, col, .9));
        if (i % 4 >= 2) k.push(e('line', { key: 'bar', x1: 6.4, y1: down ? 7.6 : 12.4, x2: 13.6, y2: down ? 7.6 : 12.4, stroke: col, strokeWidth: .8 }));
        k.push(e('circle', { key: 'd', cx: 10, cy: 10, r: 7.6, fill: 'none', stroke: GD, strokeWidth: .6, strokeDasharray: '.6 1.6' }));
      } else {
        k.push(e('circle', { key: 'a', cx: 10, cy: 10, r: 7.7, fill: 'none', stroke: GD, strokeWidth: .7, strokeDasharray: '.7 1.3' }));
        k = k.concat(hexa(10, 10, 6.3, .8, col));
        var m = i % 4;
        if (m === 0) k.push(e('circle', { key: 'c', cx: 10, cy: 10, r: 1.9, fill: 'none', stroke: GB, strokeWidth: .8 }));
        if (m === 1) k.push(e('path', { key: 'c', d: 'M7.8 10Q10 7.2 12.2 10Q10 12.8 7.8 10Z', fill: 'none', stroke: GB, strokeWidth: .7 }), e('circle', { key: 'd', cx: 10, cy: 10, r: .7, fill: GB }));
        if (m === 2) k.push(e('path', { key: 'c', d: 'M10 7.6C11.6 9.4 11.8 10.6 10 12.2C8.2 10.6 8.4 9.4 10 7.6Z', fill: 'none', stroke: GB, strokeWidth: .7 }));
        if (m === 3) k.push(e('circle', { key: 'c', cx: 10, cy: 10, r: 1.3, fill: GB }));
      }
      return e('svg', { key: K('s'), width: size, height: size, viewBox: '0 0 20 20', style: { display: 'block', flex: 'none' } }, k);
    }
    function sigAnim(delay) { if (!c.drawS) return {}; return { animation: (c.drawA >= 4 ? 'chiSigIn' : 'chiFade') + ' ' + drawDur + 's cubic-bezier(.3,.6,.3,1) ' + (delay || 0) + 's both' }; }

    function ropeSegs(L, op, full) {
      var len = full ? '50%' : [0, 22, 44, 90, '30%', '50%'][L], tight = [0, 1, 2, 3, 4, 5][c.hovA];
      var tile = tight ? 'calc(16px - var(--chiH, 0) * ' + tight + 'px)' : '16px';
      function mk(pos, vert, dir, fade, bp) {
        var st = Object.assign({ position: 'absolute', pointerEvents: 'none', zIndex: 2, opacity: hovOp(op), transition: trans, backgroundImage: vert ? ROPE_V : ROPE_H, backgroundRepeat: vert ? 'repeat-y' : 'repeat-x', backgroundSize: vert ? '8px ' + tile : tile + ' 8px', backgroundPosition: bp || '0 0' }, pos);
        if (vert) { st.width = 8; st.height = len; } else { st.height = 8; st.width = len; }
        if (len !== '50%') { var m = 'linear-gradient(' + fade + ', #000 55%, transparent)'; st.maskImage = m; st.WebkitMaskImage = m; }
        if (gDur) st.animation = 'chiRev' + dir + ' ' + gDur + 's cubic-bezier(.25,.6,.3,1) both';
        return e('div', { key: K('r'), style: st });
      }
      return [mk({ top: -4, left: 0 }, 0, 'L', 'to right'), mk({ top: -4, right: 0 }, 0, 'R', 'to left', 'right 0'), mk({ bottom: -4, left: 0 }, 0, 'L', 'to right'), mk({ bottom: -4, right: 0 }, 0, 'R', 'to left', 'right 0'),
        mk({ left: -4, top: 0 }, 1, 'T', 'to bottom'), mk({ left: -4, bottom: 0 }, 1, 'B', 'to top', '0 100%'), mk({ right: -4, top: 0 }, 1, 'T', 'to bottom'), mk({ right: -4, bottom: 0 }, 1, 'B', 'to top', '0 100%')];
    }
    function filigree(L, op) {
      var s = [0, 28, 40, 52, 64, 76][L];
      function svg() {
        var k = [e('path', { key: 1, d: 'M2 62L2 14Q2 2 14 2L62 2', fill: 'none', stroke: G, strokeWidth: 1 }),
          e('path', { key: 2, d: 'M14 2C14 11 22 13 24 18C25.5 22 21 24.5 18.5 22C17 20.5 18.5 18.5 20 19.5', fill: 'none', stroke: G, strokeWidth: .9 }),
          e('path', { key: 3, d: 'M2 14C11 14 13 22 18 24C22 25.5 24.5 21 22 18.5C20.5 17 18.5 18.5 19.5 20', fill: 'none', stroke: G, strokeWidth: .9 }),
          e('circle', { key: 4, cx: 62, cy: 2, r: 1.6, fill: G }), e('circle', { key: 5, cx: 2, cy: 62, r: 1.6, fill: G })];
        if (L >= 3) k.push(e('path', { key: 6, d: 'M7 50L7 17Q7 7 17 7L50 7', fill: 'none', stroke: GD, strokeWidth: .8 }));
        if (L >= 4) k.push(e('path', { key: 7, d: 'M30 2Q36 9 42 2M2 30Q9 36 2 42', fill: 'none', stroke: GD, strokeWidth: .8 }));
        if (L >= 5) k.push(e('circle', { key: 8, cx: 27, cy: 27, r: 2.2, fill: SUB, stroke: GB, strokeWidth: .8 }));
        return e('svg', { width: s, height: s, viewBox: '0 0 64 64', style: { display: 'block' } }, k);
      }
      var an = gDur ? { animation: 'chiRevTL ' + gDur + 's cubic-bezier(.25,.6,.3,1) both' } : drawAnim('C');
      return [{ top: -1, left: -1 }, { top: -1, right: -1, transform: 'scaleX(-1)' }, { bottom: -1, left: -1, transform: 'scaleY(-1)' }, { bottom: -1, right: -1, transform: 'scale(-1,-1)' }].map(function (pp) { return abs(Object.assign({ width: s, height: s, zIndex: 2, opacity: hovOp(op), transition: trans }, an, pp), svg()); });
    }
    function studs(L) {
      var t = [[50], [50], [25, 50, 75], [25, 50, 75], [12.5, 25, 37.5, 50, 62.5, 75, 87.5]][L - 1], sd = L >= 2 ? (L >= 4 ? [25, 50, 75] : [50]) : [];
      var out = [];
      t.forEach(function (v, i) { out.push(abs({ top: -1.5, left: v + '%', marginLeft: -5, zIndex: 3 }, e('div', { style: sigAnim(.1 * i) }, sigil(10, i)))); out.push(abs({ bottom: -1.5, left: v + '%', marginLeft: -5, zIndex: 3 }, e('div', { style: sigAnim(.1 * i + .05) }, sigil(10, i + 1)))); });
      sd.forEach(function (v, i) { out.push(abs({ left: -1.5, top: v + '%', marginTop: -5, zIndex: 3 }, e('div', { style: sigAnim(.1 * i) }, sigil(10, i + 2)))); out.push(abs({ right: -1.5, top: v + '%', marginTop: -5, zIndex: 3 }, e('div', { style: sigAnim(.1 * i) }, sigil(10, i + 3)))); });
      return out;
    }
    function cornerSigils() {
      var d = (c.corners ? [0, 26, 34, 42, 50, 58][c.corners] : 12) + 6, sz = 14, out = [];
      var pos = [{ top: -7, left: d }, { top: -7, right: d }];
      if (c.sigil >= 3) pos.push({ bottom: -7, left: d }, { bottom: -7, right: d });
      pos.forEach(function (pp, i) { out.push(abs(Object.assign({ zIndex: 3 }, pp), e('div', { style: sigAnim(.12 * i) }, sigil(sz, i)))); });
      if (c.sigil >= 4) { out.push(abs({ top: -7, left: '50%', marginLeft: -7, zIndex: 3 }, e('div', { style: sigAnim(.3) }, sigil(sz, 1)))); out.push(abs({ bottom: -7, left: '50%', marginLeft: -7, zIndex: 3 }, e('div', { style: sigAnim(.35) }, sigil(sz, 3)))); }
      if (c.sigil >= 5) { out.push(abs({ left: -7, top: '50%', marginTop: -7, zIndex: 3 }, e('div', { style: sigAnim(.4) }, sigil(sz, 2)))); out.push(abs({ right: -7, top: '50%', marginTop: -7, zIndex: 3 }, e('div', { style: sigAnim(.45) }, sigil(sz, 0)))); }
      return out;
    }
    function shimmer(delay) {
      if (!c.shimS) return [];
      // Laser: one bright head runs the whole perimeter (top → right → bottom → left), then rests.
      // On hover (--chiH) it leaves an etched gold line behind it that fades before the next pass.
      var per = [0, 40, 34, 28, 23, 19][c.shimS], op = [0, .35, .5, .65, .8, .95][c.shimA], d0 = delay || 0;
      var etchOp = 'var(--chiH, 0)', ew = 1 + Math.round(c.shimA / 2);
      var G2 = 'linear-gradient(90deg, transparent, ' + GB + ' 70%, #FFF3D6 88%, transparent)', G2v = 'linear-gradient(180deg, transparent, ' + GB + ' 70%, #FFF3D6 88%, transparent)';
      var G2r = 'linear-gradient(270deg, transparent, ' + GB + ' 70%, #FFF3D6 88%, transparent)', G2vr = 'linear-gradient(0deg, transparent, ' + GB + ' 70%, #FFF3D6 88%, transparent)';
      var sides = [
        { pos: { top: 0, left: 0, right: 0, height: 1 }, g: G2, size: '28% 100%', an: 'chiRunX', et: 'chiEtchX' },
        { pos: { top: 0, bottom: 0, right: 0, width: 1 }, g: G2v, size: '100% 28%', an: 'chiRunY', et: 'chiEtchY' },
        { pos: { bottom: 0, left: 0, right: 0, height: 1 }, g: G2r, size: '28% 100%', an: 'chiRunXr', et: 'chiEtchXr' },
        { pos: { top: 0, bottom: 0, left: 0, width: 1 }, g: G2vr, size: '100% 28%', an: 'chiRunYr', et: 'chiEtchYr' }];
      var out = [];
      sides.forEach(function (sd, i) {
        var dl = (d0 + per * .2 * i) + 's';
        out.push(abs(Object.assign({ zIndex: 3, opacity: op, backgroundImage: sd.g, backgroundSize: sd.size, backgroundRepeat: 'no-repeat', animation: sd.an + ' ' + per + 's cubic-bezier(.45,.05,.55,.95) ' + dl + ' infinite both' }, sd.pos)));
        out.push(abs(Object.assign({ zIndex: 3, opacity: etchOp, transition: 'opacity .5s ease' }, sd.pos),
          e('div', { style: { position: 'absolute', inset: 0, animation: sd.et + ' ' + per + 's cubic-bezier(.45,.05,.55,.95) ' + dl + ' infinite both' } },
            e('div', { style: Object.assign({ position: 'absolute', background: 'rgba(227,200,135,.22)' }, i % 2 ? { top: 0, bottom: 0, left: -ew - 1, right: -ew - 1 } : { left: 0, right: 0, top: -ew - 1, bottom: -ew - 1 }) }),
            e('div', { style: Object.assign({ position: 'absolute', background: GB }, i % 2 ? { top: 0, bottom: 0, left: -(ew - 1) / 2, right: -(ew - 1) / 2 } : { left: 0, right: 0, top: -(ew - 1) / 2, bottom: -(ew - 1) / 2 }) }),
            e('div', { style: Object.assign({ position: 'absolute', background: '#FFF3D6', opacity: .85 }, i % 2 ? { top: 0, bottom: 0, left: 0, width: 1 } : { left: 0, right: 0, top: 0, height: 1 }) }))));
      });
      return out;
    }
    function frame(o) {
      o = o || {}; var kids = [];
      if (c.border) {
        var bo = [0, .55, .7, .82, .92, 1][c.border], st = c.borderStyle;
        if (st === 'Engraved line' || st === 'Double line' || st === 'Sigil-studded') {
          kids.push(abs(Object.assign({ inset: 3, border: '1px solid ' + GD, opacity: hovOp(bo), transition: trans }, drawAnim('C'))));
          if (st === 'Double line') kids.push(abs(Object.assign({ inset: 3 + [0, 2, 3, 3, 4, 5][c.border], border: '1px solid ' + GD, opacity: hovOp(bo * .75), transition: trans }, drawAnim('C', .15))));
          if (hA) kids.push(abs({ inset: 3, border: '1px solid ' + GB, opacity: 'calc(var(--chiH, 0) * ' + hA + ')', transition: trans }));
          if (st === 'Sigil-studded') kids = kids.concat(studs(c.border));
        }
        if (st === 'Woven rope') kids = kids.concat(ropeSegs(5, bo, true));
        if (st === 'Filigree corners') kids = kids.concat(filigree(c.border, bo));
      }
      if (c.rope && !(c.border && c.borderStyle === 'Woven rope')) kids = kids.concat(ropeSegs(c.rope, [0, .7, .8, .9, .95, 1][c.rope]));
      if (c.sigil >= 2 && !o.noSigils) kids = kids.concat(cornerSigils());
      kids = kids.concat(shimmer(o.delay));
      if (!o.noCorners && c.corners) kids.push(e(React.Fragment, { key: K('c') }, o.bio ? oc.cornersGlow : oc.corners));
      return e(React.Fragment, { key: K('f') }, kids);
    }
    function divAbs(where) {
      if (!c.dividers) return null;
      var mid = c.sigil ? sigil(16, 2) : OFF.medallion(14);
      return abs(Object.assign({ left: 0, right: 0, height: 16, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 22, zIndex: 3 }, where === 'top' ? { top: -8 } : { bottom: -8 }, drawAnim('C')),
        [c.dividers >= 3 ? node(1.8, G) : null, node(2.4), mid, node(2.4), c.dividers >= 3 ? node(1.8, G) : null]);
    }
    function hdiv(label) {
      return e('div', { key: K('d'), style: Object.assign({ position: 'relative', height: 16, display: 'flex', alignItems: 'center', gap: 10 }, drawAnim('C')) },
        e('div', { style: { flex: 1, height: 1, background: '#3A3F48' } }), c.dividers ? (c.sigil ? sigil(14, 0) : node(2.4)) : null, e('div', { style: { flex: 1, height: 1, background: '#3A3F48' } }));
    }
    function pulse(o) {
      o = o || {}; if (!c.pulseS) return null;
      var per = [0, 6, 5, 4, 3, 2.4][c.pulseS], hi = [0, .35, .5, .65, .82, 1][c.pulseA], inset = o.inset != null ? o.inset : -4;
      var ring = function (d, m, k) { return e('div', { key: k, style: { position: 'absolute', inset: d, border: '1px solid ' + (o.color || BIO), borderRadius: o.round ? '50%' : 0, pointerEvents: 'none', '--lo': .06 * m, '--hi': hi * m, animation: 'chiPulse ' + per + 's ease-in-out infinite' } }); };
      return e(React.Fragment, { key: K('p') }, ring(inset, 1, 'a'), c.pulseA >= 4 ? ring(inset - 3, .5, 'b') : null);
    }
    function turnLayer(size, svgKids, dir, per, k) {
      return e('div', { key: k, style: { position: 'absolute', inset: 0, animation: per ? (dir < 0 ? 'chiTurnR ' : 'chiTurn ') + per + 's linear infinite' : 'none' } }, e('svg', { width: size, height: size, viewBox: '0 0 100 100', style: { display: 'block' } }, svgKids));
    }
    function circle(size, o) {
      o = o || {}; var per = [0, 160, 110, 75, 50, 32][c.turnS], A = c.turnA, col = o.color || G;
      var ticks = []; for (var i = 0; i < 36; i++) { var t = i * 10 * Math.PI / 180, r1 = i % 3 ? 46.5 : 45; ticks.push(e('line', { key: 't' + i, x1: 50 + r1 * Math.cos(t), y1: 50 + r1 * Math.sin(t), x2: 50 + 48 * Math.cos(t), y2: 50 + 48 * Math.sin(t), stroke: GD, strokeWidth: .6 })); }
      var outer = [e('circle', { key: 'o', cx: 50, cy: 50, r: 48.5, fill: 'none', stroke: col, strokeWidth: .7 })].concat(ticks);
      for (var j = 0; j < 6; j++) { var u = (-90 + j * 60) * Math.PI / 180; outer.push(e('circle', { key: 'n' + j, cx: 50 + 41 * Math.cos(u), cy: 50 + 41 * Math.sin(u), r: 2.2, fill: SUB, stroke: col, strokeWidth: .6 })); }
      var mid = [e('circle', { key: 'm', cx: 50, cy: 50, r: 41, fill: 'none', stroke: col, strokeWidth: .6 }), e('circle', { key: 'm2', cx: 50, cy: 50, r: 37.5, fill: 'none', stroke: GD, strokeWidth: .8, strokeDasharray: '.6 2' })];
      var inner = [e('circle', { key: 'i', cx: 50, cy: 50, r: 30, fill: 'none', stroke: col, strokeWidth: .6 })].concat(hexa(50, 50, 30, .6, col)).concat([e('circle', { key: 'c', cx: 50, cy: 50, r: 11, fill: 'none', stroke: GD, strokeWidth: .6 })]);
      return e('div', { key: K('t'), style: { position: 'relative', width: size, height: size, flex: 'none', opacity: o.opacity || 1 } },
        turnLayer(size, outer, 1, A ? per : 0, 'a'), turnLayer(size, mid, -1, A >= 4 ? per * .8 : 0, 'b'), turnLayer(size, inner, -1, A >= 3 ? per * 1.4 : 0, 'c'));
    }
    function progress(size, pct) {
      var R0 = 44, C = 2 * Math.PI * R0, f = Math.max(0, Math.min(1, pct / 100));
      return e('div', { key: K('pg'), style: { position: 'relative', width: size, height: size } }, circle(size, { opacity: .9 }),
        e('svg', { width: size, height: size, viewBox: '0 0 100 100', style: { position: 'absolute', inset: 0, transform: 'rotate(-90deg)' } },
          e('circle', { cx: 50, cy: 50, r: R0, fill: 'none', stroke: '#2A2F38', strokeWidth: 2.4 }),
          e('circle', { cx: 50, cy: 50, r: R0, fill: 'none', stroke: GB, strokeWidth: 2.4, strokeDasharray: (C * f).toFixed(1) + ' ' + C.toFixed(1), style: { transition: 'stroke-dasharray .6s ease' } })));
    }
    function ring(size) {
      if (!c.minimap || !omm.ring) return null; var per = [0, 200, 140, 100, 70, 45][c.turnS];
      return e('div', { key: K('mr'), style: { position: 'absolute', left: 0, top: 0, width: size, height: size, pointerEvents: 'none', animation: c.turnA ? 'chiTurn ' + per + 's linear infinite' : 'none' } }, omm.ring(size));
    }
    function seal(size, k, o) {
      o = o || {}; var dur = [0, 1, .8, .6, .45, .35][c.sealS], s0 = [1, 1.1, 1.18, 1.28, 1.38, 1.5][c.sealA], hi = [0, .3, .45, .6, .8, 1][c.sealA], col = o.color || GB;
      var body = e('svg', { width: size, height: size, viewBox: '0 0 40 40', style: { display: 'block' } },
        e('circle', { cx: 20, cy: 20, r: 18.5, fill: SUB, stroke: col, strokeWidth: 1.2 }), e('circle', { cx: 20, cy: 20, r: 15.5, fill: 'none', stroke: GD, strokeWidth: 1, strokeDasharray: '1 2' }),
        hexa(20, 20, 13, .9, G), e('circle', { cx: 20, cy: 20, r: 7, fill: SUB, stroke: col, strokeWidth: 1 }), e('polyline', { points: '16.5,20 19,22.5 23.8,17.5', fill: 'none', stroke: col, strokeWidth: 1.4, strokeLinecap: 'round', strokeLinejoin: 'round' }));
      return e('div', { key: 'seal' + n + '_' + k, style: { position: 'relative', width: size, height: size, flex: 'none' } },
        e('div', { style: dur ? { '--s0': s0, animation: 'chiSeal ' + dur + 's cubic-bezier(.2,.7,.3,1) both' } : {} }, body),
        dur ? e('div', { style: { position: 'absolute', inset: 0, borderRadius: '50%', border: '1px solid ' + col, '--hi': hi, animation: 'chiSealRing ' + (dur * 1.6) + 's ease-out ' + (dur * .45) + 's both', pointerEvents: 'none' } }) : null);
    }
    return {
      cfg: c, K: K, frame: frame, sigil: sigil, pulse: pulse, circle: circle, progress: progress, ring: ring, seal: seal, hdiv: hdiv, drawAnim: drawAnim, notes: window.CHI_LV_NOTES,
      orn: {
        tex: ot.tex || null, corners: oc.corners || null, cornersGlow: oc.cornersGlow || null,
        hl: c.headers ? (c.sigil ? sigil(c.headers >= 4 ? 20 : 17, 0) : oh.hl) : OFF.hl, hr: c.headers ? oh.hr : OFF.hr,
        divTop: divAbs('top'), divBottom: divAbs('bottom'), topbar: otb.topbar || null, rail: ora.rail || null, railSel: ora.railSel || null,
        btnGold: obt.btnGold || null, btnDark: obt.btnDark || null, inspTitle: c.headers ? omm.inspTitle : null
      }
    };
  };
})();
