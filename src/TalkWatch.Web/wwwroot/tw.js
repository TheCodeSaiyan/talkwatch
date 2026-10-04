// TalkWatch shell behaviour: the Data Current under the Signal Rail, live counters, the theme choice, menus and the
// G-then-letter workspace keys. Idempotent, because Blazor's enhanced navigation swaps pages without reloading
// scripts: init() runs on load and again after every enhanced navigation, and only wires what is not wired yet.
(function () {
  'use strict';
  var TW = window.TW || {};

  // ---------- the demo's countdown to starting over: [data-countdown] holds when, as ISO ----------
  // At zero it says so, and the page reloads a little after, once the demo has set itself up again.
  function initCountdown() {
    document.querySelectorAll('[data-countdown]').forEach(function (el) {
      if (el._counting) return;
      el._counting = true;
      var at = Date.parse(el.getAttribute('data-countdown'));
      var tick = function () {
        var left = Math.round((at - Date.now()) / 1000);
        if (left <= 0) {
          el.textContent = 'a moment';
          clearInterval(timer);
          setTimeout(function () { location.reload(); }, 25000);
          return;
        }
        el.textContent = Math.floor(left / 60) + ':' + String(left % 60).padStart(2, '0');
      };
      var timer = setInterval(tick, 1000);
      tick();
    });
  }

  // ---------- the Data Current along the rail: louder when more people are on calls ----------
  function initRail() {
    var canvas = document.querySelector('canvas[data-rail-current]');
    if (!canvas || canvas._field || !TW.railField) return;
    var field = canvas._field = TW.railField(canvas);
    var read = function () {
      var nav = document.querySelector('.rail .signals[data-activity]');
      var n = nav ? parseInt(nav.getAttribute('data-activity'), 10) || 0 : 0;
      field.set('rail', { value: n, busy: 10 });
      var fresh = document.querySelector('[data-fresh]');
      field.connect(!fresh || fresh.getAttribute('data-state') !== 'offline');
    };
    read();
    // The workspaces and freshness are interactive and re-render in place; watch their attributes, not the page.
    new MutationObserver(read).observe(canvas.closest('.rail'), { subtree: true, attributes: true, attributeFilter: ['data-activity', 'data-state'], childList: true });
  }

  // ---------- Data Current panels: [data-current] holds {nodes, streams} as JSON from the server ----------
  // Nodes are places in 0..1 of the panel; streams join two of them, and their value is the raw metric. The server
  // re-renders the attribute when calls change; the field eases to the new densities instead of starting again.
  function pathBetween(a, b) {
    var mx = (a.x + b.x) / 2;
    return [[a.x + .035, a.y], [mx, a.y + (b.y - a.y) * .15], [mx + .02, b.y - (b.y - a.y) * .15], [b.x - .04, b.y]];
  }
  function drawCurrent(el) {
    var data; try { data = JSON.parse(el.getAttribute('data-current') || '{}'); } catch (e) { return; }
    var nodes = data.nodes || [], streams = data.streams || [];
    var byId = {}; nodes.forEach(function (n) { byId[n.id] = n; });
    // labels: reuse what is there, add what is new, drop what has gone
    var have = {}; el.querySelectorAll('.node').forEach(function (d) { have[d.getAttribute('data-node')] = d; });
    nodes.forEach(function (n) {
      var d = have[n.id];
      if (!d) { d = document.createElement('div'); d.className = 'node'; d.setAttribute('data-node', n.id); d.appendChild(document.createElement('b')); var m = document.createElement('span'); m.className = 'm'; d.appendChild(m); el.appendChild(d); }
      d.style.left = (n.x * 100) + '%'; d.style.top = (n.y * 100) + '%';
      d.firstChild.textContent = n.label; d.lastChild.textContent = n.meta || '';
      delete have[n.id];
    });
    Object.keys(have).forEach(function (k) { have[k].remove(); });
    var field = el._field;
    var keep = {};
    streams.forEach(function (s) {
      var a = byId[s.from], b = byId[s.to]; if (!a || !b) return;
      var id = s.from + '>' + s.to; keep[id] = true;
      field.set(id, { pts: pathBetween(a, b), value: s.value, busy: 6, max: 30, width: 12, tone: s.tone || 'a' });
    });
    Array.from(field.streams.keys()).forEach(function (id) { if (!keep[id]) field.remove(id); });
    field.connect(el.getAttribute('data-still') !== 'true');
  }
  function initCurrents() {
    if (!TW.Field) return;
    document.querySelectorAll('[data-current]').forEach(function (el) {
      if (el._field) return;
      var canvas = document.createElement('canvas'); canvas.setAttribute('aria-hidden', 'true');
      el.appendChild(canvas);
      el._field = new TW.Field(canvas, { layer: 'data', interactive: true, speed: .9 });
      drawCurrent(el);
      new MutationObserver(function () { drawCurrent(el); }).observe(el, { attributes: true, attributeFilter: ['data-current', 'data-still'] });
    });
  }

  // ---------- live counters: [data-since] holds epoch milliseconds and shows m:ss ----------
  function fmt(s) { s = Math.max(0, Math.round(s)); var h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), x = s % 60; return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(x).padStart(2, '0'); }
  function tick() {
    var now = Date.now();
    document.querySelectorAll('[data-since]').forEach(function (el) { var v = fmt((now - Number(el.getAttribute('data-since'))) / 1000); if (el.textContent !== v) el.textContent = v; });
  }

  // ---------- theme ----------
  function currentTheme() { try { return localStorage.getItem('tw.theme') || 'system'; } catch (e) { return 'system'; } }
  function applyTheme(t) {
    if (t === 'light' || t === 'dark') document.documentElement.dataset.theme = t; else delete document.documentElement.dataset.theme;
    try { if (t === 'system') localStorage.removeItem('tw.theme'); else localStorage.setItem('tw.theme', t); } catch (e) { /* not remembered */ }
    document.querySelectorAll('[data-theme-choice]').forEach(function (b) { b.setAttribute('aria-pressed', String(b.getAttribute('data-theme-choice') === t)); });
    var c = document.querySelector('canvas[data-rail-current]'); if (c && c._field) c._field.colours();
  }
  document.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('[data-theme-choice]');
    if (b) { applyTheme(b.getAttribute('data-theme-choice')); return; }
    // a menu closes when you click outside it or choose something in it
    document.querySelectorAll('details.menu[open]').forEach(function (d) { if (!d.contains(e.target) || e.target.closest('.menu-panel a')) d.removeAttribute('open'); });
  });

  // ---------- keyboard: G then a letter jumps to a workspace; never while typing ----------
  var chord = false, chordTimer = 0;
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') { document.querySelectorAll('details.menu[open]').forEach(function (d) { d.removeAttribute('open'); var s = d.querySelector('summary'); if (s) s.focus(); }); return; }
    var t = e.target;
    if (e.ctrlKey || e.metaKey || e.altKey || (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName)))) return;
    if (chord) {
      chord = false; clearTimeout(chordTimer);
      var link = document.querySelector('.rail a.ws[data-key="' + e.key.toLowerCase() + '"]');
      if (link) { e.preventDefault(); link.click(); }
      return;
    }
    if (e.key === 'g') { chord = true; chordTimer = setTimeout(function () { chord = false; }, 1200); }
  });

  function init() {
    initRail();
    initCountdown();
    initCurrents();
    document.querySelectorAll('[data-theme-choice]').forEach(function (b) { b.setAttribute('aria-pressed', String(b.getAttribute('data-theme-choice') === currentTheme())); });
    tick();
  }
  setInterval(tick, 1000);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();
  // An interactive page can replace its prerendered elements when its circuit starts; attach to whatever appears.
  new MutationObserver(function (records) {
    for (var i = 0; i < records.length; i++) {
      for (var j = 0; j < records[i].addedNodes.length; j++) {
        var n = records[i].addedNodes[j];
        if (n.nodeType === 1 && (n.hasAttribute('data-current') || n.querySelector('[data-current]') || n.matches('canvas[data-rail-current]') || n.querySelector('canvas[data-rail-current]'))) { init(); return; }
      }
    }
  }).observe(document.documentElement, { childList: true, subtree: true });
  if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener('enhancedload', init);
  matchMedia('(prefers-color-scheme: light)').addEventListener('change', function () { var c = document.querySelector('canvas[data-rail-current]'); if (c && c._field) c._field.colours(); });
})();
