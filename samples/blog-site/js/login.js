/* Sign in, then (when the password was chosen by someone else) choose a new one. */
(function () {
  'use strict';

  var current = '';

  function message(id, kind, text) {
    document.getElementById(id).innerHTML = text ? '<div class="notice ' + kind + '">' + Sky.escape(text) + '</div>' : '';
  }

  // Where to go once signed in: the page in ?return= (the studio's gate sends people here with
  // one), or the studio. Only ever a page on this site, since anyone can put a return address
  // in a link. A redirect carries the fragment along, so #edit=<slug> survives the round trip.
  function destination() {
    var here = window.location.origin;
    var target;
    try { target = new URL(new URLSearchParams(window.location.search).get('return') || '/studio', here); }
    catch (e) { target = null; }
    if (!target || target.origin !== here) target = new URL('/studio', here);
    if (!target.hash) target.hash = window.location.hash;
    return target.pathname + target.search + target.hash;
  }

  function goOn() { window.location.href = destination(); }

  // Already signed in with nothing owed: straight to the studio.
  Sky.me.then(function (user) { if (user && !user.mustChangePassword) goOn(); });

  document.getElementById('login-form').addEventListener('submit', function (event) {
    event.preventDefault();
    current = document.getElementById('password').value;
    message('login-message');

    Sky.api('/api/auth/login', { method: 'POST', body: { username: document.getElementById('username').value, password: current } })
      .then(function (result) {
        if (!result.ok) { message('login-message', 'error', result.data.error || 'Sign-in failed.'); return; }
        if (!result.data.mustChangePassword) { goOn(); return; }

        document.getElementById('sign-in').hidden = true;
        document.getElementById('change-password').hidden = false;
        document.getElementById('next').focus();
      });
  });

  document.getElementById('password-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var next = document.getElementById('next').value;
    if (next !== document.getElementById('confirm').value) { message('password-message', 'error', 'The two passwords are different.'); return; }

    Sky.api('/api/auth/password', { method: 'POST', body: { current: current, next: next } }).then(function (result) {
      if (!result.ok) { message('password-message', 'error', result.data.error || 'That did not work.'); return; }
      goOn();
    });
  });
})();
