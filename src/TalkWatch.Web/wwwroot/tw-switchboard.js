// Operator's switchboards, as the Data Current. Each is drawn on the server as a node per stage (calls coming in, the
// menu, each option, where it puts calls through to), laid out as a tree, with the callers at each stage stacked under
// its node. This runs the particle field (TW.Field, tw-flow.js) along the streams that join each stage to the one it is
// reached from, denser the more of today's calls took it, and moves a caller from where they were to where they are
// now when Talk reports their next step: the card gathers into a capsule at its stage, rides the streams to the new
// one and opens there. The moves, timings and three-in-flight budget are Now's own (TW.continuity, from tw-board.js);
// under reduced motion the card arrives in place with a still wash.
(function () {
  'use strict';
  var TW = window.TW = window.TW || {};

  // Each stream's colour, as the design draws it: calls coming in, the menu's options, where an option puts calls
  // through to (live while someone is there), and hang-ups.
  function tone(stage) {
    var kind = stage.getAttribute('data-kind'), from = stage.getAttribute('data-from');
    if (kind === 'hungup') return 'c';
    if (!from || kind === 'menu' && stage.getAttribute('data-from') === 'in') return 'a';
    return kind === 'destination' && +stage.getAttribute('data-now') > 0 ? 'a' : 'b';
  }

  function Flow(root) {
    var self = this;
    this.root = root;
    this.canvas = root.querySelector('canvas');
    this.field = new TW.Field(this.canvas, { layer: 'data', interactive: true, speed: .9 });
    this.paths = {};
    this.cache = {};
    this.observer = new MutationObserver(function (records) { self.changed(records); });
    this.observer.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-now', 'data-today'] });
    this.resized = new ResizeObserver(function () { self.streams(); });
    this.resized.observe(root);
    this.streams();
    this.cacheRows();
  }

  Flow.prototype.destroy = function () {
    this.dead = true; this.observer.disconnect(); this.resized.disconnect(); this.field.destroy();
  };

  Flow.prototype.node = function (id) { return this.root.querySelector('.node[data-stage="' + id + '"]'); };

  // Each stage's stream, in the field's 0..1 units: out of the right of the node it is reached from, a gentle S, into
  // the left of this one. Calls coming in arrive from the flow's left edge. Loudness is today's calls through it, and
  // anyone there now counts for more.
  Flow.prototype.streams = function () {
    var self = this, box = this.root.getBoundingClientRect(), seen = {};
    if (!box.width || !box.height) return;
    function edge(el, side) { var r = el.getBoundingClientRect(); return [((side === 'out' ? r.right + 2 : r.left - 4) - box.left) / box.width, (r.top + r.height / 2 - box.top) / box.height]; }
    this.root.querySelectorAll('.node[data-stage]').forEach(function (stage) {
      var id = stage.getAttribute('data-stage'), from = stage.getAttribute('data-from'), source = from && self.node(from);
      var b = edge(stage, 'in'), a = source ? edge(source, 'out') : [0, b[1]];
      var mx = (a[0] + b[0]) / 2, dy = b[1] - a[1];
      self.field.set(id, { pts: [a, [mx, a[1] + dy * .15], [mx + .01, b[1] - dy * .15], b], value: +stage.getAttribute('data-today') + 3 * +stage.getAttribute('data-now'), busy: 6, max: 30, width: 12, tone: tone(stage) });
      seen[id] = true;
    });
    this.field.streams.forEach(function (st, id) { if (!seen[id]) self.field.remove(id); });
  };

  // The same streams in pixels on the page, for a caller to ride.
  Flow.prototype.measure = function () {
    var K = TW.continuity, origin = this.canvas.getBoundingClientRect(), paths = {}, self = this;
    this.origin = origin;
    function at(el, side) { var r = el.getBoundingClientRect(); return { x: (side === 'out' ? r.right : r.left) - origin.left, y: r.top + r.height / 2 - origin.top }; }
    this.root.querySelectorAll('.node[data-stage]').forEach(function (stage) {
      var id = stage.getAttribute('data-stage'), from = stage.getAttribute('data-from');
      if (!from) { var b = at(stage, 'in'); paths[id] = K.poly([{ x: 0, y: b.y }, b]); return; }
      var source = self.node(from);
      if (source) paths[id] = K.curve(at(source, 'out'), at(stage, 'in'), 'h');
    });
    this.paths = paths;
  };

  Flow.prototype.burst = function (ids, count) { var f = this.field; ids.forEach(function (id) { f.burst(id, count); }); };

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
    this.streams();
    this.cacheRows();
  };

  // The streams from one stage to another: down the chain of stages the target is reached from, back to the source.
  // A stage that is not on that chain is reached straight across.
  Flow.prototype.route = function (fromStage, toStage) {
    var chain = [], id = toStage, guard = 0;
    while (id && id !== fromStage && guard++ < 12) {
      if (!this.paths[id]) break;
      chain.unshift(this.paths[id]);
      var stage = this.node(id); id = stage && stage.getAttribute('data-from');
    }
    if (id === fromStage && chain.length) return { pts: [].concat.apply([], chain), ids: this.chainIds(fromStage, toStage) };
    var K = TW.continuity, a = this.node(fromStage), b = this.node(toStage);
    if (!a || !b) return null;
    var o = this.origin, ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
    return { pts: K.curve({ x: ra.right - o.left, y: ra.top + ra.height / 2 - o.top }, { x: rb.left - o.left, y: rb.top + rb.height / 2 - o.top }, 'h'), ids: [toStage] };
  };

  Flow.prototype.chainIds = function (fromStage, toStage) {
    var ids = [], id = toStage, guard = 0;
    while (id && id !== fromStage && guard++ < 12) { ids.unshift(id); var s = this.node(id); id = s && s.getAttribute('data-from'); }
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
    var name = document.createElement('span'); name.textContent = ((row.querySelector('b') || {}).textContent || '').trim();
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
        if (!rode) { rode = true; self.burst(route.ids, 10); }
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
    this.burst(this.chainIds(null, stage), 10);
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
