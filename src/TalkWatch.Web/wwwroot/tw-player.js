// TalkWatch's audio player: the design system's waveform player around each <audio data-audio> element, which stays in
// the page so it still works without script. Nothing is fetched until someone presses play, because the server writes
// "recording played" to the audit log when a file is requested from the start; on play the file is fetched once, the
// waveform is drawn from that same download and the audio plays from it, so one play is one audit entry, as before.
(function () {
  'use strict';
  function fmt(s) { s = Math.max(0, Math.floor(s || 0)); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); }
  function svg(d) { return '<svg class="ico" viewBox="0 0 16 16" aria-hidden="true"><path d="' + d + '"></path></svg>'; }
  var PLAY = 'M5 3.5v9l7-4.5z', PAUSE = 'M4.5 3.5h2.5v9H4.5zM9 3.5h2.5v9H9z', BACK = 'M3 8a5 5 0 1 0 1.5-3.5M3 2.5v2.5h2.5', FWD = 'M13 8a5 5 0 1 1-1.5-3.5M13 2.5v2.5h-2.5';
  var players = [];

  function holder() { return document.getElementById('tw-mini'); }
  function heldAudio() { var h = holder(); return h ? Array.from(h.querySelectorAll('audio[data-audio]')) : []; }

  function enhance(el) {
    if (el._tw) return;
    el._tw = true;
    // Back on a page whose recording is still playing in the background: the full player takes that audio over,
    // so playback carries on and nothing is fetched again.
    var id = el.getAttribute('data-audio');
    var held = heldAudio().find(function (a) { return a.getAttribute('data-audio') === id; });
    var audio = held || el;
    var src = el.getAttribute('src');
    var kind = audio.getAttribute('data-kind') === 'Voicemail' ? 'voicemail' : 'recording';
    var box = document.createElement('div');
    box.className = 'player tw-player';
    box.innerHTML =
      '<button type="button" class="pp" aria-label="Play the ' + kind + '">' + svg(PLAY) + '</button>' +
      '<div style="min-width:0"><canvas class="wave" role="slider" tabindex="0" aria-label="Position in the ' + kind + '" aria-valuemin="0" aria-valuenow="0"></canvas>' +
      '<div class="meta"><span class="now">0:00</span><span class="left"></span></div></div>' +
      '<div class="ctl"><button type="button" class="iconbtn back" aria-label="Back 10 seconds" title="Back 10s">' + svg(BACK) + '</button>' +
      '<button type="button" class="iconbtn fwd" aria-label="Forward 10 seconds" title="Forward 10s">' + svg(FWD) + '</button>' +
      '<button type="button" class="speed" aria-label="Playback speed">1×</button></div>';
    el.after(box);
    if (held) el.remove(); else { audio.controls = false; audio.hidden = true; audio.preload = 'none'; }
    audio._box = box;
    var pp = box.querySelector('.pp'), cv = box.querySelector('canvas'), now = box.querySelector('.now'), left = box.querySelector('.left'), speed = box.querySelector('.speed');
    // Until the real waveform is known, a flat placeholder that says "not loaded" rather than inventing a shape.
    function draw() {
      var r = cv.getBoundingClientRect(); if (!r.width) return;
      var dpr = Math.min(2, window.devicePixelRatio || 1);
      cv.width = r.width * dpr; cv.height = r.height * dpr;
      var ctx = cv.getContext('2d'); ctx.scale(dpr, dpr);
      var cs = getComputedStyle(document.documentElement);
      var played = cs.getPropertyValue('--live').trim(), rest = cs.getPropertyValue('--fg-4').trim();
      var f = audio.duration ? audio.currentTime / audio.duration : 0;
      if (!audio._peaks) { ctx.fillStyle = rest; ctx.globalAlpha = .6; ctx.fillRect(0, r.height / 2 - .5, r.width, 1); return; }
      var n = audio._peaks.length, bw = r.width / n;
      for (var i = 0; i < n; i++) {
        var h = Math.max(1.5, audio._peaks[i] * (r.height - 4));
        ctx.globalAlpha = i / n < f ? 1 : .55; ctx.fillStyle = i / n < f ? played : rest;
        ctx.fillRect(i * bw + bw * .2, (r.height - h) / 2, Math.max(1, bw * .6), h);
      }
      ctx.globalAlpha = 1; ctx.fillStyle = cs.getPropertyValue('--fg').trim(); ctx.fillRect(f * r.width - .75, 0, 1.5, r.height);
    }
    function times() {
      now.textContent = fmt(audio.currentTime);
      left.textContent = audio.duration && isFinite(audio.duration) ? '-' + fmt(audio.duration - audio.currentTime) : '';
      cv.setAttribute('aria-valuenow', String(Math.round(audio.currentTime)));
      cv.setAttribute('aria-valuetext', fmt(audio.currentTime) + (audio.duration ? ' of ' + fmt(audio.duration) : ''));
      if (audio.duration) cv.setAttribute('aria-valuemax', String(Math.round(audio.duration)));
      draw(); sync();
    }
    // the one fetch: the waveform and the playback both come from it
    function load() {
      if (audio._loading) return audio._loading;
      pp.setAttribute('aria-busy', 'true');
      audio._loading = fetch(src, { credentials: 'same-origin' }).then(function (res) {
        if (!res.ok) throw new Error(res.status === 404 ? 'This audio is not available to you any more.' : 'The audio could not be loaded.');
        return res.arrayBuffer();
      }).then(function (buf) {
        audio.src = URL.createObjectURL(new Blob([buf], { type: 'audio/mpeg' }));
        var Ctx = window.AudioContext || window.webkitAudioContext;
        if (Ctx) {
          var ac = new Ctx();
          ac.decodeAudioData(buf.slice(0)).then(function (decoded) {
            var data = decoded.getChannelData(0), n = 160, size = Math.floor(data.length / n), out = [], max = 0;
            for (var i = 0; i < n; i++) { var peak = 0; for (var j = i * size; j < (i + 1) * size; j += 16) { var v = Math.abs(data[j]); if (v > peak) peak = v; } out.push(peak); if (peak > max) max = peak; }
            audio._peaks = out.map(function (p) { return max ? p / max : 0; });
            draw(); if (ac.close) ac.close();
          }, function () { /* the waveform is a nicety; playback still works */ });
        }
        pp.removeAttribute('aria-busy');
      }).catch(function (e) {
        pp.removeAttribute('aria-busy'); audio._loading = null;
        box.insertAdjacentHTML('afterend', '<p class="hint" role="status">' + e.message + '</p>');
      });
      return audio._loading;
    }
    function play() {
      players.forEach(function (p) { if (p !== audio) p.pause(); });
      // Into the holder the layout keeps across page changes, before it starts, so leaving the page does not stop it.
      var h = holder(); if (h && audio.parentNode !== h) h.appendChild(audio);
      load().then(function () { if (audio.src) audio.play(); });
    }
    pp.addEventListener('click', function () { audio.paused ? play() : audio.pause(); });
    audio.addEventListener('play', function () { pp.innerHTML = svg(PAUSE); pp.setAttribute('aria-label', 'Pause'); tickLoop(); });
    audio.addEventListener('pause', function () { pp.innerHTML = svg(PLAY); pp.setAttribute('aria-label', 'Play the ' + kind); });
    audio.addEventListener('timeupdate', times);
    audio.addEventListener('loadedmetadata', times);
    function tickLoop() { if (audio.paused) return; draw(); requestAnimationFrame(tickLoop); }
    function seekTo(t) { if (!audio.src || !audio.duration) { play(); return; } audio.currentTime = Math.max(0, Math.min(audio.duration, t)); times(); }
    cv.addEventListener('click', function (e) { var r = cv.getBoundingClientRect(); if (audio.duration) seekTo((e.clientX - r.left) / r.width * audio.duration); else play(); });
    cv.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowRight') { seekTo(audio.currentTime + 5); e.preventDefault(); }
      if (e.key === 'ArrowLeft') { seekTo(audio.currentTime - 5); e.preventDefault(); }
      if (e.key === ' ' || e.key === 'k') { audio.paused ? play() : audio.pause(); e.preventDefault(); }
    });
    box.querySelector('.back').addEventListener('click', function () { seekTo(audio.currentTime - 10); });
    box.querySelector('.fwd').addEventListener('click', function () { seekTo(audio.currentTime + 10); });
    speed.addEventListener('click', function () { var s = audio.playbackRate === 1 ? 1.5 : audio.playbackRate === 1.5 ? 2 : audio.playbackRate === 2 ? .75 : 1; audio.playbackRate = s; speed.textContent = s + '×'; });
    if (window.ResizeObserver) new ResizeObserver(draw).observe(cv);
    if (players.indexOf(audio) < 0) {
      players.push(audio);
      audio.addEventListener('play', renderMini); audio.addEventListener('pause', renderMini); audio.addEventListener('timeupdate', renderMini);
    }
    if (audio.src) { times(); if (!audio.paused) { pp.innerHTML = svg(PAUSE); pp.setAttribute('aria-label', 'Pause'); tickLoop(); } }
    audio._seek = seekTo; audio._play = play;
    draw();
  }

  // A transcript line with data-at seeks the page's player there; the line being spoken is highlighted, and kept in
  // view inside the transcript's own scrolling box, never by scrolling the page.
  function sync() {
    var lines = document.querySelectorAll('.transcript [data-at]'); if (!lines.length) return;
    var audio = players.find(function (p) { return !p.paused || p.currentTime > 0; }); if (!audio) return;
    var t = audio.currentTime, k = -1;
    lines.forEach(function (l, i) { if (t >= Number(l.getAttribute('data-at'))) k = i; });
    lines.forEach(function (l, i) {
      var was = l.classList.contains('now');
      l.classList.toggle('now', i === k);
      if (i === k && !was) follow(l);
    });
  }
  function follow(line) {
    var box = line.closest('.transcript-lines'); if (!box || box.scrollHeight <= box.clientHeight) return;
    var top = line.offsetTop, bottom = top + line.offsetHeight;
    if (top < box.scrollTop || bottom > box.scrollTop + box.clientHeight) box.scrollTo({ top: Math.max(0, top - box.clientHeight / 3), behavior: 'smooth' });
  }
  document.addEventListener('click', function (e) {
    var line = e.target.closest && e.target.closest('.transcript [data-at]');
    if (!line || !players.length) return;
    var audio = players[0];
    var at = Number(line.getAttribute('data-at'));
    if (audio.src && audio.duration) { audio._seek(at); audio.play(); } else { audio._play(); audio.addEventListener('loadedmetadata', function once() { audio.removeEventListener('loadedmetadata', once); audio._seek(at); }); }
  });

  // The mini bar: shown while a recording is loaded in the holder and its full player is not on this page.
  function renderMini() {
    var h = holder(); if (!h) return;
    var audio = heldAudio().find(function (a) { return a.src; });
    var bar = h.querySelector('.mini');
    if (!audio || (audio._box && audio._box.isConnected)) { if (bar) bar.remove(); return; }
    if (!bar) {
      bar = document.createElement('div'); bar.className = 'mini'; bar.setAttribute('role', 'region'); bar.setAttribute('aria-label', 'Now playing');
      bar.innerHTML = '<button type="button" class="pp"></button><a class="lbl" style="text-decoration:none;color:inherit;min-width:0"><b></b><span></span></a>' +
        '<button type="button" class="iconbtn close" aria-label="Stop and close the player"><svg class="ico" viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8"></path></svg></button>';
      h.appendChild(bar);
      bar.querySelector('.pp').addEventListener('click', function () { var a = heldAudio().find(function (x) { return x.src; }); if (a) a.paused ? a.play() : a.pause(); });
      bar.querySelector('.close').addEventListener('click', function () {
        var a = heldAudio().find(function (x) { return x.src; }); if (!a) return;
        a.pause(); players.splice(players.indexOf(a), 1); a.remove(); bar.remove();
      });
    }
    var pp = bar.querySelector('.pp');
    pp.innerHTML = svg(audio.paused ? PLAY : PAUSE); pp.setAttribute('aria-label', audio.paused ? 'Play' : 'Pause');
    var lbl = bar.querySelector('.lbl'); lbl.href = audio.getAttribute('data-call-url') || '#';
    lbl.querySelector('b').textContent = audio.getAttribute('data-label') || 'Recording';
    lbl.querySelector('span').textContent = fmt(audio.currentTime) + (audio.duration && isFinite(audio.duration) ? ' / ' + fmt(audio.duration) : '');
  }

  function init() { document.querySelectorAll('audio[data-audio]').forEach(function (a) { if (!a.closest('#tw-mini')) enhance(a); }); renderMini(); }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();
  if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener('enhancedload', init);
  new MutationObserver(function (records) {
    for (var i = 0; i < records.length; i++) for (var j = 0; j < records[i].addedNodes.length; j++) {
      var n = records[i].addedNodes[j];
      if (n.nodeType === 1 && (n.matches('audio[data-audio]') || n.querySelector('audio[data-audio]'))) { init(); return; }
    }
  }).observe(document.documentElement, { childList: true, subtree: true });
})();
