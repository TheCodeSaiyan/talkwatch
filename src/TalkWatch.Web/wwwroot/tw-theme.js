// Applies the viewer's chosen theme before the page paints, so a dark-theme viewer never sees a light flash.
// "system" (or nothing stored) leaves the choice to the operating system. Storage can be unavailable; then: system.
(function () {
  try {
    var t = localStorage.getItem('tw.theme');
    if (t === 'light' || t === 'dark') document.documentElement.dataset.theme = t;
  } catch (e) { /* private window or blocked storage: follow the system */ }
})();
