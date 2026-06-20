/* Shared 1920×1080 letterbox scaler for Project Chimera screens.
   Markup:  <div class="viewport"><div class="stage">…1920×1080…</div></div>
   Scales .stage to fit while preserving aspect, centering on void black. */
(function () {
  function fit() {
    var ok = true;
    document.querySelectorAll('.stage').forEach(function (stage) {
      var vp = stage.parentElement;
      var vw = vp.clientWidth, vh = vp.clientHeight;
      if (!vw || !vh) { ok = false; return; }            // viewport not laid out yet
      var sw = stage.offsetWidth || 1920, sh = stage.offsetHeight || 1080;
      var scale = Math.min(vw / sw, vh / sh);
      if (!isFinite(scale) || scale <= 0) { ok = false; return; }
      stage.style.transform = 'translate(-50%,-50%) scale(' + scale + ')';
    });
    return ok;
  }
  var tries = 0;
  function fitRetry() {
    if (!fit() && tries++ < 60) requestAnimationFrame(fitRetry);  // retry ~1s until first layout
  }
  window.addEventListener('resize', fit);
  window.addEventListener('load', fitRetry);
  document.addEventListener('DOMContentLoaded', fitRetry);
  fitRetry();
  window.__fitStage = fit;
})();
