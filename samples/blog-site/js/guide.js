/* The dark-adaptation timer: twenty minutes, drawn as a ring filling up. */
(function () {
  'use strict';

  var TOTAL = 20 * 60;
  var CIRCUMFERENCE = 2 * Math.PI * 52;
  var ring = document.getElementById('timer-ring');
  var text = document.getElementById('timer-text');
  var note = document.getElementById('timer-note');
  var start = document.getElementById('timer-start');
  var started = null, tick = null;

  function show(elapsed) {
    var left = Math.max(0, TOTAL - elapsed);
    text.textContent = Math.floor(left / 60) + ':' + String(left % 60).padStart(2, '0');
    ring.setAttribute('stroke-dashoffset', (CIRCUMFERENCE * (1 - Math.min(1, elapsed / TOTAL))).toFixed(1));
    if (left === 0) {
      clearInterval(tick); tick = null;
      note.textContent = 'Fully dark-adapted. Averted vision now shows you the faint stuff.';
      start.textContent = 'Start again';
    }
  }

  start.addEventListener('click', function () {
    started = Date.now();
    clearInterval(tick);
    tick = setInterval(function () { show(Math.floor((Date.now() - started) / 1000)); }, 1000);
    note.textContent = 'Adapting… keep screens away, or switch on red light.';
    start.textContent = 'Restart';
    show(0);
  });

  document.getElementById('timer-reset').addEventListener('click', function () {
    clearInterval(tick); tick = null; started = null;
    note.textContent = 'Start this when you step outside. Put the phone away; come back when it is full.';
    start.textContent = 'Start adapting';
    show(0);
  });
})();
