// Operator's switchboards. Each is drawn on the server as stages in columns (calls coming in, the menu, each option,
// where it puts calls through to), with the callers at each stage as rows. This draws the Data Current streams that
// join each stage to the one it is reached from, louder the more of today's calls took it, and moves a caller from
// where they were to where they are now when Talk reports their next step: the row gathers into a capsule at its stage,
// rides the streams to the new one and opens there. The moves, timings and three-in-flight budget are Now's own
// (TW.continuity, from tw-board.js); under reduced motion the row arrives in place with a still wash.
(function () {
  'use strict';
  var TW = window.TW = window.TW || {};

  // Each stage's stream colour: the menu and its options route calls, a stage with callers in it is live, a hang-up is bad.
  function tone(stage) {
    var kind = stage.getAttribute('data-kind');
    if (kind === 'hungup') return '--bad';
    if (kind === 'in') return '--live';
    return +stage.getAttribute('data-now') > 0 ? '--live' : '--route';
  }

  function Flow(root) {
    var self = this;
    this.root = root;
    this.canvas = root.querySelector('canvas.sb-current');
    this.ctx = this.canvas.getContext('2d');
    this.paths = {};
    this.cache = {};
    this.bursts = [];
    this.observer = new MutationObserver(function (records) { self.changed(records); });
    this.observer.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-now', 'data-today'] });
    this.frame = this.frame.bind(this);
    this.onWake = function () { self.kick(); };
    addEventListener('resize', this.onWake);
    document.addEventListener('visibilitychange', this.onWake);
    this.cacheRows();
    this.kick();
  }

  Flow.prototype.kick = function () {
    if (this.dead) return;
    if (!this.running) { this.running = true; requestAnimationFrame(this.frame); }
  };

  Flow.prototype.destroy = function () {
    this.dead = true; this.observer.disconnect();
    removeEventListener('resize', this.onWake); document.removeEventListener('visibilitychange', this.onWake);
  };

  // Stage to stage: out of the right of the stage it is reached from, into the left of this one, in the canvas's own
  // coordinates. Calls coming in arrive from the plane's left edge.
  Flow.prototype.measure = function () {
    var K = TW.continuity, origin = this.canvas.getBoundingClientRect(), paths = {}, self = this;
    this.origin = origin;
    function at(el, side) { var r = el.getBoundingClientRect(); return { x: (side === 'out' ? r.right : r.left) - origin.left, y: r.top + Math.min(r.height / 2, 18) - origin.top }; }
    this.root.querySelectorAll('[data-stage]').forEach(function (stage) {
      var id = stage.getAttribute('data-stage'), node = stage.querySelector('.sb-node') || stage, from = stage.getAttribute('data-from');
      if (!from) { var b = at(node, 'in'); paths[id] = K.poly([{ x: 0, y: b.y }, b]); return; }
      var source = self.root.querySelector('[data-stage="' + from + '"] .sb-node');
      if (source) paths[id] = K.curve(at(source, 'out'), at(node, 'in'), 'h');
    });
    this.paths = paths;
  };

  Flow.prototype.frame = function (now) {
    var K = TW.continuity;
    if (!this.root.isConnected || !K) { this.destroy(); return; }
    var still = K.reduced() || document.hidden, c = this.canvas, dpr = Math.min(2, devicePixelRatio || 1);
    var w = c.clientWidth, h = c.clientHeight;
    if (c.width !== Math.round(w * dpr) || c.height !== Math.round(h * dpr)) { c.width = Math.round(w * dpr); c.height = Math.round(h * dpr); }
    this.measure(); this.cacheRows();
    var ctx = this.ctx, self = this, t = still ? 0 : now / 1000;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0); ctx.clearRect(0, 0, w, h); ctx.lineCap = 'round'; ctx.lineJoin = 'round';
    this.bursts = this.bursts.filter(function (b) { return now - b.t0 < b.dur; });
    this.root.querySelectorAll('[data-stage]').forEach(function (stage, i) {
      var id = stage.getAttribute('data-stage'), pts = self.paths[id]; if (!pts) return;
      // Loudness: today's calls through it, and anyone there now counts for more.
      var n = K.normalise(+stage.getAttribute('data-today') + 3 * +stage.getAttribute('data-now'), 6);
      var bursts = self.bursts.filter(function (b) { return b.id === id; }).map(function (b) { return { at: K.inOut(Math.min(1, (now - b.t0) / b.dur)), miss: b.miss }; });
      K.speak(ctx, pts, Math.max(.06, n), t, K.css(tone(stage)) || '#888', { seed: 7 + i * 13, swing: 5, single: false, alpha: .9, bursts: bursts, live: 1 });
    });
    if (still) { this.running = false; return; }
    requestAnimationFrame(this.frame);
  };

  // Where every caller's row was, to ride from when it moves.
  Flow.prototype.cacheRows = function () {
    var cache = {};
    this.root.querySelectorAll('[data-stage-list] > [data-key]').forEach(function (row) {
      cache[row.getAttribute('data-key')] = { rect: row.getBoundingClientRect(), stage: row.parentElement.getAttribute('data-stage-list'), node: row };
    });
    this.cache = cache;
  };

  Flow.prototype.changed = function (records) {
    var self = this, came = {}, gone = {};
    records.forEach(function (r) {
      if (r.type !== 'childList' || !(r.target.matches && r.target.matches('[data-stage-list]'))) return;
      r.removedNodes.forEach(function (n) { var k = n.nodeType === 1 && n.getAttribute('data-key'); if (k) gone[k] = true; });
      r.addedNodes.forEach(function (n) { var k = n.nodeType === 1 && n.getAttribute('data-key'); if (k && n.isConnected) came[k] = n; });
    });
    Object.keys(came).forEach(function (key) {
      var row = came[key], was = self.cache[key], to = row.parentElement.getAttribute('data-stage-list');
      if (was && was.stage !== to) self.ride(was, row, to);
      else if (!was) self.arrive(row, to);
    });
    Object.keys(gone).forEach(function (key) { if (!came[key] && self.cache[key] && !document.querySelector('[data-stage-list] > [data-key="' + key + '"]')) self.leave(self.cache[key]); });
    this.cacheRows();
    this.kick();
  };

  // The streams from one stage to another: down the chain of stages the target is reached from, back to the source.
  // A stage that is not on that chain is reached straight across.
  Flow.prototype.route = function (fromStage, toStage) {
    var chain = [], id = toStage, guard = 0;
    while (id && id !== fromStage && guard++ < 12) {
      if (!this.paths[id]) break;
      chain.unshift(this.paths[id]);
      var stage = this.root.querySelector('[data-stage="' + id + '"]'); id = stage && stage.getAttribute('data-from');
    }
    if (id === fromStage && chain.length) return { pts: [].concat.apply([], chain), ids: this.chainIds(fromStage, toStage) };
    var K = TW.continuity, a = this.root.querySelector('[data-stage="' + fromStage + '"] .sb-node'), b = this.root.querySelector('[data-stage="' + toStage + '"] .sb-node');
    if (!a || !b) return null;
    var o = this.origin, ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
    return { pts: K.curve({ x: ra.right - o.left, y: ra.top + 18 - o.top }, { x: rb.left - o.left, y: rb.top + 18 - o.top }, 'h'), ids: [toStage] };
  };

  Flow.prototype.chainIds = function (fromStage, toStage) {
    var ids = [], id = toStage, guard = 0;
    while (id && id !== fromStage && guard++ < 12) { ids.unshift(id); var s = this.root.querySelector('[data-stage="' + id + '"]'); id = s && s.getAttribute('data-from'); }
    return ids;
  };

  // A caller takes their next step: gather into a capsule at their stage, ride the streams, open into their new row.
  Flow.prototype.ride = function (was, row, toStage) {
    var K = TW.continuity, self = this;
    this.measure();
    var route = this.route(was.stage, toStage);
    if (!K.canFly() || !route || !K.visible(was.rect) || !K.visible(row.getBoundingClientRect())) { K.flash(row); return; }
    K.take();
    row.classList.add('tw-await');
    var ghost = document.createElement('div'); ghost.className = 'tw-ghost'; ghost.setAttribute('aria-hidden', 'true');
    var copy = K.copyOf(was.node); copy.style.width = was.rect.width + 'px';
    var cap = document.createElement('span'); cap.className = 'tw-cap';
    var mark = row.querySelector('.st'); if (mark) cap.appendChild(K.copyOf(mark));
    var name = document.createElement('span'); name.textContent = ((row.querySelector('.who b') || {}).textContent || '').trim();
    cap.appendChild(name);
    var dst = K.copyOf(row);
    ghost.appendChild(copy); ghost.appendChild(cap); ghost.appendChild(dst);
    document.body.appendChild(ghost);
    var ringing = row.querySelector('.st[data-s="ringing"]'), colour = K.css(ringing ? '--live' : '--route');
    var cw = cap.offsetWidth, ch = 26, pts = route.pts, o = this.origin;
    ghost.style.borderColor = colour; cap.style.opacity = 0; dst.style.opacity = 0; copy.style.opacity = .55;
    function box(l, t, w, h, r) { ghost.style.transform = 'translate(' + l + 'px,' + t + 'px)'; ghost.style.width = w + 'px'; ghost.style.height = h + 'px'; ghost.style.borderRadius = r + 'px'; }
    var A = 170, B = 440, C = 210, t0 = performance.now(), rode = false;
    function step(now) {
      var el = now - t0, to = row.getBoundingClientRect(), p0 = pts[0], p1 = pts[pts.length - 1];
      o = self.canvas.getBoundingClientRect();
      if (!row.isConnected) el = A + B + C;
      if (el < A) {
        var e = K.ease(el / A);
        box(K.lerp(was.rect.left, o.left + p0.x - cw / 2, e), K.lerp(was.rect.top, o.top + p0.y - ch / 2, e), K.lerp(was.rect.width, cw, e), K.lerp(was.rect.height, ch, e), K.lerp(5, 13, e));
        copy.style.opacity = .55 * (1 - e); cap.style.opacity = e;
      } else if (el < A + B) {
        if (!rode) { rode = true; route.ids.forEach(function (id) { self.bursts.push({ id: id, t0: now, dur: B }); }); self.kick(); }
        var p = pts[Math.round(K.inOut((el - A) / B) * (pts.length - 1))];
        box(o.left + p.x - cw / 2, o.top + p.y - ch / 2, cw, ch, 13); copy.style.opacity = 0; cap.style.opacity = 1;
      } else if (el < A + B + C) {
        var f = K.ease((el - A - B) / C);
        box(K.lerp(o.left + p1.x - cw / 2, to.left, f), K.lerp(o.top + p1.y - ch / 2, to.top, f), K.lerp(cw, to.width, f), K.lerp(ch, to.height, f), K.lerp(13, 5, f));
        cap.style.opacity = 1 - f; dst.style.opacity = f; dst.style.width = to.width + 'px';
        if (f > .5) ghost.style.borderColor = '';
      }
      if (el < A + B + C) { requestAnimationFrame(step); return; }
      ghost.remove(); K.done();
      row.classList.remove('tw-await'); K.flash(row);
    }
    requestAnimationFrame(step);
  };

  // A new caller comes in: a pulse runs in along their stage's stream, and the row appears as it lands.
  Flow.prototype.arrive = function (row, stage) {
    var K = TW.continuity, self = this;
    if (K.reduced() || !K.visible(row.getBoundingClientRect())) { K.flash(row); return; }
    row.classList.add('tw-await');
    var ids = this.chainIds(null, stage);
    ids.forEach(function (id) { self.bursts.push({ id: id, t0: performance.now(), dur: 520 }); });
    this.kick();
    setTimeout(function () { row.classList.remove('tw-await'); row.classList.add('entering'); setTimeout(function () { row.classList.remove('entering'); }, 600); }, 520);
  };

  // A caller who leaves the switchboard (answered, or hung up) slides 14px right and fades, as rows leave on Now.
  Flow.prototype.leave = function (was) {
    var K = TW.continuity;
    if (K.reduced() || !K.visible(was.rect)) return;
    var c = K.copyOf(was.node); c.setAttribute('aria-hidden', 'true');
    c.style.cssText += ';position:fixed;left:' + was.rect.left + 'px;top:' + was.rect.top + 'px;width:' + was.rect.width + 'px;height:' + was.rect.height + 'px;margin:0;z-index:40';
    document.body.appendChild(c);
    c.animate([{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'translateX(14px)' }], { duration: 320, easing: K.EASE_IN, fill: 'forwards' }).finished
      .catch(function () { }).then(function () { c.remove(); });
  };

  // ---------- wiring ----------
  var flows = [];
  function scan() {
    flows = flows.filter(function (f) { if (f.root.isConnected) return true; f.destroy(); return false; });
    document.querySelectorAll('[data-sb-flow]').forEach(function (root) {
      if (!flows.some(function (f) { return f.root === root; })) flows.push(new Flow(root));
    });
  }
  var pending = false;
  new MutationObserver(function () {
    if (pending) return; pending = true;
    requestAnimationFrame(function () { pending = false; scan(); });
  }).observe(document.documentElement, { childList: true, subtree: true });
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', scan); else scan();
})();
