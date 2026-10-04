/* Copied from the TalkWatch design system bundle (the Data Current section of components/bundle.js). Change it there and copy it back. */
/* Data Current: TalkWatch's signal language, drawn as sound.
   Each route is one thin line drawn like a voice on an audio meter: a carrier under a speech
   envelope of syllables and pauses travelling toward the destination, along real paths between
   real places (lines, attendants, groups, people). It shows activity, not individual records.

   Semantic mapping, fixed across the product:
     loudness     amount (normalised, nonlinear)    speech speed  rate / urgency
     pauses       quiet: a busy route barely pauses  destination   where the work is going
     colour       category / state                   distortion    instability or failure
     a burst      a call starting on that route      flat line     no connection

   One canvas, one requestAnimationFrame loop per field, particles in typed arrays,
   no DOM per particle. Fields pause when hidden or off-screen and scale their
   particle budget to the device. Reduced motion draws a static equivalent. */
(function () {
  'use strict';
  const TW = window.TW = window.TW || {};

  // ---------- normalisation: very different metrics, one visual scale ----------
  // value -> 0..1 using a soft log curve with a configurable "busy" point.
  function normalise(v, busy = 10, max = busy * 4) {
    if (!(v > 0)) return 0;
    const x = Math.log1p(v) / Math.log1p(max);
    return Math.min(1, Math.max(0, x));
  }
  TW.normalise = normalise;

  const motionPref = () => {
    const forced = document.documentElement.dataset.motion;
    if (forced === 'reduced' || forced === 'minimal') return 'reduced';
    return matchMedia('(prefers-reduced-motion: reduce)').matches ? 'reduced' : 'full';
  };
  const deviceBudget = () => {
    const cores = navigator.hardwareConcurrency || 4;
    const small = Math.min(innerWidth, innerHeight) < 600;
    const base = small ? 260 : cores >= 8 ? 1400 : 800;
    return base;
  };
  function cssRGB(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || '120,180,220'; }

  // ---------- path geometry ----------
  // a path is a list of points in field-relative units (0..1); we fit a Catmull-Rom spline through them
  function sampler(pts, w, h) {
    const P = pts.map(p => [p[0] * w, p[1] * h]);
    const segs = [];
    let total = 0;
    const N = 18;
    let prev = P[0];
    const out = [prev];
    for (let i = 0; i < P.length - 1; i++) {
      const p0 = P[i - 1] || P[i], p1 = P[i], p2 = P[i + 1], p3 = P[i + 2] || P[i + 1];
      for (let s = 1; s <= N; s++) {
        const t = s / N, t2 = t * t, t3 = t2 * t;
        const x = .5 * ((2 * p1[0]) + (-p0[0] + p2[0]) * t + (2 * p0[0] - 5 * p1[0] + 4 * p2[0] - p3[0]) * t2 + (-p0[0] + 3 * p1[0] - 3 * p2[0] + p3[0]) * t3);
        const y = .5 * ((2 * p1[1]) + (-p0[1] + p2[1]) * t + (2 * p0[1] - 5 * p1[1] + 4 * p2[1] - p3[1]) * t2 + (-p0[1] + 3 * p1[1] - 3 * p2[1] + p3[1]) * t3);
        const d = Math.hypot(x - prev[0], y - prev[1]);
        total += d; segs.push(total); out.push([x, y]); prev = [x, y];
      }
    }
    return {
      length: total, pts: out,
      at(u) { // u: 0..1 along length -> [x, y, nx, ny]
        const target = u * total; let lo = 0, hi = segs.length - 1;
        while (lo < hi) { const mid = (lo + hi) >> 1; if (segs[mid] < target) lo = mid + 1; else hi = mid; }
        const a = out[lo], b = out[lo + 1] || a; const s0 = lo ? segs[lo - 1] : 0; const f = segs[lo] - s0 ? (target - s0) / (segs[lo] - s0) : 0;
        const x = a[0] + (b[0] - a[0]) * f, y = a[1] + (b[1] - a[1]) * f;
        const dx = b[0] - a[0], dy = b[1] - a[1]; const L = Math.hypot(dx, dy) || 1;
        return [x, y, -dy / L, dx / L];
      },
    };
  }

  // cheap smooth noise for organic drift
  function noise(x, y) { return Math.sin(x * 1.7 + Math.sin(y * 1.3)) * .5 + Math.sin(y * 2.1 + x * .7) * .5; }
  // smooth value noise in 0..1: random heights at whole numbers, eased between them, so it wanders like speech
  function hash(i) { const x = Math.sin(i * 127.1 + 311.7) * 43758.5453; return x - Math.floor(x); }
  function vnoise(x) { const i = Math.floor(x), f = x - i, e = f * f * (3 - 2 * f); return hash(i) * (1 - e) + hash(i + 1) * e; }

  // ---------- a field ----------
  class Field {
    constructor(canvas, opts = {}) {
      this.c = canvas; this.ctx = canvas.getContext('2d');
      this.opts = Object.assign({ mode: 'flow', layer: 'data', speed: 1, budget: null, glow: true, fadeIn: true }, opts);
      this.streams = new Map();
      // Empty, not missing: a panel can start before its first stream arrives, and a frame then has nothing to step.
      this.order = [];
      this.disturb = []; // [{x,y,r,until,strength}]
      this.emission = 1; this.emissionTarget = 1; // connection state, 0 = still
      this.cap = 0; this.n = 0;
      this.alloc(this.opts.budget || deviceBudget());
      this.visible = true; this.onscreen = true; this.running = false;
      this.last = 0;
      this.pointer = null;
      this.resize = this.resize.bind(this); this.frame = this.frame.bind(this);
      this.ro = new ResizeObserver(this.resize); this.ro.observe(canvas.parentElement || canvas);
      this.io = new IntersectionObserver((e) => { this.onscreen = e[0].isIntersecting; this.kick(); }); this.io.observe(canvas);
      this.vis = () => { this.visible = !document.hidden; this.kick(); }; document.addEventListener('visibilitychange', this.vis);
      // The system can turn animation off and on again (Windows' energy saver does): follow it both ways, as CSS does.
      matchMedia('(prefers-reduced-motion: reduce)').addEventListener('change', () => this.kick());
      if (opts.interactive) {
        canvas.parentElement.addEventListener('pointermove', (e) => { const r = canvas.getBoundingClientRect(); this.pointer = [e.clientX - r.left, e.clientY - r.top]; });
        canvas.parentElement.addEventListener('pointerleave', () => { this.pointer = null; });
      }
      this.resize();
      this.kick();
    }
    alloc(n) {
      this.cap = n;
      this.s = new Int16Array(n);      // stream index
      this.u = new Float32Array(n);    // progress 0..1
      this.v = new Float32Array(n);    // speed (fraction per second)
      this.o = new Float32Array(n);    // lateral offset
      this.a = new Float32Array(n);    // alpha base
      this.z = new Float32Array(n);    // size
      this.ph = new Float32Array(n);   // phase
      this.n = 0;
    }
    resize() {
      const r = this.c.getBoundingClientRect(); const dpr = Math.min(2, devicePixelRatio || 1);
      this.w = Math.max(1, r.width); this.h = Math.max(1, r.height);
      this.c.width = Math.round(this.w * dpr); this.c.height = Math.round(this.h * dpr);
      this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      for (const st of this.streams.values()) st.path = sampler(st.pts, this.w, this.h);
      this.colours();
      if (motionPref() === 'reduced') this.drawStatic();
    }
    colours() { this.rgb = { a: cssRGB('--flow-a'), b: cssRGB('--flow-b'), c: cssRGB('--flow-c'), bad: cssRGB('--flow-bad') }; this.base = parseFloat(cssRGB('--flow-base')) || .9; }
    // define or update a stream; value is the raw metric, busy is the metric's "busy" point
    set(id, def) {
      let st = this.streams.get(id);
      const norm = def.norm != null ? def.norm : normalise(def.value ?? 0, def.busy ?? 10, def.max);
      if (!st) {
        st = { id, pts: def.pts, path: sampler(def.pts, this.w, this.h), tone: def.tone || 'a', norm: 0, target: norm, width: def.width ?? 10, speed: def.speed ?? 1, acc: 0, turb: def.turb || 0, dim: false, idx: this.streams.size, label: def.label };
        this.streams.set(id, st);
      } else {
        if (def.pts && JSON.stringify(def.pts) !== JSON.stringify(st.pts)) { st.pts = def.pts; st.path = sampler(def.pts, this.w, this.h); }
        st.target = norm; if (def.tone) st.tone = def.tone; if (def.turb != null) st.turb = def.turb; if (def.width != null) st.width = def.width; if (def.speed != null) st.speed = def.speed;
      }
      this.order = [...this.streams.values()];
      if (motionPref() === 'reduced') this.drawStatic();
      return st;
    }
    remove(id) { this.streams.delete(id); this.order = [...this.streams.values()]; }
    focus(id) { for (const st of this.streams.values()) st.dim = id != null && st.id !== id; }
    connect(on) { this.emissionTarget = on ? 1 : 0; this.kick(); }
    // a localised disturbance: the flow near (x,y) goes turbulent and shifts toward the failure colour
    disturbAt(x, y, ms = 2400, strength = 1) { this.disturb.push({ x: x * this.w, y: y * this.h, r: Math.min(this.w, this.h) * .18 + 30, until: performance.now() + ms, strength }); this.kick(); }
    // burst: a one-off pulse along a stream (a call starting, a metric jump)
    burst(id, count = 8) { const st = this.streams.get(id); if (!st) return; (st.bursts = st.bursts || []).push({ t0: performance.now() / 1000, a: Math.min(1, .5 + count / 20) }); this.kick(); }
    spawn(st, u0 = 0) {
      if (this.n >= this.cap) return;
      const i = this.n++;
      const layer = this.opts.layer;
      this.s[i] = this.order.indexOf(st); this.u[i] = u0;
      const sp = (layer === 'background' ? .018 : .11) * this.opts.speed * st.speed * (0.75 + Math.random() * .5) * (st.path.length ? Math.max(.4, 420 / st.path.length) : 1);
      this.v[i] = sp;
      this.o[i] = (Math.random() - .5) * 2 * (st.width * (0.35 + st.norm * .65));
      this.a[i] = (layer === 'background' ? .16 : .55) + Math.random() * (layer === 'background' ? .14 : .45);
      this.z[i] = (layer === 'background' ? 1.6 : 1.1) + Math.random() * (layer === 'background' ? 1.6 : 1.3);
      this.ph[i] = Math.random() * 6.28;
    }
    kick() {
      const should = this.visible && this.onscreen && motionPref() !== 'reduced' && document.documentElement.dataset.bg !== 'off-' + this.opts.layer;
      if (should && !this.running) { this.running = true; this.last = performance.now(); requestAnimationFrame(this.frame); }
      if (!should) { this.running = false; if (motionPref() === 'reduced') this.drawStatic(); }
    }
    frame(t) {
      if (!this.running) return;
      const dt = Math.min(.05, (t - this.last) / 1000); this.last = t;
      // A frame that throws ends the loop; say so, so the next change starts it again rather than finding it "running".
      try { this.step(dt, t); this.draw(t); } catch (e) { this.running = false; throw e; }
      requestAnimationFrame(this.frame);
    }
    step(dt, t) {
      // ease emission and per-stream density toward targets over ~800ms
      const k = 1 - Math.exp(-dt * 3.2);
      this.emission += (this.emissionTarget - this.emission) * (this.emissionTarget < this.emission ? 1 - Math.exp(-dt * 1.2) : k);
      const budget = this.cap;
      const nStreams = Math.max(1, this.order.length);
      for (const st of this.order) {
        st.norm += (st.target - st.norm) * k;
        // emission rate: particles per second, nonlinear in the normalised value
        const share = budget / nStreams;
        const rate = (this.opts.layer === 'background' ? .9 : 1.2) * st.norm * st.norm * share * .55 * (this.opts.layer === 'background' ? .06 : .055) + (st.norm > 0.01 ? .4 : 0);
        st.acc = 0; void rate; // loudness only: lines carry the traffic now, no particles are spawned
      }
      this.disturb = this.disturb.filter(d => d.until > t);
      // advance and compact
      let j = 0;
      for (let i = 0; i < this.n; i++) {
        let u = this.u[i] + this.v[i] * dt * (this.emissionTarget === 0 ? .55 : 1);
        if (u >= 1) continue;
        this.u[j] = u; this.v[j] = this.v[i]; this.o[j] = this.o[i] * (this.opts.mode === 'converge' ? (1 - dt * .4) : 1); this.a[j] = this.a[i] * (this.emissionTarget === 0 ? (1 - dt * .9) : 1); this.z[j] = this.z[i]; this.ph[j] = this.ph[i]; this.s[j] = this.s[i];
        j++;
      }
      this.n = j;
    }
    // One thin line per route, drawn the way a voice looks on an audio meter. A fast carrier oscillation sits under
    // a speech envelope: smooth noise that rises into syllables and falls into pauses, travelling toward the
    // destination. The route's traffic sets how loud it is: a quiet route is a near-flat line with the odd murmur,
    // a busy one swells and talks almost without pause. A new call sends a burst of louder speech along its route.
    // A disturbance distorts the wave (clipped, jagged, failure colour); a lost connection lets it fall flat.
    speech(s, T, st, seed = 0) {
      // 0..1: where along the line a voice is speaking at this moment
      const drift = T * (.55 + st.norm * .6) * this.opts.speed;
      let e = vnoise(s * .011 - drift + st.idx * 17.3 + seed) * .7 + vnoise(s * .031 - drift * 1.7 + st.idx * 5.1 + seed * 1.9) * .3;
      e = Math.max(0, e * 2.1 - .85 + st.norm * .45);           // busier routes pause less
      return Math.min(1, Math.pow(e, 1.6));                     // sharp onsets, real silences
    }
    draw(t) {
      const ctx = this.ctx; ctx.clearRect(0, 0, this.w, this.h);
      const T = t / 1000;
      const bg = this.opts.layer === 'background';
      const live = this.emission;
      ctx.lineCap = 'round'; ctx.lineJoin = 'round';
      const distortAt = (x, y) => { let f = 0; for (const d of this.disturb) { const dd = Math.hypot(x - d.x, y - d.y); if (dd < d.r) f = Math.max(f, (1 - dd / d.r) * d.strength); } return Math.min(1.4, f); };
      for (const st of this.order) {
        const len = st.path.length; if (len < 8) continue;
        st.bursts = (st.bursts || []).filter(b => T - b.t0 < 6);
        // More voices on a busier route: one line when quiet, up to five when busy, each fading in as traffic grows.
        const voices = bg || this.opts.carrier ? 1 : 1 + st.norm * 4;
        for (let vi = 0; vi < Math.ceil(voices); vi++) {
          const presence = Math.min(1, voices - vi);
          if (presence <= .02) continue;
          this.line(ctx, st, vi, presence, T, len, bg, live, distortAt);
        }
      }
    }
    // One voice on a route: its own rhythm (seed), its own height in the alternation, its own weight.
    line(ctx, st, vi, presence, T, len, bg, live, distortAt) {
      const heights = [1, .45, .8, .3, .62];      // alternating heights: tall, short, tall-ish, shortest, middle
      const loud = (bg ? 10 : Math.max(2.5, st.width * .85)) * (.08 + st.norm * .92) * (.15 + .85 * live) * heights[vi % heights.length];
      const k = (bg ? .09 : .78) * (1 + st.norm * .2) * (1 + vi * .13);
      const w = (bg ? 2 : 11) * this.opts.speed * (1 + vi * .09);
      const seed = vi * 7.7;
      const step = bg ? 4 : vi === 0 ? 2 : 3;
      const steps = Math.max(24, Math.min(900, Math.round(len / step)));
      let distorted = false;
      ctx.beginPath();
      for (let j = 0; j <= steps; j++) {
        const u = j / steps, s = u * len;
        const p = st.path.at(u);
        const taper = Math.pow(Math.sin(Math.PI * u), .6);
        let env = this.speech(s, T, st, seed);
        for (const b of st.bursts) { const at = (T - b.t0) * .32 * this.opts.speed; const d = (u - at) * 9; env = Math.max(env, Math.exp(-d * d) * b.a * (vi ? .7 : 1)); }
        // several unrelated frequencies and a little noise: irregular, like speech, not a coiled spring
        let v = Math.sin(s * k - T * w + st.idx + seed) * .5 + Math.sin(s * k * 2.37 - T * w * 1.6 + 1.3 + seed) * .3 + Math.sin(s * k * 3.91 - T * w * 2.3 + 2.1) * .2 + noise(s * .9 + seed, T * 7) * .25;
        const f = this.disturb.length ? distortAt(p[0], p[1]) : 0;
        if (f > 0) { distorted = distorted || f > .25; v = Math.max(-.55, Math.min(.55, (v + Math.sin(s * k * 3.3 + T * 31) * f * .7 + noise(s * .5, T * 9) * f * .4) * (1 + f))); env = Math.max(env, f * .8); }
        v += st.turb * noise(s * .2, T * 4) * .5;
        const a = loud * taper * (.12 + .88 * env);
        // each voice sits a hair off the route, alternating sides, so the strands read as separate lines
        const lane = vi === 0 ? 0 : (vi % 2 ? 1 : -1) * Math.ceil(vi / 2) * 1.6 * taper;
        const x = p[0] + p[2] * (v * a + lane), y = p[1] + p[3] * (v * a + lane);
        j ? ctx.lineTo(x, y) : ctx.moveTo(x, y);
      }
      const alpha = (st.dim ? .08 : (bg ? .05 + st.norm * .08 : (.35 + st.norm * .5) * (vi ? .78 : 1))) * this.base * presence;
      ctx.strokeStyle = 'rgba(' + (distorted ? this.rgb.bad : this.rgb[st.tone]) + ',' + alpha.toFixed(3) + ')';
      ctx.lineWidth = bg ? 1 : (vi ? .6 : .8 + st.norm * .7);   // the lead voice a touch heavier, the rest hairlines
      ctx.stroke();
    }
    // reduced motion: the same voices, frozen at one instant; how many lines, how loud and where they pause
    // still show the traffic, and a distorted route keeps its jagged shape.
    drawStatic() {
      const ctx = this.ctx; if (!this.w) return; ctx.clearRect(0, 0, this.w, this.h);
      if (this.opts.layer === 'background') return;
      ctx.lineCap = 'round'; ctx.lineJoin = 'round';
      for (const st of this.streams.values()) {
        const len = st.path.length; if (len < 8) continue;
        const saved = st.norm; st.norm = st.target; st.bursts = [];
        const voices = this.opts.carrier ? 1 : 1 + st.norm * 4;
        for (let vi = 0; vi < Math.ceil(voices); vi++) this.line(ctx, st, vi, Math.min(1, voices - vi), 0, len, false, 1, () => (st.turb > .3 ? .6 : 0));
        st.norm = saved;
      }
    }
    destroy() { this.running = false; this.ro.disconnect(); this.io.disconnect(); document.removeEventListener('visibilitychange', this.vis); }
  }
  TW.Field = Field;

  // ---------- presets ----------
  // background: barely-visible currents converging on a few points, density from live activity
  TW.backgroundField = function (canvas) {
    const f = new Field(canvas, { layer: 'background', budget: Math.round(deviceBudget() * .3), guides: false, trails: false, speed: 1 });
    const paths = [
      [[-.05, .25], [.25, .32], [.55, .3], [.82, .42], [1.05, .45]],
      [[-.05, .7], [.3, .62], [.6, .66], [.85, .55], [1.05, .5]],
      [[.1, 1.05], [.25, .7], [.5, .5], [.7, .3], [.8, -.05]],
      [[-.05, .48], [.35, .5], [.65, .48], [1.05, .52]],
    ];
    paths.forEach((p, i) => f.set('bg' + i, { pts: p, value: 2, busy: 6, width: 60, tone: ['a', 'b', 'a', 'c'][i] }));
    f.setActivity = (n) => paths.forEach((p, i) => f.set('bg' + i, { value: Math.max(.6, n * (0.6 + i * .15)), busy: 8 }));
    return f;
  };
  // the signal rail: a thin oscilloscope line under the workspaces, louder when more calls are live
  TW.railField = function (canvas) {
    const f = new Field(canvas, { layer: 'data', budget: 140, guides: false, carrier: true, trails: false, speed: .55 });
    f.set('rail', { pts: [[0, .5], [.5, .45], [1, .55]], value: 4, busy: 10, width: 4, tone: 'a' });
    return f;
  };
  // micro field inside a metric or component
  TW.microField = function (canvas, value, busy, tone = 'a', converge = false) {
    const f = new Field(canvas, { layer: 'data', budget: 90, guides: false, carrier: true, trails: false, speed: .8, mode: converge ? 'converge' : 'flow' });
    f.set('m', { pts: converge ? [[0, .2], [.5, .45], [1, .5]] : [[0, .5], [.5, .5], [1, .5]], value, busy, width: 10, tone });
    return f;
  };
})();
