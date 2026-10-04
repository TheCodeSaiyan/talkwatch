/* The Signal Rail's signature: now and then, while nothing is happening, the rail's line rises out of its quiet and
   writes "TalkWatch" in one oscilloscope stroke, holds it a moment, and settles back. Only when idle, with the console
   connected and the page in view, at a random interval, in the empty stretch of the rail; never where it would cover
   a workspace, never with reduced motion. It is not a signal: the line goes on meaning what it always means. */
(function () {
  'use strict';
  var TW = window.TW = window.TW || {};

  // Monoline glyphs in em units: x across from the glyph's left, y up from the baseline (cap height 1, x-height .6).
  var GLYPHS = {
    T: { w: .64, s: [[[.02, 1], [.62, 1]], [[.32, 1], [.32, 0]]] },
    a: { w: .54, s: [[[.44, .46], [.36, .57], [.22, .6], [.09, .51], [.04, .3], [.09, .09], [.22, 0], [.36, .04], [.44, .16]], [[.44, .6], [.44, 0]]] },
    l: { w: .22, s: [[[.08, 1.02], [.08, 0]]] },
    k: { w: .5, s: [[[.06, 1.02], [.06, 0]], [[.44, .6], [.06, .24]], [[.18, .34], [.46, 0]]] },
    W: { w: .94, s: [[[.02, 1], [.22, 0], [.47, .78], [.72, 0], [.92, 1]]] },
    t: { w: .34, s: [[[.14, .88], [.14, .12], [.2, .02], [.31, 0]], [[.02, .6], [.3, .6]]] },
    c: { w: .48, s: [[[.42, .46], [.32, .57], [.2, .6], [.08, .5], [.04, .3], [.08, .1], [.2, 0], [.32, .03], [.42, .14]]] },
    h: { w: .5, s: [[[.06, 1.02], [.06, 0]], [[.06, .4], [.14, .54], [.27, .6], [.38, .56], [.43, .44], [.43, 0]]] },
  };
  var WORD = 'TalkWatch';
  var TRACKING = .06;

  // Catmull-Rom through the points, sampled, so curves stay round at any size.
  function smooth(points) {
    if (points.length < 3) return points;
    var out = [];
    for (var i = 0; i < points.length - 1; i++) {
      var p0 = points[i - 1] || points[i], p1 = points[i], p2 = points[i + 1], p3 = points[i + 2] || p2;
      for (var t = 0; t < 1; t += .125) {
        var t2 = t * t, t3 = t2 * t;
        out.push([
          .5 * (2 * p1[0] + (-p0[0] + p2[0]) * t + (2 * p0[0] - 5 * p1[0] + 4 * p2[0] - p3[0]) * t2 + (-p0[0] + 3 * p1[0] - 3 * p2[0] + p3[0]) * t3),
          .5 * (2 * p1[1] + (-p0[1] + p2[1]) * t + (2 * p0[1] - 5 * p1[1] + 4 * p2[1] - p3[1]) * t2 + (-p0[1] + 3 * p1[1] - 3 * p2[1] + p3[1]) * t3),
        ]);
      }
    }
    out.push(points[points.length - 1]);
    return out;
  }

  // The word as strokes in pixels, each with its length, for an em of `size` with its baseline at (x, y).
  function layout(x, y, size) {
    var strokes = [], cursor = x;
    for (var i = 0; i < WORD.length; i++) {
      var glyph = GLYPHS[WORD[i]];
      glyph.s.forEach(function (stroke) {
        var pts = smooth(stroke).map(function (p) { return [cursor + p[0] * size, y - p[1] * size]; });
        var len = 0;
        for (var j = 1; j < pts.length; j++) len += Math.hypot(pts[j][0] - pts[j - 1][0], pts[j][1] - pts[j - 1][1]);
        strokes.push({ pts: pts, len: len });
      });
      cursor += (glyph.w + TRACKING) * size;
    }
    return { strokes: strokes, width: cursor - x, total: strokes.reduce(function (n, s) { return n + s.len; }, 0) };
  }

  function width(size) {
    var w = 0;
    for (var i = 0; i < WORD.length; i++) w += (GLYPHS[WORD[i]].w + TRACKING) * size;
    return w;
  }

  function reduced() {
    var forced = document.documentElement.dataset.motion;
    return forced === 'reduced' || forced === 'minimal' || matchMedia('(prefers-reduced-motion: reduce)').matches;
  }

  // Idle: nobody on a call, and the console connected (or a demo). Never over activity, never over a lost connection.
  function idle() {
    var nav = document.querySelector('.rail .signals[data-activity]');
    var fresh = document.querySelector('[data-fresh]');
    return !!nav && nav.getAttribute('data-activity') === '0' && !!fresh && fresh.getAttribute('data-state') === 'live';
  }

  // The empty stretch between the last workspace and search, in the rail's coordinates; null when there is none.
  function room(rail) {
    var signals = rail.querySelector('.signals'), end = rail.querySelector('.rail-end');
    if (!signals || !end) return null;
    var items = signals.children, last = items[items.length - 1];
    if (!last) return null;
    var r = rail.getBoundingClientRect();
    var from = Math.max.apply(null, Array.prototype.map.call(items, function (e) { return e.getBoundingClientRect().right; })) - r.left;
    var to = end.getBoundingClientRect().left - r.left;
    return to - from > 0 ? { from: from, to: to, height: r.height } : null;
  }

  function colour(name, fallback) {
    var v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return v || fallback;
  }

  var state = { canvas: null, playing: false, timer: 0 };

  function canvasFor(rail) {
    var box = rail.querySelector('#rail-current');
    if (!box) return null;
    var cv = box.querySelector('canvas.rail-signature');
    if (!cv) {
      cv = document.createElement('canvas');
      cv.className = 'rail-signature';
      cv.setAttribute('aria-hidden', 'true');
      box.appendChild(cv);
    }
    return cv;
  }

  // Draws the moment at progress p (0..1): rising out of the line, writing, holding, then fading back.
  function frame(ctx, plan, p, w, h, base) {
    var LEAD = .14, WRITE = .58, HOLD = .14;
    ctx.clearRect(0, 0, w, h);
    var stroke = colour('--live', '#3aa8e0');
    var fade = p < LEAD + WRITE + HOLD ? 1 : Math.max(0, 1 - (p - LEAD - WRITE - HOLD) / (1 - LEAD - WRITE - HOLD));
    var written = p < LEAD ? 0 : Math.min(1, (p - LEAD) / WRITE);
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.strokeStyle = stroke;
    ctx.globalAlpha = .9 * fade;
    ctx.shadowColor = stroke;
    ctx.shadowBlur = 6;
    ctx.lineWidth = 1.6;

    // The lead-in: the quiet line along the bottom swells and climbs toward the first stroke.
    var first = plan.strokes[0].pts[0];
    var rise = Math.min(1, p / LEAD);
    ctx.beginPath();
    var startX = plan.x - 34;
    for (var i = 0; i <= 40; i++) {
      var f = i / 40, x = startX + (first[0] - startX) * f;
      var climb = Math.pow(f, 2.2) * (base - first[1]) * rise;
      var wobble = Math.sin(f * 26 + p * 40) * 2.2 * (1 - f) * rise;
      var y = base - climb + wobble;
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.globalAlpha = .55 * fade * (written >= 1 ? Math.max(.25, 1 - (p - LEAD - WRITE) * 4) : 1);
    ctx.stroke();

    // The word, drawn by length; the pen's tip still trembles like a voice on the line.
    ctx.globalAlpha = .9 * fade;
    var budget = written * plan.total, tip = null;
    for (var s = 0; s < plan.strokes.length && budget > 0; s++) {
      var pts = plan.strokes[s].pts;
      ctx.beginPath();
      ctx.moveTo(pts[0][0], pts[0][1]);
      for (var k = 1; k < pts.length && budget > 0; k++) {
        var seg = Math.hypot(pts[k][0] - pts[k - 1][0], pts[k][1] - pts[k - 1][1]);
        if (seg <= budget) {
          ctx.lineTo(pts[k][0], pts[k][1]);
          tip = pts[k];
        } else {
          var t = budget / seg;
          tip = [pts[k - 1][0] + (pts[k][0] - pts[k - 1][0]) * t, pts[k - 1][1] + (pts[k][1] - pts[k - 1][1]) * t];
          ctx.lineTo(tip[0] + Math.sin(p * 120) * .8, tip[1] + Math.cos(p * 97) * .8);
        }
        budget -= seg;
      }
      ctx.stroke();
    }

    // The pen: a brighter point while it writes.
    if (tip && written < 1) {
      ctx.globalAlpha = fade;
      ctx.shadowBlur = 10;
      ctx.beginPath();
      ctx.arc(tip[0], tip[1], 1.8, 0, Math.PI * 2);
      ctx.fillStyle = stroke;
      ctx.fill();
    }

    ctx.globalAlpha = 1;
    ctx.shadowBlur = 0;
  }

  // Lays the word out in the room there is, or returns null when it would not fit.
  function plan(rail) {
    var space = room(rail);
    if (!space) return null;
    var size = Math.min(space.height * .5, 30);
    while (size >= 16 && width(size) + 80 > space.to - space.from) size -= 2;
    if (size < 16) return null;
    var w = width(size);
    var x = space.from + 46 + Math.random() * Math.max(0, space.to - space.from - w - 92);
    var base = space.height - 4;
    var laid = layout(x, base - 7, size);
    laid.x = x;
    laid.base = base;
    return laid;
  }

  /** Plays the signature once now, if there is room; returns whether it played. For a page that wants to show it. */
  function play() {
    var rail = document.querySelector('.rail.shell');
    if (!rail || state.playing || reduced()) return false;
    var cv = canvasFor(rail);
    var laid = cv && plan(rail);
    if (!laid) return false;
    var dpr = Math.min(2, devicePixelRatio || 1), r = cv.getBoundingClientRect();
    cv.width = Math.round(r.width * dpr);
    cv.height = Math.round(r.height * dpr);
    var ctx = cv.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    state.playing = true;
    var start = performance.now(), duration = 6800;
    (function step(now) {
      var p = Math.min(1, (now - start) / duration);
      frame(ctx, laid, p, r.width, r.height, laid.base);
      if (p < 1 && !document.hidden) requestAnimationFrame(step);
      else { ctx.clearRect(0, 0, r.width, r.height); state.playing = false; }
    })(start);
    return true;
  }

  // Now and then: a random wait, and only if the rail is idle when it comes round.
  function schedule() {
    clearTimeout(state.timer);
    state.timer = setTimeout(function () {
      if (!document.hidden && idle()) play();
      schedule();
    }, (3 + Math.random() * 9) * 60000);
  }

  TW.railSignature = play;
  // For checking a single moment by eye: draws progress p (0..1) and leaves it there.
  TW.railSignature.at = function (p) {
    var rail = document.querySelector('.rail.shell'), cv = rail && canvasFor(rail), laid = cv && plan(rail);
    if (!laid) return false;
    var dpr = Math.min(2, devicePixelRatio || 1), r = cv.getBoundingClientRect();
    cv.width = Math.round(r.width * dpr);
    cv.height = Math.round(r.height * dpr);
    var ctx = cv.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    frame(ctx, laid, p, r.width, r.height, laid.base);
    return true;
  };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', schedule); else schedule();
})();
