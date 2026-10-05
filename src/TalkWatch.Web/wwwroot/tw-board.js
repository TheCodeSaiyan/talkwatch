// The Now view's flow board and Continuity. The planes on Now are nodes; this draws the Data Current streams that join
// them at their ports (calls streaming in, leaving Live calls by how they ended, dropping into the call log), and moves
// what changes from where it was to where it lands: a finished call rides its stream into Recent activity, a missed
// call's mark rides up to the rail's count, rows slide, counts roll. The page is rendered on the server; this only
// watches what the server changes and explains it. It never holds a value back for longer than the move that explains
// it, and under reduced motion nothing travels: changes arrive in place with a still wash.
(function () {
  'use strict';
  var TW = window.TW = window.TW || {};

  // ---------- shared ----------
  function reduced() {
    var forced = document.documentElement.dataset.motion;
    if (forced === 'reduced' || forced === 'minimal') return true;
    return matchMedia('(prefers-reduced-motion: reduce)').matches;
  }
  // A CSS cubic-bezier as a function of time, so script-driven moves ease exactly as the stylesheet's do.
  function bezier(x1, y1, x2, y2) {
    return function (t) {
      var u = t;
      for (var i = 0; i < 8; i++) {
        var x = 3 * (1 - u) * (1 - u) * u * x1 + 3 * (1 - u) * u * u * x2 + u * u * u - t;
        var dx = 3 * (1 - u) * (1 - u) * x1 + 6 * (1 - u) * u * (x2 - x1) + 3 * u * u * (1 - x2);
        if (Math.abs(dx) < 1e-6) break;
        u = Math.max(0, Math.min(1, u - x / dx));
      }
      return 3 * (1 - u) * (1 - u) * u * y1 + 3 * (1 - u) * u * u * y2 + u * u * u;
    };
  }
  var ease = bezier(.2, .7, .2, 1), inOut = bezier(.45, 0, .25, 1);
  var EASE = 'cubic-bezier(.2, .7, .2, 1)', EASE_IN = 'cubic-bezier(.5, 0, .75, 0)';
  function lerp(a, b, t) { return a + (b - a) * t; }
  function css(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }
  function visible(r) { return !!r && r.width > 0 && r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth; }
  function centre(el) { var r = el.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; }
  function normalise(v, busy) { return TW.normalise ? TW.normalise(v, busy) : Math.min(1, Math.log1p(Math.max(0, v)) / Math.log1p(busy * 4)); }
  // A copy that travels: not read out, not focusable, no ids to collide with the real thing.
  function copyOf(el) {
    var c = el.cloneNode(true);
    c.removeAttribute('id'); c.removeAttribute('tabindex');
    c.querySelectorAll('[id],[tabindex]').forEach(function (n) { n.removeAttribute('id'); n.removeAttribute('tabindex'); });
    c.classList.remove('tw-await', 'changed', 'entering');
    return c;
  }
  // A change that arrives in place: the changed flash, or a still wash when motion is reduced.
  function flash(el) {
    if (!el || !el.isConnected) return;
    var cls = reduced() ? 'tw-wash' : 'changed';
    el.classList.remove(cls); void el.offsetWidth; el.classList.add(cls);
    setTimeout(function () { el.classList.remove(cls); }, 1150);
  }

  // ---------- what is in flight, and the counts that wait for it ----------
  // At most three rides and signals at once; anything beyond snaps into place. A count something is travelling to
  // changes when it lands, never before, and never later than the move.
  var flying = 0;
  var waits = {};
  function canFly() { return !reduced() && flying < 3; }
  function holdCounts(keys, ms) { var at = performance.now() + ms; keys.forEach(function (k) { waits[k] = Math.max(waits[k] || 0, at); }); }
  function landCounts(keys) { keys.forEach(function (k) { delete waits[k]; }); rollAll(keys); }

  // ---------- rolling counts ----------
  // [data-roll] holds the figure in a .rv span the server renders; beside it an odometer shows it, rolling the digits
  // that change, upwards for more and downwards for fewer. data-better says which way is good for the delta chip.
  var rolls = [];
  function isNum(s) { return /^-?\d+$/.test(s); }
  function digitsHtml(s) { return s.split('').map(function (ch) { return '<span class="d"><span>' + (ch === ' ' ? '&nbsp;' : ch) + '</span></span>'; }).join(''); }
  function setupRoll(el) {
    if (el._roll) return;
    var rv = el.querySelector(':scope > .rv'); if (!rv) return;
    el._roll = true; el._rv = rv;
    var odo = document.createElement('span'); odo.className = 'odo'; odo.setAttribute('aria-hidden', 'true');
    el.appendChild(odo); el._odo = odo;
    el._shown = rv.textContent.trim(); odo.innerHTML = digitsHtml(el._shown);
    // A page is drawn at its first wait, with nothing read yet, and redrawn a moment later with the figures: that is
    // the page arriving, not the count changing, so the first two seconds swap without a roll or a chip.
    el._born = performance.now();
    el.classList.add('on');
    rolls.push(el);
    // A tick later, so a move that explains the change has had the chance to say when it will land.
    new MutationObserver(function () { setTimeout(function () { queueRoll(el); }, 0); }).observe(rv, { characterData: true, childList: true, subtree: true });
  }
  function queueRoll(el) {
    var at = waits[el.getAttribute('data-roll')] || 0, now = performance.now();
    if (at > now) { clearTimeout(el._wait); el._wait = setTimeout(function () { roll(el); }, at - now + 20); return; }
    roll(el);
  }
  function rollAll(keys) { rolls.forEach(function (el) { if (el.isConnected && keys.indexOf(el.getAttribute('data-roll')) >= 0) roll(el); }); }
  function roll(el) {
    if (!el.isConnected) return;
    var key = el.getAttribute('data-roll');
    if ((waits[key] || 0) > performance.now()) return;
    var v = el._rv.textContent.trim(), prev = el._shown;
    if (v === prev) return;
    el._shown = v;
    var odo = el._odo, tick = el._tick = (el._tick || 0) + 1;
    if (performance.now() - el._born < 2000) { odo.innerHTML = digitsHtml(v); return; }
    if (reduced() || !isNum(v) || !isNum(prev)) {
      odo.innerHTML = digitsHtml(v);
    } else {
      var up = +v > +prev, n = Math.max(v.length, prev.length), a = prev.padStart(n, ' '), b = v.padStart(n, ' '), done = [];
      odo.innerHTML = '';
      for (var i = 0; i < n; i++) {
        var d = document.createElement('span'); d.className = 'd';
        if (a[i] === b[i]) { d.innerHTML = '<span>' + (b[i] === ' ' ? '&nbsp;' : b[i]) + '</span>'; odo.appendChild(d); continue; }
        var col = document.createElement('span'); col.className = 'col';
        var top = up ? a[i] : b[i], bottom = up ? b[i] : a[i];
        col.innerHTML = '<span>' + (top === ' ' ? '&nbsp;' : top) + '</span><span>' + (bottom === ' ' ? '&nbsp;' : bottom) + '</span>';
        d.appendChild(col); odo.appendChild(d);
        var frames = up ? [{ transform: 'translateY(0)' }, { transform: 'translateY(-1.15em)' }] : [{ transform: 'translateY(-1.15em)' }, { transform: 'translateY(0)' }];
        done.push(col.animate(frames, { duration: 460, delay: (n - 1 - i) * 40, easing: EASE, fill: 'forwards' }).finished.catch(function () { }));
      }
      Promise.all(done).then(function () { if (el._tick === tick) odo.innerHTML = digitsHtml(v); });
    }
    // The chip says whether the change is good. No data-better, no chip: a count with no meaning to its direction.
    var better = el.getAttribute('data-better');
    if (better && isNum(v) && isNum(prev)) {
      var diff = +v - +prev, old = el.querySelector(':scope > .delta'); if (old) old.remove();
      var chip = document.createElement('span');
      chip.className = 'delta ' + (better === 'none' ? 'neutral' : ((diff > 0) === (better === 'up') ? 'good' : 'bad'));
      chip.setAttribute('aria-hidden', 'true');
      chip.textContent = (diff > 0 ? '+' : '−') + Math.abs(diff);
      el.appendChild(chip);
      setTimeout(function () { chip.remove(); }, 1700);
    }
    // A rail count bumps as it changes.
    var ws = el.closest('.ws');
    if (ws) { ws.classList.remove('bump'); void ws.offsetWidth; ws.classList.add('bump'); setTimeout(function () { ws.classList.remove('bump'); }, 1100); }
  }

  // ---------- drawing a stream ----------
  // Paths are lists of points about 2px apart with their normals, in window coordinates: the canvas covers the window.
  function withNormals(pts) {
    for (var j = 0; j < pts.length; j++) {
      var p = pts[Math.max(0, j - 1)], q = pts[Math.min(pts.length - 1, j + 1)], l = Math.hypot(q.x - p.x, q.y - p.y) || 1;
      pts[j].nx = -(q.y - p.y) / l; pts[j].ny = (q.x - p.x) / l;
    }
    return pts;
  }
  // A smooth S between two ports, leaving and arriving horizontally (h) or vertically (v).
  function curve(a, b, dir) {
    var n = Math.max(8, Math.round(Math.hypot(b.x - a.x, b.y - a.y) * 1.15 / 2)), pts = [];
    var c1 = dir === 'h' ? { x: a.x + (b.x - a.x) / 2, y: a.y } : { x: a.x, y: a.y + (b.y - a.y) / 2 };
    var c2 = dir === 'h' ? { x: b.x - (b.x - a.x) / 2, y: b.y } : { x: b.x, y: b.y - (b.y - a.y) / 2 };
    for (var i = 0; i <= n; i++) {
      var t = i / n, u = 1 - t;
      pts.push({ x: u * u * u * a.x + 3 * u * u * t * c1.x + 3 * u * t * t * c2.x + t * t * t * b.x, y: u * u * u * a.y + 3 * u * u * t * c1.y + 3 * u * t * t * c2.y + t * t * t * b.y });
    }
    return withNormals(pts);
  }
  // Straight runs through corners, for the inlet and the missed branch.
  function poly(corners) {
    var pts = [];
    for (var k = 0; k < corners.length - 1; k++) {
      var a = corners[k], b = corners[k + 1], n = Math.max(2, Math.round(Math.hypot(b.x - a.x, b.y - a.y) / 2));
      for (var i = k ? 1 : 0; i <= n; i++) pts.push({ x: lerp(a.x, b.x, i / n), y: lerp(a.y, b.y, i / n) });
    }
    return withNormals(pts);
  }
  function hash(i) { var s = Math.sin(i * 127.1 + 311.7) * 43758.5453; return s - Math.floor(s); }
  function vnoise(x) { var i = Math.floor(x), f = x - i, s = f * f * (3 - 2 * f); return hash(i) + (hash(i + 1) - hash(i)) * s; }
  // The speech envelope: syllables and pauses; a busy route barely pauses.
  function envelope(v, n) { var th = .62 - .44 * n, x = vnoise(v) * .7 + vnoise(v * 2.3 + 7) * .3, e = Math.max(0, Math.min(1, (x - th) / .22)); return .07 + .93 * e * e * (3 - 2 * e); }
  var HEIGHTS = [1, .45, .8, .3, .62];
  // One route drawn as voices on an audio meter: louder and less paused the busier it is, up to five voices; a burst
  // (a call riding it) runs along as a louder syllable, jagged on the missed stream.
  function speak(ctx, pts, n, t, colour, o) {
    if (!pts || pts.length < 4) return;
    var speed = 24 + 84 * n, voices = o.single ? 1 : 1 + Math.round(n * 4), L = (pts.length - 1) * 2;
    ctx.strokeStyle = colour; ctx.lineWidth = o.single ? .8 : .8 + .7 * n;
    for (var li = 0; li < voices; li++) {
      ctx.beginPath();
      for (var i = 0; i < pts.length; i++) {
        var d = i * 2, u = d - t * speed, e = envelope(u * .03 + li * 17 + o.seed, n), dist = 0;
        for (var b = 0; b < o.bursts.length; b++) { var g = Math.exp(-Math.pow((d - o.bursts[b].at * L) / 24, 2)); e += g; if (o.bursts[b].miss) dist = Math.max(dist, .7 * g); }
        var taper = Math.min(1, d / 12, (L - d) / 12);
        var w = Math.sin(u * .85 + li * 3 + Math.sin(u * .21 + li) * 2.4) * (.55 + .45 * Math.sin(u * 1.9 + li * 5));
        if (dist > .02) w = Math.max(-1, Math.min(1, w * (1 + 4 * dist) + dist * Math.sin(u * 5.3)));
        var y = w * e * o.swing * (.12 + .88 * n) * HEIGHTS[li % 5] * taper * o.live, p = pts[i];
        if (i) ctx.lineTo(p.x + p.nx * y, p.y + p.ny * y); else ctx.moveTo(p.x + p.nx * y, p.y + p.ny * y);
      }
      ctx.globalAlpha = (li ? .45 : .9) * (o.alpha || 1);
      ctx.stroke();
    }
    ctx.globalAlpha = 1;
  }

  // ---------- the board ----------
  // Each stream: its colour token, its "busy" point in calls a minute, and a seed so no two talk in step.
  var STREAMS = {
    inlet: { tone: '--live', busy: 2.5, seed: 11 },
    answered: { tone: '--ok', busy: 2, seed: 3 },
    voicemail: { tone: '--route', busy: .6, seed: 41 },
    missed: { tone: '--bad', busy: .6, seed: 77 },
    branch: { tone: '--bad', busy: .6, seed: 93 },
    archive: { tone: '--hist', busy: 2.5, seed: 57 },
  };
  var EDGES = { live: { tone: '--live', busy: 4 }, recent: { tone: '--ok', busy: 2.5 }, log: { tone: '--hist', busy: 2.5 } };
  var dotnet = null, connectedAt = 0;

  function Board(root) {
    var self = this;
    this.root = root;
    this.born = performance.now();
    this.canvas = document.createElement('canvas');
    this.canvas.className = 'board-current'; this.canvas.setAttribute('aria-hidden', 'true');
    document.body.appendChild(this.canvas);
    this.ctx = this.canvas.getContext('2d');
    this.level = {}; this.target = {}; this.live = 1;
    Object.keys(STREAMS).concat(Object.keys(EDGES)).forEach(function (k) { self.level[k] = 0; self.target[k] = 0; });
    this.bursts = [];
    this.paths = {};
    this.cache = {};       // 'list:key' -> the row's rectangle at the last frame
    this.signalled = {};
    this.held = {}; this.letGo = {};
    this.colours(); this.readRates();
    this.observer = new MutationObserver(function (records) { self.changed(records); });
    this.observer.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-rates', 'data-still', 'data-signal'] });
    this.frame = this.frame.bind(this);
    this.onPointer = function (e) { self.hover = e.type === 'pointerover' ? e.target : e.relatedTarget; self.pointing(); };
    this.onFocus = function () { setTimeout(function () { self.pointing(); }, 0); };
    document.addEventListener('pointerover', this.onPointer);
    document.addEventListener('pointerout', this.onPointer);
    document.addEventListener('focusin', this.onFocus);
    document.addEventListener('focusout', this.onFocus);
    this.onWake = function () { self.kick(); };
    document.addEventListener('visibilitychange', this.onWake);
    addEventListener('resize', this.onWake); addEventListener('scroll', this.onWake, { passive: true });
    this.kick();
  }

  Board.prototype.colours = function () {
    this.col = {}; var c = this.col;
    ['--live', '--ok', '--route', '--bad', '--hist'].forEach(function (n) { c[n] = css(n) || '#888'; });
    this.colouredAt = performance.now();
  };

  Board.prototype.readRates = function () {
    var r = {}; try { r = JSON.parse(this.root.getAttribute('data-rates') || '{}'); } catch (e) { }
    var t = this.target;
    Object.keys(STREAMS).forEach(function (k) { t[k] = normalise(+r[k === 'branch' ? 'missed' : k] || 0, STREAMS[k].busy) * (k === 'branch' ? .7 : 1); });
    t.live = normalise(+r.live || 0, EDGES.live.busy);
    t.recent = normalise((+r.answered || 0) + (+r.voicemail || 0) + (+r.missed || 0), EDGES.recent.busy);
    t.log = normalise(+r.archive || 0, EDGES.log.busy);
    this.still = this.root.getAttribute('data-still') === 'true';
  };

  Board.prototype.kick = function () {
    if (this.dead) return;
    if (!this.running) { this.running = true; this.last = performance.now(); requestAnimationFrame(this.frame); }
  };

  Board.prototype.destroy = function () {
    this.dead = true; this.observer.disconnect(); this.canvas.remove();
    document.removeEventListener('pointerover', this.onPointer); document.removeEventListener('pointerout', this.onPointer);
    document.removeEventListener('focusin', this.onFocus); document.removeEventListener('focusout', this.onFocus);
    document.removeEventListener('visibilitychange', this.onWake);
    removeEventListener('resize', this.onWake); removeEventListener('scroll', this.onWake);
    var self = this;
    Object.keys(this.held).forEach(function (k) { if (self.held[k]) send(k, false); });
    if (board === this) board = null;
  };

  // Every frame the streams are rebuilt from where the ports really are, so they follow scrolling, resizing and layout.
  Board.prototype.measure = function () {
    var root = this.root, ports = {};
    root.querySelectorAll('[data-port]').forEach(function (p) { ports[p.getAttribute('data-port')] = p; });
    if (!ports.inlet || !ports.in || !ports.archive || !ports.log) return false;
    // The call log's port sits directly below Recent activity's archive port, whatever the columns.
    var logPlane = ports.log.parentElement.getBoundingClientRect(), ax = centre(ports.archive).x;
    ports.log.style.left = (ax - logPlane.left - 4.5) + 'px';
    var narrow = matchMedia('(max-width: 999px)').matches, P = {};
    Object.keys(ports).forEach(function (k) { P[k] = centre(ports[k]); });
    var paths = this.paths;
    paths.inlet = poly([{ x: 0, y: P.inlet.y }, P.inlet]);
    ['answered', 'voicemail', 'missed'].forEach(function (k) { if (P[k]) paths[k] = curve(P[k], P.in, narrow ? 'v' : 'h'); });
    paths.archive = curve(P.archive, P.log, 'v');
    // The missed branch leaves the missed stream halfway and climbs the open gutter to the rail (narrow: the right edge).
    var rail = document.querySelector('.rail.shell'), top = rail ? rail.getBoundingClientRect().bottom : 0;
    var m = paths.missed, from = m && m[Math.floor(m.length / 2)];
    if (from) {
      var right = root.getBoundingClientRect().right + 8;
      paths.branch = narrow ? poly([from, { x: right, y: from.y }, { x: right, y: top }]) : poly([from, { x: from.x, y: top }]);
    }
    return true;
  };

  Board.prototype.frame = function (now) {
    if (!this.root.isConnected) { this.destroy(); return; }
    var still = reduced() || document.hidden;
    var dt = Math.min(.05, (now - this.last) / 1000); this.last = now;
    if (now - this.colouredAt > 1000) this.colours();
    var k = still ? 1 : 1 - Math.exp(-dt * 3.2), lv = this.level, tg = this.target;
    Object.keys(lv).forEach(function (s) { lv[s] += (tg[s] - lv[s]) * k; });
    this.live += ((this.still ? 0 : 1) - this.live) * (still ? 1 : 1 - Math.exp(-dt * 1.2));
    this.cacheRows();
    if (this.measure()) this.draw(still ? 0 : now / 1000, now);
    this.bursts = this.bursts.filter(function (b) { return now - b.t0 < b.dur; });
    // Reduced motion and a hidden tab draw once and stop; a change, a scroll or a resize draws again.
    if (still) { this.running = false; return; }
    requestAnimationFrame(this.frame);
  };

  Board.prototype.draw = function (t, now) {
    var c = this.canvas, dpr = Math.min(2, devicePixelRatio || 1), w = innerWidth, h = innerHeight, ctx = this.ctx, self = this;
    if (c.width !== Math.round(w * dpr) || c.height !== Math.round(h * dpr)) { c.width = Math.round(w * dpr); c.height = Math.round(h * dpr); }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0); ctx.clearRect(0, 0, w, h); ctx.lineCap = 'round'; ctx.lineJoin = 'round';
    Object.keys(STREAMS).forEach(function (s) {
      var bursts = self.bursts.filter(function (b) { return b.s === s; }).map(function (b) { return { at: inOut(Math.min(1, (now - b.t0) / b.dur)), miss: b.miss }; });
      speak(ctx, self.paths[s], self.level[s], t, self.col[STREAMS[s].tone], { seed: STREAMS[s].seed, swing: s === 'branch' ? 4 : 6, single: s === 'branch', alpha: s === 'branch' ? .7 : 1, bursts: bursts, live: self.live });
    });
    // Edge currents: one quiet voice along the bottom of each node's head, from that node's own activity.
    this.root.querySelectorAll('canvas.edge').forEach(function (e) {
      var name = e.getAttribute('data-edge'), spec = EDGES[name]; if (!spec) return;
      var r = e.getBoundingClientRect(); if (!visible(r)) return;
      if (e.width !== Math.round(r.width * dpr)) { e.width = Math.round(r.width * dpr); e.height = Math.round(r.height * dpr); }
      var x = e.getContext('2d'); x.setTransform(dpr, 0, 0, dpr, 0, 0); x.clearRect(0, 0, r.width, r.height); x.lineCap = 'round';
      speak(x, poly([{ x: 8, y: r.height / 2 }, { x: r.width - 8, y: r.height / 2 }]), self.level[name], t, self.col[spec.tone], { seed: name.length * 13, swing: 2, single: true, alpha: .6, bursts: [], live: self.live });
    });
  };

  // Where every row was at the last frame: a change compares against it, so rows slide from where they were.
  Board.prototype.cacheRows = function () {
    var cache = {};
    this.root.querySelectorAll('[data-hold]').forEach(function (list) {
      var name = list.getAttribute('data-hold');
      for (var i = 0; i < list.children.length; i++) {
        var row = list.children[i], key = row.getAttribute('data-key');
        if (key) cache[name + ':' + key] = row.getBoundingClientRect();
      }
    });
    this.cache = cache;
  };

  // ---------- what the server changed ----------
  Board.prototype.changed = function (records) {
    var self = this, gone = {}, came = {}, signals = [], rates = false;
    // Until the page's circuit is up, and a moment after, changes are the page being drawn, not the system moving.
    var settling = !connectedAt || performance.now() - Math.max(connectedAt, this.born) < 600;
    records.forEach(function (r) {
      if (r.type === 'attributes') {
        if (r.attributeName === 'data-signal') { if (r.target.getAttribute('data-signal') === 'missed') signals.push(r.target); }
        else rates = true;
        return;
      }
      if (!(r.target.matches && r.target.matches('[data-hold]'))) return;
      var list = r.target.getAttribute('data-hold');
      r.removedNodes.forEach(function (n) { var k = n.nodeType === 1 && n.getAttribute('data-key'); if (k) gone[list + ':' + k] = { node: n, list: list, key: k }; });
      r.addedNodes.forEach(function (n) { var k = n.nodeType === 1 && n.getAttribute('data-key'); if (k) came[list + ':' + k] = { node: n, list: list, key: k }; });
    });
    if (rates) this.readRates();
    // A keyed row the server moved is taken out and put back: still here, so it slides rather than leaves.
    Object.keys(gone).forEach(function (id) { if (gone[id].node.isConnected) { delete gone[id]; delete came[id]; } });
    Object.keys(came).forEach(function (id) { if (!came[id].node.isConnected) delete came[id]; });
    var arrived = {};
    Object.keys(came).forEach(function (id) { arrived[came[id].node.getAttribute('data-key')] = true; });

    if (!settling) {
      // Slides first, measured against the last frame, before anything is hidden or flown.
      this.slide(came);
      Object.keys(came).forEach(function (id) {
        var c = came[id];
        if (c.list === 'recent') {
          var from = gone['live:' + c.key], log = came['log:' + c.key];
          if (from) { delete gone['live:' + c.key]; self.ride(from, c.node, log && log.node); }
          else { flash(c.node); if (log) flash(log.node); }
        } else if (c.list === 'live') {
          self.arrive(c.node);
          if (c.node.getAttribute('data-signal') === 'missed') signals.push(c.node);
        } else if (c.list === 'log' && !came['recent:' + c.key]) {
          flash(c.node);
        }
      });
      Object.keys(gone).forEach(function (id) { if (!arrived[gone[id].key]) self.leave(gone[id], self.cache[id]); });
      signals.forEach(function (row) { self.signal(row); });
    }
    this.cacheRows();
    this.kick();
  };

  Board.prototype.slide = function (came) {
    if (reduced()) return;
    var cache = this.cache;
    this.root.querySelectorAll('[data-hold]').forEach(function (list) {
      var name = list.getAttribute('data-hold');
      for (var i = 0; i < list.children.length; i++) {
        var row = list.children[i], key = row.getAttribute('data-key'); if (!key || came[name + ':' + key]) continue;
        var was = cache[name + ':' + key]; if (!was) continue;
        var dy = was.top - row.getBoundingClientRect().top;
        if (Math.abs(dy) > .5) row.animate([{ transform: 'translateY(' + dy + 'px)' }, { transform: 'none' }], { duration: 320, easing: EASE });
      }
    });
  };

  // A row that leaves slides 14px right and fades, while the rest close the gap.
  Board.prototype.leave = function (g, was) {
    if (reduced() || !visible(was)) return;
    var c = copyOf(g.node); c.setAttribute('aria-hidden', 'true'); c.classList.add('tw-leaving');
    c.style.cssText += ';position:fixed;left:' + was.left + 'px;top:' + was.top + 'px;width:' + was.width + 'px;height:' + was.height + 'px;margin:0';
    document.body.appendChild(c);
    c.animate([{ opacity: c.classList.contains('ended') ? .55 : 1, transform: 'none' }, { opacity: 0, transform: 'translateX(14px)' }], { duration: 320, easing: EASE_IN, fill: 'forwards' }).finished
      .catch(function () { }).then(function () { c.remove(); });
  };

  // A call arrives: a pulse runs in along the inlet, and the row appears when it lands, with the counts it changes.
  Board.prototype.arrive = function (row) {
    if (reduced()) { flash(row); return; }
    if (!visible(row.getBoundingClientRect())) { row.classList.add('entering'); setTimeout(function () { row.classList.remove('entering'); }, 600); return; }
    row.classList.add('tw-await');
    this.bursts.push({ s: 'inlet', t0: performance.now(), dur: 520 });
    holdCounts(['inprogress', 'today'], 520);
    setTimeout(function () {
      row.classList.remove('tw-await'); row.classList.add('entering');
      landCounts(['inprogress', 'today']);
      setTimeout(function () { row.classList.remove('entering'); }, 600);
    }, 520);
  };

  // A finished call rides its outcome's stream into Recent activity: gather into a capsule at the port, ride the
  // stream with a louder syllable, open into its new slot. A row it also made in the call log waits for the archive.
  Board.prototype.ride = function (from, target, logRow) {
    var self = this, was = this.cache['live:' + from.key], stream = target.getAttribute('data-stream') || 'answered';
    if (!canFly() || !visible(was) || !visible(target.getBoundingClientRect()) || !this.paths[stream]) {
      flash(target); if (logRow) flash(logRow); return;
    }
    flying++;
    target.classList.add('tw-await'); if (logRow) logRow.classList.add('tw-await');
    var ghost = document.createElement('div'); ghost.className = 'tw-ghost'; ghost.setAttribute('aria-hidden', 'true');
    var row = copyOf(from.node); row.style.width = was.width + 'px';
    var cap = document.createElement('span'); cap.className = 'tw-cap';
    var mark = document.createElement('span'); mark.setAttribute('data-stream', stream);
    var icon = target.querySelector('.oc svg, .g svg'); if (icon) mark.appendChild(icon.cloneNode(true));
    var name = document.createElement('span'); name.textContent = ((from.node.querySelector('.who b') || {}).textContent || '').trim();
    cap.appendChild(mark); cap.appendChild(name);
    var dst = copyOf(target);
    ghost.appendChild(row); ghost.appendChild(cap); ghost.appendChild(dst);
    document.body.appendChild(ghost);
    var cw = cap.offsetWidth, ch = 26, colour = this.col[STREAMS[stream].tone];
    ghost.style.borderColor = colour; cap.style.opacity = 0; dst.style.opacity = 0; row.style.opacity = .55;
    function box(l, t, w, h, r) { ghost.style.transform = 'translate(' + l + 'px,' + t + 'px)'; ghost.style.width = w + 'px'; ghost.style.height = h + 'px'; ghost.style.borderRadius = r + 'px'; }
    var A = 170, B = 440, C = 210, t0 = performance.now(), rode = false;
    function step(now) {
      var el = now - t0, pts = self.paths[stream], p0 = pts[0], p1 = pts[pts.length - 1], to = target.getBoundingClientRect();
      if (!target.isConnected) el = A + B + C;
      if (el < A) {
        var e = ease(el / A);
        box(lerp(was.left, p0.x - cw / 2, e), lerp(was.top, p0.y - ch / 2, e), lerp(was.width, cw, e), lerp(was.height, ch, e), lerp(5, 13, e));
        row.style.opacity = .55 * (1 - e); cap.style.opacity = e;
      } else if (el < A + B) {
        if (!rode) { rode = true; self.bursts.push({ s: stream, t0: now, dur: B, miss: stream === 'missed' }); }
        var p = pts[Math.round(inOut((el - A) / B) * (pts.length - 1))];
        box(p.x - cw / 2, p.y - ch / 2, cw, ch, 13); row.style.opacity = 0; cap.style.opacity = 1;
      } else if (el < A + B + C) {
        var f = ease((el - A - B) / C);
        box(lerp(p1.x - cw / 2, to.left, f), lerp(p1.y - ch / 2, to.top, f), lerp(cw, to.width, f), lerp(ch, to.height, f), lerp(13, 5, f));
        cap.style.opacity = 1 - f; dst.style.opacity = f; dst.style.width = to.width + 'px';
        if (f > .5) ghost.style.borderColor = '';
      }
      if (el < A + B + C) { requestAnimationFrame(step); return; }
      ghost.remove(); flying--;
      target.classList.remove('tw-await'); flash(target);
      if (logRow) self.archive(logRow);
    }
    requestAnimationFrame(step);
    this.kick();
  };

  // A moment after a call opens in Recent activity, a pulse runs down the archive stream and it enters the call log.
  Board.prototype.archive = function (logRow) {
    if (reduced() || !visible(logRow.getBoundingClientRect())) { logRow.classList.remove('tw-await'); flash(logRow); return; }
    this.bursts.push({ s: 'archive', t0: performance.now(), dur: 460 });
    this.kick();
    setTimeout(function () { logRow.classList.remove('tw-await'); flash(logRow); }, 460);
  };

  // A missed call's mark rides the missed stream, climbs the branch into the rail and runs along it to the Calls
  // count, distorting each stretch it passes; missed today and call-backs change when it lands.
  Board.prototype.signal = function (row) {
    var key = row.getAttribute('data-key'), self = this;
    if (!key || this.signalled[key]) return;
    this.signalled[key] = true;
    var calls = document.querySelector('.rail a.ws[data-key="c"]'), lineCanvas = document.querySelector('canvas[data-rail-current]');
    var start = row.querySelector('.oc') || row;
    if (!canFly() || !calls || !lineCanvas || !visible(start.getBoundingClientRect()) || !this.paths.missed || !this.paths.branch) return;
    flying++;
    var DUR = 1250;
    holdCounts(['missed', 'callbacks'], DUR + 40);
    var mark = document.createElement('div'); mark.className = 'tw-mark'; mark.setAttribute('aria-hidden', 'true');
    var icon = start.querySelector('svg'); if (icon) mark.appendChild(icon.cloneNode(true));
    document.body.appendChild(mark);
    var origin = centre(start), t0 = performance.now(), lastDisturb = 0, burstAt = {};
    // The route, rebuilt every frame from where things are: to the port, half the missed stream, the branch, the rail.
    function route() {
      var m = self.paths.missed, half = m.slice(0, Math.floor(m.length / 2) + 1), br = self.paths.branch;
      var line = lineCanvas.getBoundingClientRect(), cx = centre(calls).x, top = br[br.length - 1];
      var legs = [[origin, m[0]], half, br, [{ x: top.x, y: line.top + line.height / 2 }, { x: cx, y: line.top + line.height / 2 }]];
      var lens = legs.map(function (pts) { var s = 0; for (var i = 1; i < pts.length; i++) s += Math.hypot(pts[i].x - pts[i - 1].x, pts[i].y - pts[i - 1].y); return Math.max(1, s); });
      return { legs: legs, lens: lens, total: lens.reduce(function (a, b) { return a + b; }, 0), line: line };
    }
    function along(pts, f) {
      var len = 0, seg = [];
      for (var i = 1; i < pts.length; i++) { var d = Math.hypot(pts[i].x - pts[i - 1].x, pts[i].y - pts[i - 1].y); seg.push(d); len += d; }
      var want = f * len;
      for (var j = 0; j < seg.length; j++) { if (want <= seg[j] || j === seg.length - 1) { var q = seg[j] ? Math.min(1, want / seg[j]) : 0; return { x: lerp(pts[j].x, pts[j + 1].x, q), y: lerp(pts[j].y, pts[j + 1].y, q) }; } want -= seg[j]; }
      return pts[pts.length - 1];
    }
    function step(now) {
      var q = Math.min(1, (now - t0) / DUR), r = route(), d = inOut(q) * r.total, leg = 0;
      while (leg < 3 && d > r.lens[leg]) { d -= r.lens[leg]; leg++; }
      var p = along(r.legs[leg], Math.min(1, d / r.lens[leg]));
      mark.style.transform = 'translate(' + (p.x - 11) + 'px,' + (p.y - 11) + 'px)';
      // Distort each stretch as the mark reaches it.
      if (leg === 1 && !burstAt.missed) { burstAt.missed = true; self.bursts.push({ s: 'missed', t0: now, dur: DUR * r.lens[1] / r.total * 2, miss: true }); }
      if (leg === 2 && !burstAt.branch) { burstAt.branch = true; self.bursts.push({ s: 'branch', t0: now, dur: DUR * r.lens[2] / r.total, miss: true }); }
      if (leg === 3 && now - lastDisturb > 90 && lineCanvas._field) {
        lastDisturb = now; lineCanvas._field.disturbAt((p.x - r.line.left) / r.line.width, .5, 600, 1);
      }
      if (q < 1 && mark.isConnected) { requestAnimationFrame(step); return; }
      mark.remove(); flying--;
      landCounts(['missed', 'callbacks']);
    }
    requestAnimationFrame(step);
    this.kick();
  };

  // ---------- holding still while someone points ----------
  // While the pointer is over a list, or focus is in it, the page holds the list still; it catches up 500ms after
  // they leave. Counts and the rail are never held: only list layout waits.
  function send(list, on) { if (dotnet) dotnet.invokeMethodAsync('Hold', list, on).catch(function () { }); }
  Board.prototype.pointing = function () {
    var self = this, hover = this.hover, focus = document.activeElement;
    this.root.querySelectorAll('[data-hold]').forEach(function (list) {
      var name = list.getAttribute('data-hold');
      var inside = (hover && hover.nodeType === 1 && list.contains(hover)) || (focus && focus !== document.body && list.contains(focus));
      if (inside) {
        clearTimeout(self.letGo[name]); self.letGo[name] = 0;
        if (!self.held[name]) { self.held[name] = true; send(name, true); }
      } else if (self.held[name] && !self.letGo[name]) {
        self.letGo[name] = setTimeout(function () { self.letGo[name] = 0; self.held[name] = false; send(name, false); }, 500);
      }
    });
  };

  // ---------- wiring ----------
  var board = null;
  function scan() {
    document.querySelectorAll('[data-roll]').forEach(setupRoll);
    var root = document.querySelector('[data-board]');
    if (board && board.root !== root) board.destroy();
    if (root && !board) board = new Board(root);
  }
  TW.board = {
    // The page hands over its .NET object once its circuit is up, so the script can tell it when a list is held.
    connect: function (ref) { dotnet = ref; connectedAt = performance.now(); scan(); },
  };
  var pending = false;
  new MutationObserver(function () {
    if (pending) return; pending = true;
    requestAnimationFrame(function () { pending = false; scan(); });
  }).observe(document.documentElement, { childList: true, subtree: true });
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', scan); else scan();
  if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener('enhancedload', scan);
  matchMedia('(prefers-reduced-motion: reduce)').addEventListener('change', function () { if (board) board.kick(); });
})();
