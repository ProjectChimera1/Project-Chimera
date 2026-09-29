// Chimera ornament kit: transmutation geometry (circles, hexagrams, vertex nodes). Each piece is null when its toggle is off.
window.CHI_ORN = function (React, o) {
  var e = React.createElement, L = Math.max(1, Math.min(5, o.intensity || 3)), glow = o.glow !== false;
  var G = '#C9A86A', GB = '#E3C887', GD = '#8A6E3C', SUB = '#14161A', BIO = '#7FE3A1';
  var k = 0; function K() { return 'o' + (k++); }
  function hexa(cx, cy, r, sw, col) {
    var a = [], b = [];
    for (var i = 0; i < 3; i++) { var t = (-90 + i * 120) * Math.PI / 180, u = (90 + i * 120) * Math.PI / 180; a.push((cx + r * Math.cos(t)).toFixed(2) + ',' + (cy + r * Math.sin(t)).toFixed(2)); b.push((cx + r * Math.cos(u)).toFixed(2) + ',' + (cy + r * Math.sin(u)).toFixed(2)); }
    return [e('polygon', { key: K(), points: a.join(' '), stroke: col || G, strokeWidth: sw, fill: 'none' }), e('polygon', { key: K(), points: b.join(' '), stroke: col || G, strokeWidth: sw, fill: 'none' })];
  }
  function medallion(size, bio) {
    return e('svg', { key: K(), width: size, height: size, viewBox: '0 0 20 20', style: { display: 'block', flex: 'none' } },
      e('circle', { cx: 10, cy: 10, r: 9, fill: SUB, stroke: G, strokeWidth: 1 }),
      e('circle', { cx: 10, cy: 10, r: 6.8, fill: 'none', stroke: GD, strokeWidth: .7, strokeDasharray: '.8 1.4' }),
      hexa(10, 10, 6.8, .8),
      e('circle', { cx: 10, cy: 10, r: 1.7, fill: bio ? BIO : GB }));
  }
  function node(r, fill) { return e('svg', { key: K(), width: r * 2 + 2, height: r * 2 + 2, viewBox: '0 0 ' + (r * 2 + 2) + ' ' + (r * 2 + 2), style: { display: 'block', flex: 'none' } }, e('circle', { cx: r + 1, cy: r + 1, r: r, fill: fill || SUB, stroke: G, strokeWidth: 1 })); }
  function abs(style, kids) { return e('div', { key: K(), style: Object.assign({ position: 'absolute', pointerEvents: 'none' }, style) }, kids); }

  // corners
  function cornerSvg(size, bio) {
    var kids = [
      e('polyline', { key: K(), points: '5,44 5,5 44,5', fill: 'none', stroke: G, strokeWidth: 1 }),
      e('path', { key: K(), d: 'M20 1 A19 19 0 0 1 1 20', fill: 'none', stroke: G, strokeWidth: 1 }),
      e('line', { key: K(), x1: 5, y1: 5, x2: 14, y2: 14, stroke: GD, strokeWidth: 1 }),
      e('circle', { key: K(), cx: 44, cy: 5, r: 1.8, fill: G }), e('circle', { key: K(), cx: 5, cy: 44, r: 1.8, fill: G }),
      e('circle', { key: K(), cx: 5, cy: 5, r: 4, fill: SUB, stroke: GB, strokeWidth: 1 }),
      e('circle', { key: K(), cx: 5, cy: 5, r: 1.5, fill: bio && glow ? BIO : GB })
    ];
    if (L >= 3) kids.splice(2, 0, e('path', { key: K(), d: 'M29 1 A28 28 0 0 1 1 29', fill: 'none', stroke: GD, strokeWidth: .8, strokeDasharray: '1 2.5' }));
    if (L >= 4) { kids.push(e('circle', { key: K(), cx: 15.5, cy: 15.5, r: 4.5, fill: SUB, stroke: G, strokeWidth: .8 })); kids = kids.concat(hexa(15.5, 15.5, 3.4, .6)); }
    if (L >= 5) { kids.push(e('polyline', { key: K(), points: '9,36 9,9 36,9', fill: 'none', stroke: GD, strokeWidth: .7 })); kids.push(e('circle', { key: K(), cx: 13.4, cy: 19.7, r: 1.2, fill: G })); kids.push(e('circle', { key: K(), cx: 19.7, cy: 13.4, r: 1.2, fill: G })); }
    return e('svg', { width: size, height: size, viewBox: '0 0 48 48', style: { display: 'block' } }, kids);
  }
  function corners(bio) {
    var s = [26, 34, 42, 50, 58][L - 1], off = -2;
    var pos = [{ top: off, left: off }, { top: off, right: off, transform: 'scaleX(-1)' }, { bottom: off, left: off, transform: 'scaleY(-1)' }, { bottom: off, right: off, transform: 'scale(-1,-1)' }];
    return e(React.Fragment, { key: K() }, pos.map(function (p) { return abs(Object.assign({ width: s, height: s, zIndex: 3 }, p), cornerSvg(s, bio)); }));
  }

  // texture
  var op = [.035, .05, .07, .09, .12][L - 1];
  var tile = "<svg xmlns='http://www.w3.org/2000/svg' width='140' height='140'><g fill='none' stroke='%23C9A86A' stroke-opacity='" + op + "' stroke-width='.8'><circle cx='70' cy='70' r='38'/><circle cx='70' cy='70' r='31' stroke-dasharray='1 3'/><polygon points='70,39 96.8,85.5 43.2,85.5'/><polygon points='70,101 43.2,54.5 96.8,54.5'/><circle cx='70' cy='70' r='6'/><circle cx='0' cy='0' r='22'/><circle cx='140' cy='0' r='22'/><circle cx='0' cy='140' r='22'/><circle cx='140' cy='140' r='22'/><line x1='0' y1='70' x2='32' y2='70'/><line x1='108' y1='70' x2='140' y2='70'/><line x1='70' y1='0' x2='70' y2='32'/><line x1='70' y1='108' x2='70' y2='140'/></g></svg>";
  var tex = abs({ inset: 0, zIndex: -1, backgroundImage: 'url("data:image/svg+xml,' + tile.replace(/</g, '%3C').replace(/>/g, '%3E').replace(/"/g, "'") + '")', backgroundSize: '140px 140px' });

  // header terminals
  var hlOff = e('div', { style: { width: 6, height: 6, background: GD, transform: 'rotate(45deg)', flex: 'none' } });
  var hrOff = e('div', { style: { flex: 1, height: 1, background: '#4A4234' } });
  var hlOn = medallion(L >= 4 ? 20 : 17);
  var hrOn = e('div', { style: { flex: 1, display: 'flex', alignItems: 'center', gap: 5, minWidth: 0 } },
    e('div', { style: { flex: 1, height: 4, borderTop: '1px solid ' + GD, borderBottom: '1px solid #4A4234' } }),
    node(3), L >= 3 ? e('div', { style: { width: 16, height: 1, background: GD, flex: 'none' } }) : null, L >= 3 ? node(2, G) : null);

  // dividers (sit on an existing border line)
  function divAbs(where) {
    return abs(Object.assign({ left: 0, right: 0, height: 14, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 22, zIndex: 3 }, where === 'top' ? { top: -7 } : { bottom: -7 }),
      [L >= 3 ? node(1.8, G) : null, node(2.4), medallion(14), node(2.4), L >= 3 ? node(1.8, G) : null]);
  }

  // top bar
  var topbar = e(React.Fragment, null,
    abs({ left: 0, right: 0, bottom: 3, height: 1, background: '#4A4234' }),
    abs({ left: 0, right: 0, bottom: -10, height: 20, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 0, zIndex: 3 }, [
      node(2, G), e('div', { key: K(), style: { width: 90, height: 1, background: G } }), node(3), e('div', { key: K(), style: { width: 40, height: 1, background: G } }),
      medallion(20), e('div', { key: K(), style: { width: 40, height: 1, background: G } }), node(3), e('div', { key: K(), style: { width: 90, height: 1, background: G } }), node(2, G)]));

  // rail
  var rail = e(React.Fragment, null,
    abs({ top: 0, bottom: 0, right: 4, width: 1, background: '#4A4234' }),
    abs({ top: 4, right: -1, zIndex: 3 }, node(3)), abs({ bottom: 4, right: -1, zIndex: 3 }, node(3)));
  var railSel = abs({ left: -5, top: -5, width: 54, height: 54 }, e('svg', { width: 54, height: 54, viewBox: '0 0 54 54', style: { display: 'block' } },
    e('circle', { cx: 27, cy: 27, r: 24, fill: 'none', stroke: GB, strokeWidth: 1 }),
    L >= 3 ? e('circle', { cx: 27, cy: 27, r: 20.5, fill: 'none', stroke: GD, strokeWidth: .8, strokeDasharray: '1 2.2' }) : null,
    [0, 90, 180, 270].map(function (a) { var t = a * Math.PI / 180; return e('circle', { key: a, cx: 27 + 24 * Math.cos(t), cy: 27 + 24 * Math.sin(t), r: 2.2, fill: SUB, stroke: GB, strokeWidth: 1 }); })));

  // buttons
  function btn(onGold) {
    var c = onGold ? 'rgba(20,22,26,.5)' : GD;
    return e(React.Fragment, null,
      abs({ inset: 3, border: '1px solid ' + c }),
      abs({ left: -4, top: '50%', marginTop: -4, width: 8, height: 8, transform: 'rotate(45deg)', background: onGold ? GB : SUB, border: '1px solid ' + (onGold ? '#8A6E3C' : G) }),
      abs({ right: -4, top: '50%', marginTop: -4, width: 8, height: 8, transform: 'rotate(45deg)', background: onGold ? GB : SUB, border: '1px solid ' + (onGold ? '#8A6E3C' : G) }));
  }

  // inspector title underline
  var inspTitle = e('div', { style: { display: 'flex', alignItems: 'center', gap: 6, marginTop: 4 } }, medallion(16), hrOn);

  // ring overlay for minimaps: size px, square map occupies the middle
  function ring(size) {
    var c = size / 2, R = c - 3, ri = R - 9, kids = [
      e('circle', { key: K(), cx: c, cy: c, r: R, fill: 'none', stroke: G, strokeWidth: 2 }),
      e('circle', { key: K(), cx: c, cy: c, r: R - 4, fill: 'none', stroke: GD, strokeWidth: 3, strokeDasharray: '1 3' }),
      e('circle', { key: K(), cx: c, cy: c, r: ri, fill: 'none', stroke: G, strokeWidth: 1 })];
    kids = kids.concat(hexa(c, c, ri, 1, GD));
    for (var i = 0; i < 6; i++) { var t = (-90 + i * 60) * Math.PI / 180; kids.push(e('circle', { key: K(), cx: c + ri * Math.cos(t), cy: c + ri * Math.sin(t), r: 4, fill: SUB, stroke: GB, strokeWidth: 1 })); kids.push(e('circle', { key: K(), cx: c + ri * Math.cos(t), cy: c + ri * Math.sin(t), r: 1.6, fill: glow ? BIO : GB })); }
    if (L >= 4) for (var j = 0; j < 24; j++) { var u = j * 15 * Math.PI / 180; kids.push(e('line', { key: K(), x1: c + (R - 1) * Math.cos(u), y1: c + (R - 1) * Math.sin(u), x2: c + (R + 2) * Math.cos(u), y2: c + (R + 2) * Math.sin(u), stroke: G, strokeWidth: 1 })); }
    return e('svg', { width: size, height: size, viewBox: '0 0 ' + size + ' ' + size, style: { position: 'absolute', left: 0, top: 0, pointerEvents: 'none' } }, kids);
  }

  return {
    corners: o.corners ? corners(false) : null, cornersGlow: o.corners ? corners(true) : null,
    tex: o.texture ? tex : null,
    hl: o.headers ? hlOn : hlOff, hr: o.headers ? hrOn : hrOff,
    divTop: o.dividers ? divAbs('top') : null, divBottom: o.dividers ? divAbs('bottom') : null,
    topbar: o.topBar ? topbar : null, rail: o.rail ? rail : null, railSel: o.rail ? railSel : null,
    btnGold: o.buttons ? btn(true) : null, btnDark: o.buttons ? btn(false) : null,
    inspTitle: o.inspector ? inspTitle : null, ring: o.minimap ? ring : null, medallion: medallion
  };
};
