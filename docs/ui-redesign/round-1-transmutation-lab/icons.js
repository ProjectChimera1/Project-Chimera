// Chimera line-icon set: 24px grid, 1.5 stroke, primitives only. Consumed by .dc.html logic via window.CHI_ICONS.
window.CHI_ICONS = {
  select: [["polygon", { points: "5,3 19,12 12.5,13.5 9.5,20" }]],
  terrain: [["polyline", { points: "2,19 8,9 12,14 15,10 22,19" }], ["line", { x1: 2, y1: 19, x2: 22, y2: 19 }]],
  water: [["path", { d: "M2 9c2.5-2.5 5 2.5 7.5 0s5-2.5 7.5 0 3.5 1 5 0" }], ["path", { d: "M2 15c2.5-2.5 5 2.5 7.5 0s5-2.5 7.5 0 3.5 1 5 0" }]],
  place: [["polyline", { points: "3,10 12,3 21,10" }], ["rect", { x: 5, y: 9, width: 14, height: 11 }], ["rect", { x: 10, y: 14, width: 4, height: 6 }]],
  unit: [["circle", { cx: 12, cy: 7, r: 3.2 }], ["path", { d: "M5.5 21v-2.5a6.5 6.5 0 0 1 13 0V21" }]],
  resource: [["polygon", { points: "12,2.5 19,9 12,21.5 5,9" }], ["line", { x1: 5, y1: 9, x2: 19, y2: 9 }]],
  start: [["line", { x1: 6, y1: 21, x2: 6, y2: 3 }], ["polygon", { points: "6,4 19,7.5 6,11" }]],
  prop: [["polygon", { points: "12,3 18.5,15 5.5,15" }], ["line", { x1: 12, y1: 15, x2: 12, y2: 21 }]],
  region: [["rect", { x: 5, y: 6, width: 14, height: 12, "stroke-dasharray": "2.5 2" }], ["rect", { x: 3, y: 4, width: 4, height: 4 }], ["rect", { x: 17, y: 16, width: 4, height: 4 }]],
  pathing: [["circle", { cx: 12, cy: 12, r: 8.5 }], ["line", { x1: 6, y1: 18, x2: 18, y2: 6 }]],
  camera: [["rect", { x: 2.5, y: 7, width: 13, height: 10 }], ["polygon", { points: "15.5,10.5 21.5,7 21.5,17 15.5,13.5" }]],
  map: [["polygon", { points: "3,6 9,4 15,6 21,4 21,18 15,20 9,18 3,20" }], ["line", { x1: 9, y1: 4, x2: 9, y2: 18 }], ["line", { x1: 15, y1: 6, x2: 15, y2: 20 }]],
  rules: [["circle", { cx: 6, cy: 6, r: 2.5 }], ["circle", { cx: 18, cy: 12, r: 2.5 }], ["circle", { cx: 6, cy: 18, r: 2.5 }], ["path", { d: "M8.5 6h3.5v12H8.5M12 12h3.5" }]],
  undo: [["polyline", { points: "8,4 4,8 8,12" }], ["path", { d: "M4 8h10a6 6 0 0 1 0 12h-4" }]],
  redo: [["polyline", { points: "16,4 20,8 16,12" }], ["path", { d: "M20 8H10a6 6 0 0 0 0 12h4" }]],
  save: [["rect", { x: 4, y: 4, width: 16, height: 16 }], ["rect", { x: 8, y: 4, width: 8, height: 5 }], ["rect", { x: 7, y: 13, width: 10, height: 7 }]],
  play: [["polygon", { points: "7,4 20,12 7,20" }]],
  stop: [["rect", { x: 6, y: 6, width: 12, height: 12 }]],
  search: [["circle", { cx: 10.5, cy: 10.5, r: 6.5 }], ["line", { x1: 15.5, y1: 15.5, x2: 21, y2: 21 }]],
  rotate: [["path", { d: "M20 12a8 8 0 1 1-2.4-5.7" }], ["polyline", { points: "20,3 20,8 15,8" }]],
  copy: [["rect", { x: 8, y: 8, width: 12, height: 12 }], ["polyline", { points: "4,16 4,4 16,4" }]],
  trash: [["line", { x1: 4, y1: 6, x2: 20, y2: 6 }], ["polyline", { points: "6,6 7,20 17,20 18,6" }], ["line", { x1: 10, y1: 3, x2: 14, y2: 3 }]],
  owner: [["circle", { cx: 8, cy: 9, r: 4 }], ["path", { d: "M3 20a5 5 0 0 1 10 0" }], ["polyline", { points: "16,9 20,13 16,17" }], ["line", { x1: 12, y1: 13, x2: 20, y2: 13 }]],
  group: [["rect", { x: 3, y: 3, width: 8, height: 8 }], ["rect", { x: 13, y: 13, width: 8, height: 8 }], ["line", { x1: 11, y1: 11, x2: 13, y2: 13 }]],
  move: [["line", { x1: 12, y1: 3, x2: 12, y2: 21 }], ["line", { x1: 3, y1: 12, x2: 21, y2: 12 }], ["polyline", { points: "9,6 12,3 15,6" }], ["polyline", { points: "9,18 12,21 15,18" }], ["polyline", { points: "6,9 3,12 6,15" }], ["polyline", { points: "18,9 21,12 18,15" }]],
  win: [["polygon", { points: "12,3 14.6,9 21,9.6 16.1,13.8 17.6,20.2 12,16.8 6.4,20.2 7.9,13.8 3,9.6 9.4,9" }]],
  publish: [["polyline", { points: "7,9 12,4 17,9" }], ["line", { x1: 12, y1: 4, x2: 12, y2: 16 }], ["polyline", { points: "4,15 4,20 20,20 20,15" }]],
  check: [["polyline", { points: "4,12.5 9.5,18 20,6" }]],
  x: [["line", { x1: 5, y1: 5, x2: 19, y2: 19 }], ["line", { x1: 19, y1: 5, x2: 5, y2: 19 }]],
  plus: [["line", { x1: 12, y1: 4, x2: 12, y2: 20 }], ["line", { x1: 4, y1: 12, x2: 20, y2: 12 }]],
  chevron: [["polyline", { points: "9,5 16,12 9,19" }]],
  down: [["polyline", { points: "5,9 12,16 19,9" }]],
  eye: [["path", { d: "M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z" }], ["circle", { cx: 12, cy: 12, r: 3 }]],
  lock: [["rect", { x: 5, y: 11, width: 14, height: 10 }], ["path", { d: "M8 11V7a4 4 0 0 1 8 0v4" }]],
  grid: [["rect", { x: 3, y: 3, width: 18, height: 18 }], ["line", { x1: 9, y1: 3, x2: 9, y2: 21 }], ["line", { x1: 15, y1: 3, x2: 15, y2: 21 }], ["line", { x1: 3, y1: 9, x2: 21, y2: 9 }], ["line", { x1: 3, y1: 15, x2: 21, y2: 15 }]],
  sliders: [["line", { x1: 4, y1: 7, x2: 20, y2: 7 }], ["line", { x1: 4, y1: 17, x2: 20, y2: 17 }], ["rect", { x: 7, y: 5, width: 4, height: 4 }], ["rect", { x: 13, y: 15, width: 4, height: 4 }]],
  array: [["circle", { cx: 12, cy: 12, r: 9.5 }], ["polygon", { points: "12,3.5 19.4,16.3 4.6,16.3" }], ["polygon", { points: "12,20.5 4.6,7.7 19.4,7.7" }], ["circle", { cx: 12, cy: 12, r: 3 }]],
  alert: [["polygon", { points: "12,3 22,20 2,20" }], ["line", { x1: 12, y1: 9, x2: 12, y2: 14 }], ["line", { x1: 12, y1: 16.5, x2: 12, y2: 17.5 }]],
  info: [["circle", { cx: 12, cy: 12, r: 9 }], ["line", { x1: 12, y1: 11, x2: 12, y2: 17 }], ["line", { x1: 12, y1: 7.5, x2: 12, y2: 8.5 }]]
};
window.CHI_ICON = function (React, name, size, color, sw) {
  var parts = window.CHI_ICONS[name] || [];
  return React.createElement("svg", { key: name, width: size || 18, height: size || 18, viewBox: "0 0 24 24", fill: "none", stroke: color || "currentColor", strokeWidth: sw || 1.5, strokeLinecap: "round", strokeLinejoin: "round", style: { display: "block", flex: "none" } },
    parts.map(function (p, i) {
      var a = {}; for (var k in p[1]) { a[k === "stroke-dasharray" ? "strokeDasharray" : k] = p[1][k]; }
      a.key = i; return React.createElement(p[0], a);
    }));
};
