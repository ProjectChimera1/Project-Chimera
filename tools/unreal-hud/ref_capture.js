// ref_capture.js: Playwright snippet for `playwright-cli run-code --filename` (plan B section 3, task T0).
//
// ref_render.py replaces the JOB literal below with the job list, writes the result next to the site and runs it. The
// snippet renders board 3.1a of Match.dc.html at 1920x1080 with every CSS animation paused at 3000 ms, one PNG per job.
// run-code has no `require`/`fs`, so all file reads happen through Playwright itself (route.fulfill({path}),
// locator.screenshot({path})) and everything else is returned as one JSON string.
//
// Per job (JOB.jobs[i]):
//   name, out        job name and absolute PNG path
//   fonts            'google' (the pinned variable woff2 files, the default) | 'static' (the static TTFs, control P1/P4)
//   world            'show' | 'hide' (nodes 1-3 hidden) | 'only' (everything but nodes 1-3 hidden)
//   bg               null | '#000' | '#fff'  board background (the alpha matte is computed from a black and a white render)
//   textOff          true: every glyph transparent (-webkit-text-fill-color), icons and boxes stay
//   mods             list of: 'minimap' | 'figure' | 'lasers_off' | 'P2' | 'P3' | 'N1' ... 'N8'
// Node ids are the pre-order DOM index inside the board, identical to `elements[].id` in board-3.1-elements.json
// (checked: 485/485 boxes equal).
async (page) => {
  const JOB = /*JOB*/ { jobs: [] } /*END*/;
  const results = [];
  const ctx = page.context();
  const browserVersion = ctx.browser() ? ctx.browser().version() : null;

  for (const job of JOB.jobs) {
    const p = await ctx.newPage();
    const served = [], unmapped = [], failed = [];
    try {
      await p.setViewportSize({ width: 2200, height: 1300 });
      // ---- fonts: serve the pinned files, never the live Google service
      await p.route('https://fonts.googleapis.com/**', (route) => {
        if (job.fonts === 'static') {
          route.fulfill({ body: JOB.staticCss, contentType: 'text/css', headers: { 'access-control-allow-origin': '*' } });
        } else {
          route.fulfill({ path: JOB.googleCss, contentType: 'text/css', headers: { 'access-control-allow-origin': '*' } });
        }
      });
      await p.route('https://fonts.gstatic.com/**', (route) => {
        const url = route.request().url();
        const map = job.fonts === 'static' ? JOB.staticMap : JOB.fontMap;
        const file = map[url];
        if (!file) { unmapped.push(url); route.abort(); return; }
        served.push(url);
        route.fulfill({ path: file, contentType: job.fonts === 'static' ? 'font/ttf' : 'font/woff2',
                        headers: { 'access-control-allow-origin': '*' } });
      });
      p.on('requestfailed', (r) => failed.push(r.url()));

      await p.goto(JOB.site + '/Match.dc.html');
      await p.waitForTimeout(1500);
      await p.getByTitle('Show only 3.1a', { exact: true }).click();
      await p.waitForTimeout(800);
      await p.mouse.move(2, 2);
      await p.evaluate(() => document.fonts.ready);
      await p.waitForTimeout(300);

      // ---- the in-page part: tag nodes, pause animations, apply the job, check fonts
      const info = await p.evaluate(async (job) => {
        const board = document.querySelector('[data-screen-label="3.1a"]');
        const els = [board, ...board.querySelectorAll('*')];
        els.forEach((e, i) => e.setAttribute('data-ctl', String(i)));
        const out = { nodes: els.length, mod: { ok: true } };
        const rgb = (s) => s;
        const style = (css) => { const t = document.createElement('style'); t.textContent = css; document.head.appendChild(t); };

        // text-bearing elements = elements with a direct non-blank text node
        const textEls = [];
        els.forEach((e, i) => {
          for (const n of e.childNodes) { if (n.nodeType === 3 && n.nodeValue.trim()) { textEls.push(i); break; } }
        });
        out.textEls = textEls;

        // ---- fonts really loaded (document.fonts.check() alone is true for an unregistered family)
        const faces = [...document.fonts].map((f) => ({ family: f.family.replace(/["']/g, ''), weight: f.weight, status: f.status }));
        const used = new Map();
        textEls.forEach((i) => {
          const cs = getComputedStyle(els[i]);
          const fam = cs.fontFamily.split(',')[0].replace(/["']/g, '').trim();
          used.set(fam + '|' + cs.fontWeight + '|' + cs.fontSize, [fam, cs.fontWeight, cs.fontSize]);
        });
        const inRange = (w, spec) => { const p = spec.split(' ').map(Number); return p.length === 1 ? p[0] === w : (w >= p[0] && w <= p[1]); };
        out.fontsUsed = [...used.values()].map(([fam, w, sz]) => {
          const loaded = faces.some((f) => f.family === fam && f.status === 'loaded' && inRange(Number(w), f.weight));
          const check = document.fonts.check(w + ' ' + sz + ' "' + fam + '"');
          return { family: fam, weight: Number(w), size: sz, loaded, check, ok: loaded && check };
        });
        out.facesLoaded = faces.filter((f) => f.status === 'loaded').length;
        out.facesTotal = faces.length;

        // ---- animations: pause everything at 3000 ms, report the laser heads
        document.getAnimations().forEach((a) => { a.pause(); a.currentTime = 3000; });
        const brect = board.getBoundingClientRect();
        const rel = (e) => { const r = e.getBoundingClientRect(); return [+(r.x - brect.x).toFixed(2), +(r.y - brect.y).toFixed(2), +r.width.toFixed(2), +r.height.toFixed(2)]; };
        out.lasers = document.getAnimations().filter((a) => a.animationName === 'chiRunX' && a.effect.target && board.contains(a.effect.target)).map((a) => {
          const t = a.effect.target;
          const tm = a.effect.getComputedTiming();
          return { id: Number(t.getAttribute('data-ctl')), box: rel(t), delay: tm.delay, progress: tm.progress,
                   localTime: tm.localTime, bgPos: getComputedStyle(t).backgroundPosition, bgSize: getComputedStyle(t).backgroundSize };
        });
        out.animCount = document.getAnimations().length;

        // ---- backdrop and background
        const worldLayer = els[1];
        if (job.world === 'hide') worldLayer.style.visibility = 'hidden';
        if (job.world === 'only') {
          [...board.children].forEach((c) => { if (c !== worldLayer) c.style.visibility = 'hidden'; });
        }
        if (job.bg) board.style.background = job.bg;
        if (job.textOff) style('*{-webkit-text-fill-color:transparent !important}');

        // ---- the job's one change
        for (const m of (job.mods || [])) {
        if (m === 'minimap') {
          els[203].querySelectorAll('*').forEach((e) => { e.style.visibility = 'hidden'; });
          els[203].style.boxShadow = 'none';
        } else if (m === 'figure') {
          board.querySelectorAll('*').forEach((e) => { e.style.visibility = 'hidden'; });
          els[266].style.visibility = 'visible';
          els[266].querySelectorAll('*').forEach((e) => { e.style.visibility = 'visible'; });
        } else if (m === 'lasers_off') {
          out.lasers.forEach((l) => { els[l.id].style.visibility = 'hidden'; });
        } else if (m === 'P2') {
          style('*{font-kerning:none !important}');
        } else if (m === 'P3') {
          // every element moves exactly once: an element inside an already-offset element moves with it (els is in
          // pre-order, so ancestors are marked first). Offsetting nested ones again moved them by (.8,.6) px, past the
          // half-pixel snap (the F10 keycap inside the Menu button moved a whole pixel; verifier 2026-10-01)
          let n = 0, nested = 0;
          els.forEach((e, i) => {
            const isSvgRoot = e.tagName.toLowerCase() === 'svg';
            if (!isSvgRoot && !textEls.includes(i)) return;
            if (e.parentElement && e.parentElement.closest('[data-p3]')) { nested++; return; }
            e.setAttribute('data-p3', '1');
            const cs = getComputedStyle(e);
            if (isSvgRoot) { e.style.translate = '.4px .3px'; n++; return; }
            if (cs.position === 'static') { e.style.position = 'relative'; e.style.left = '.4px'; e.style.top = '.3px'; }
            else if (cs.display === 'inline') { e.style.translate = '.4px .3px'; }
            else { e.style.translate = '.4px .3px'; }
            n++;
          });
          out.mod.count = n;
          out.mod.nestedSkipped = nested;
        } else if (m === 'N1') {
          els[283].style.transform = 'translateY(1px)';
        } else if (m === 'N2') {
          [9, 18, 27, 38].forEach((i) => { els[i].style.backgroundColor = '#1C1F25'; });
        } else if (m === 'N3') {
          els[270].style.fontWeight = '700';
        } else if (m === 'N4') {
          els[77].style.fontSize = '14px';
        } else if (m === 'N5') {
          els[182].style.borderColor = 'transparent';
        } else if (m === 'N6') {
          els[444].innerHTML = els[191].innerHTML;
        } else if (m === 'N7') {
          // change the family on the row AND the run, with the row's line-height pinned in px: a different font on the
          // strut alone would grow the line box by 1 px and shift the whole info column (measured)
          [281, 282].forEach((i) => { els[i].style.lineHeight = '15.6px'; els[i].style.fontFamily = 'Inter, sans-serif'; });
        } else if (m === 'N8') {
          els[272].style.color = '#ECE6D8';
        }
        }
        out.board = rel(board);
        return out;
      }, job);

      // fonts that were still loading (a job changed a family/weight): wait, then re-check
      await p.evaluate(() => document.fonts.ready);
      await p.waitForTimeout(250);
      // the font assertion again, AFTER the job's mods and the second fonts.ready, so a control that changes a family,
      // weight or size (N3 Cinzel 700, N4 Inter 14px, N7 Inter stat line, P1/P4 static faces) is checked as rendered
      const after = await p.evaluate((textEls) => {
        const board = document.querySelector('[data-screen-label="3.1a"]');
        const anims = document.getAnimations();
        const faces = [...document.fonts].map((f) => ({ family: f.family.replace(/["']/g, ''), weight: f.weight, status: f.status }));
        const used = new Map();
        textEls.forEach((i) => {
          const cs = getComputedStyle(board.querySelector('[data-ctl="' + i + '"]') || board);
          const fam = cs.fontFamily.split(',')[0].replace(/["']/g, '').trim();
          used.set(fam + '|' + cs.fontWeight + '|' + cs.fontSize, [fam, cs.fontWeight, cs.fontSize]);
        });
        const inRange = (w, spec) => { const q = String(spec).split(' ').map(Number); return q.length === 1 ? q[0] === w : (w >= q[0] && w <= q[1]); };
        const fontsUsed = [...used.values()].map(([fam, w, sz]) => {
          const loaded = faces.some((f) => f.family === fam && f.status === 'loaded' && inRange(Number(w), f.weight));
          const check = document.fonts.check(w + ' ' + sz + ' "' + fam + '"');
          return { family: fam, weight: Number(w), size: sz, loaded, check, ok: loaded && check };
        });
        return { nAnim: anims.length, allPaused: anims.every((a) => a.playState === 'paused' && a.currentTime === 3000),
                 fontsStatus: document.fonts.status, ctl: board.querySelectorAll('[data-ctl]').length, fontsUsed };
      }, info.textEls);

      // ---- platform fonts per text node (is the glyph source really the web font?)
      let platform = [];
      try {
        const cdp = await ctx.newCDPSession(p);
        await cdp.send('DOM.enable'); await cdp.send('CSS.enable');
        const doc = await cdp.send('DOM.getDocument', { depth: 0 });
        for (const i of info.textEls) {
          const q = await cdp.send('DOM.querySelector', { nodeId: doc.root.nodeId, selector: '[data-ctl="' + i + '"]' });
          const f = await cdp.send('CSS.getPlatformFontsForNode', { nodeId: q.nodeId });
          platform.push({ id: i, fonts: f.fonts.map((x) => ({ family: x.familyName, ps: x.postScriptName, custom: x.isCustomFont, glyphs: x.glyphCount })) });
        }
        await cdp.detach();
      } catch (e) { platform = [{ error: String(e) }]; }

      const gl = await p.evaluate(() => {
        try {
          const c = document.createElement('canvas'); const g = c.getContext('webgl');
          const ext = g.getExtension('WEBGL_debug_renderer_info');
          return ext ? g.getParameter(ext.UNMASKED_RENDERER_WEBGL) : g.getParameter(g.RENDERER);
        } catch (e) { return 'none'; }
      });
      const ua = await p.evaluate(() => navigator.userAgent);

      await p.locator('[data-screen-label="3.1a"]').screenshot({ path: job.out });
      results.push({ name: job.name, out: job.out, fonts: job.fonts, world: job.world, bg: job.bg, textOff: !!job.textOff,
                     mods: job.mods || [], info, after, platform, served, unmapped, failed, browserVersion, ua, webgl: gl });
    } catch (e) {
      results.push({ name: job.name, error: String(e && e.stack || e) });
    } finally {
      await p.close();
    }
  }
  return JSON.stringify(results);
}
