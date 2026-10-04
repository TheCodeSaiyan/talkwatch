// The command palette: Ctrl/Cmd+K or / opens it anywhere. It jumps to any workspace the rail offers (so it never
// lists a page this person cannot open), runs a few common views by name, and searches calls and lines through
// /search, which reads through the viewer's own access scope.
(function () {
  'use strict';
  var ICON = {
    go: 'M6 3.5 10.5 8 6 12.5', search: 'M7 12a5 5 0 1 0 0-10 5 5 0 0 0 0 10zM10.6 10.6 14 14', in: 'M12 4 4.5 11.5M4.5 6.5v5h5', out: 'M4 12l7.5-7.5M6.5 4.5h5v5',
    line: 'M6 2.5 5 13.5M11 2.5l-1 11M2.5 6h11M2 10.5h11', user: 'M8 7.5a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5zM3 13.5c.6-2.6 2.6-4 5-4s4.4 1.4 5 4',
    filter: 'M2.5 3.5h11L9.5 8.5v4l-3 1.5v-5.5z', theme: 'M13 9.5A5.5 5.5 0 0 1 6.5 3a5.5 5.5 0 1 0 6.5 6.5z',
  };
  function icon(name) { return '<svg class="ico" viewBox="0 0 16 16" aria-hidden="true"><path d="' + ICON[name] + '"></path></svg>'; }
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  function mark(text, q) {
    var t = esc(text); if (!q) return t;
    var i = String(text).toLowerCase().indexOf(q.toLowerCase());
    return i < 0 ? t : esc(String(text).slice(0, i)) + '<mark>' + esc(String(text).slice(i, i + q.length)) + '</mark>' + esc(String(text).slice(i + q.length));
  }
  var OUTCOME = { Answered: 'answered', Missed: 'missed', Voicemail: 'voicemail', HungUpAtSwitchboard: 'hung up in the menu', Outbound: 'outbound', InProgress: 'in progress', Blocked: 'blocked' };

  function commands() {
    var out = [];
    // workspaces and configuration, exactly as the rail offers them to this person
    document.querySelectorAll('.rail a.ws, .rail .ws-menu .menu-panel a').forEach(function (a) {
      var label = (a.querySelector('.wl') || a).textContent.trim();
      var key = a.getAttribute('data-key');
      out.push({ group: 'Go to', label: label, hint: key ? 'G ' + key.toUpperCase() : '', href: a.getAttribute('href'), icon: 'go' });
    });
    [['Show missed calls today', '/calls?outcome=missed&period=today'], ['Show calls nobody answered this week', '/calls?outcome=unanswered&period=7d'],
     ['Show callers who hung up in the menu', '/calls?outcome=menu&period=30d'], ['Show voicemail from the last 30 days', '/calls?outcome=voicemail&period=30d'],
     ['Show outbound calls today', '/calls?dir=out&period=today'], ['Analytics for the last 30 days', '/dashboard?days=30']]
      .forEach(function (c) { out.push({ group: 'Views', label: c[0], href: c[1], icon: 'filter' }); });
    out.push({ group: 'Views', label: 'Switch between light and dark', run: function () {
      var dark = document.documentElement.dataset.theme ? document.documentElement.dataset.theme === 'dark' : !matchMedia('(prefers-color-scheme: light)').matches;
      var b = document.querySelector('[data-theme-choice="' + (dark ? 'light' : 'dark') + '"]'); if (b) b.click();
    }, icon: 'theme' });
    return out;
  }
  function score(c, q) {
    if (!q) return c.group === 'Go to' ? 2 : 1;
    var l = c.label.toLowerCase(), qq = q.toLowerCase();
    if (l.indexOf(qq) === 0) return 4;
    if (l.indexOf(qq) >= 0) return 3;
    return qq.split(/\s+/).every(function (w) { return l.indexOf(w) >= 0; }) ? 2 : 0;
  }

  var open = null;
  function show() {
    if (open) return;
    var prev = document.activeElement;
    var scrim = document.createElement('div'); scrim.className = 'scrim';
    var box = document.createElement('div'); box.className = 'palette'; box.setAttribute('role', 'dialog'); box.setAttribute('aria-modal', 'true'); box.setAttribute('aria-label', 'Search and go');
    box.innerHTML = '<div class="pin">' + icon('search') + '<input id="tw-palette-q" role="combobox" aria-expanded="true" aria-controls="tw-palette-res" autocomplete="off" placeholder="Search calls, numbers, people, lines, or go somewhere" aria-label="Search or go somewhere"><kbd>Esc</kbd></div>' +
      '<div class="res" id="tw-palette-res" role="listbox"></div>' +
      '<div class="foot"><span>↑↓ move</span><span>↵ open</span><span>Type part of a number to find calls from it</span></div>';
    document.body.append(scrim, box);
    var input = box.querySelector('input'), res = box.querySelector('.res');
    var cmds = commands(), sel = 0, shown = [], remote = { calls: [], lines: [] }, timer = 0, seq = 0;
    function render() {
      var q = input.value.trim();
      var local = cmds.map(function (c) { return [score(c, q), c]; }).filter(function (x) { return x[0] > 0; }).sort(function (a, b) { return b[0] - a[0]; }).map(function (x) { return x[1]; });
      var items = [];
      remote.calls.forEach(function (c) {
        var when = new Date(c.time);
        items.push({ group: 'Calls', label: c.who, sub: (c.number && c.number !== c.who ? c.number + ' · ' : '') + (OUTCOME[c.outcome] || c.outcome.toLowerCase()) + ' · ' + when.toLocaleString('en-GB', { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }), href: '/calls/' + c.uuid, icon: c.direction === 'out' ? 'out' : 'in' });
      });
      remote.lines.forEach(function (l) {
        var kind = { Did: 'Number', User: 'Person', RingGroup: 'Group', Attendant: 'Menu' }[l.kind] || l.kind;
        items.push({ group: 'Lines', label: l.name, sub: kind + (l.number ? ' · ' + l.number : '') + (l.ext ? ' · ext ' + l.ext : '') + ' · show its calls', href: '/calls?line=' + encodeURIComponent(l.kind + ':' + l.key), icon: l.kind === 'User' ? 'user' : 'line' });
      });
      items = items.concat(q ? local.slice(0, 8) : local);
      shown = items;
      var html = '', last = '';
      items.forEach(function (it, i) {
        if (it.group !== last) { html += '<div class="grp eyebrow">' + esc(it.group) + '</div>'; last = it.group; }
        html += '<button type="button" class="it" role="option" id="tw-pal-' + i + '" aria-selected="' + (i === sel) + '" data-i="' + i + '"><span class="muted">' + icon(it.icon) + '</span>' +
          '<span style="min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">' + mark(it.label, q) + (it.sub ? '<span class="s2">' + esc(it.sub) + '</span>' : '') + '</span>' +
          '<span class="d">' + esc(it.hint || (it.run ? 'Run' : 'Open')) + '</span></button>';
      });
      if (!items.length) html = '<div class="empty" style="padding:16px"><b>Nothing matches “' + esc(q) + '”</b><p class="why">Try a name, an extension or part of a number.</p></div>';
      res.innerHTML = html;
      sel = Math.min(sel, Math.max(0, items.length - 1)); focusSel();
    }
    function focusSel() {
      res.querySelectorAll('.it').forEach(function (b, i) { b.setAttribute('aria-selected', String(i === sel)); });
      var cur = res.querySelector('#tw-pal-' + sel); if (cur) { cur.scrollIntoView({ block: 'nearest' }); input.setAttribute('aria-activedescendant', cur.id); }
    }
    function go(i) {
      var it = shown[i]; if (!it) return;
      close();
      if (it.run) it.run(); else if (it.href) location.href = it.href;
    }
    function search() {
      var q = input.value.trim(), mine = ++seq;
      if (q.length < 2) { remote = { calls: [], lines: [] }; render(); return; }
      fetch('/search?q=' + encodeURIComponent(q), { credentials: 'same-origin', headers: { Accept: 'application/json' } })
        .then(function (r) { return r.ok ? r.json() : { calls: [], lines: [] }; })
        .then(function (d) { if (mine !== seq) return; remote = { calls: d.calls || [], lines: d.lines || [] }; render(); })
        .catch(function () { /* the palette still offers navigation */ });
    }
    function close() { scrim.remove(); box.remove(); open = null; if (prev && prev.focus) prev.focus(); }
    input.addEventListener('input', function () { sel = 0; render(); clearTimeout(timer); timer = setTimeout(search, 160); });
    input.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown') { sel = Math.min(shown.length - 1, sel + 1); focusSel(); e.preventDefault(); }
      else if (e.key === 'ArrowUp') { sel = Math.max(0, sel - 1); focusSel(); e.preventDefault(); }
      else if (e.key === 'Enter') { go(sel); e.preventDefault(); }
      else if (e.key === 'Escape') { close(); }
    });
    res.addEventListener('click', function (e) { var b = e.target.closest('.it'); if (b) go(Number(b.getAttribute('data-i'))); });
    res.addEventListener('mousemove', function (e) { var b = e.target.closest('.it'); if (b && Number(b.getAttribute('data-i')) !== sel) { sel = Number(b.getAttribute('data-i')); focusSel(); } });
    scrim.addEventListener('click', close);
    open = box; render(); input.focus();
  }

  document.addEventListener('keydown', function (e) {
    var t = e.target, typing = t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName));
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); show(); return; }
    if (e.key === '/' && !typing && !e.ctrlKey && !e.metaKey && !e.altKey) { e.preventDefault(); show(); }
  });
  document.addEventListener('click', function (e) { if (e.target.closest && e.target.closest('[data-palette]')) { e.preventDefault(); show(); } });
})();
