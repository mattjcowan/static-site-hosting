/* Night Sky Field Notes: shared by every page. */
(function () {
  'use strict';

  var Sky = (window.Sky = {});

  // ---- the journal's name -----------------------------------------------------
  // SITE_NAME and SITE_TAGLINE are site variables (_variables.json), which /_host/site.js hands
  // every page, so the journal can be renamed on the host without a redeploy. The markup carries
  // the defaults, which stay put if site.js did not load.
  var DEFAULT_NAME = 'Night Sky Field Notes';
  Sky.name = window.site ? window.site.get('SITE_NAME', DEFAULT_NAME) : DEFAULT_NAME;

  document.title = document.title.replace(DEFAULT_NAME, Sky.name);
  document.querySelectorAll('[data-site-name]').forEach(function (el) { el.textContent = Sky.name; });

  var tagline = window.site ? window.site.get('SITE_TAGLINE', '') : '';
  if (tagline) document.querySelectorAll('[data-site-tagline]').forEach(function (el) { el.textContent = tagline; });

  // ---- API ----------------------------------------------------------------
  // Every change carries X-Night-Sky: a header a cross-site page cannot add without a
  // CORS preflight, which is what keeps another site from posting here as you.
  Sky.api = function (path, options) {
    options = options || {};
    var init = { method: options.method || 'GET', credentials: 'same-origin', headers: { 'X-Night-Sky': '1' } };
    if (options.body !== undefined) {
      init.headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(options.body);
    }
    return fetch(path, init).then(function (response) {
      return response.json().catch(function () { return {}; }).then(function (data) {
        return { ok: response.ok, status: response.status, data: data };
      });
    });
  };

  Sky.escape = function (value) {
    var div = document.createElement('div');
    div.textContent = value == null ? '' : String(value);
    return div.innerHTML;
  };

  Sky.date = function (iso) {
    return new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
  };

  Sky.postUrl = function (slug) { return '/journal/' + encodeURIComponent(slug); };

  // ---- who is signed in -----------------------------------------------------
  Sky.me = Sky.api('/api/auth/me').then(function (r) { return (r.data && r.data.user) || null; });

  Sky.me.then(function (user) {
    var slot = document.getElementById('nav-auth');
    if (!slot) return;
    if (!user) {
      slot.innerHTML = '<a href="/login">Sign in</a>';
      return;
    }
    slot.innerHTML = '<a href="/studio">Studio</a><button type="button" id="sign-out">Sign out</button>';
    document.getElementById('sign-out').addEventListener('click', function () {
      Sky.api('/api/auth/logout', { method: 'POST' }).then(function () { window.location.href = '/'; });
    });
  });

  // ---- red-light mode ----------------------------------------------------------
  function storedRed() { try { return localStorage.getItem('red-light') === '1'; } catch (e) { return false; } }
  document.documentElement.classList.toggle('red-light', storedRed());

  document.addEventListener('click', function (event) {
    if (!event.target.closest('[data-red-toggle]')) return;
    var on = !document.documentElement.classList.contains('red-light');
    document.documentElement.classList.toggle('red-light', on);
    try { localStorage.setItem('red-light', on ? '1' : '0'); } catch (e) { /* private mode */ }
  });

  // Mark the current page in the nav. Its links leave .html off, since the host answers /journal with journal.html.
  document.querySelectorAll('.nav a[href]').forEach(function (a) {
    var here = window.location.pathname.replace(/\/index\.html$/, '/').replace(/\.html$/, '');
    if (a.getAttribute('href') === here || (here.indexOf('/journal') === 0 && a.getAttribute('href') === '/journal')) {
      a.setAttribute('aria-current', 'page');
    }
  });

  // ---- starfield ------------------------------------------------------------------
  // A canvas behind the hero: stars of a few sizes and colours, a slow twinkle, and now and
  // then a meteor. Motion stops for anyone who has asked their system for less of it.
  Sky.starfield = function (canvas) {
    var ctx = canvas.getContext('2d');
    var still = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    var stars = [], meteor = null, width = 0, height = 0, scale = window.devicePixelRatio || 1;

    function resize() {
      width = canvas.clientWidth; height = canvas.clientHeight;
      canvas.width = width * scale; canvas.height = height * scale;
      ctx.setTransform(scale, 0, 0, scale, 0, 0);
      var count = Math.round(width * height / 2600);
      stars = [];
      for (var i = 0; i < count; i++) {
        var big = Math.random() < 0.06;
        stars.push({
          x: Math.random() * width, y: Math.random() * height,
          r: big ? 1.1 + Math.random() * 0.9 : 0.3 + Math.random() * 0.8,
          tint: Math.random() < 0.15 ? '255,214,170' : Math.random() < 0.2 ? '190,210,255' : '255,255,255',
          phase: Math.random() * Math.PI * 2, speed: 0.4 + Math.random() * 1.4
        });
      }
    }

    function draw(t) {
      ctx.clearRect(0, 0, width, height);
      for (var i = 0; i < stars.length; i++) {
        var s = stars[i];
        var alpha = still ? 0.8 : 0.55 + 0.45 * Math.sin(s.phase + t * 0.001 * s.speed);
        ctx.beginPath();
        ctx.fillStyle = 'rgba(' + s.tint + ',' + alpha.toFixed(3) + ')';
        ctx.arc(s.x, s.y, s.r, 0, Math.PI * 2);
        ctx.fill();
        if (s.r > 1.3) {
          ctx.fillStyle = 'rgba(' + s.tint + ',' + (alpha * 0.12).toFixed(3) + ')';
          ctx.beginPath(); ctx.arc(s.x, s.y, s.r * 4, 0, Math.PI * 2); ctx.fill();
        }
      }

      if (!still) {
        if (!meteor && Math.random() < 0.004) {
          meteor = { x: Math.random() * width * 0.7 + width * 0.2, y: Math.random() * height * 0.35, life: 0 };
        }
        if (meteor) {
          meteor.life += 1;
          var head = { x: meteor.x - meteor.life * 7, y: meteor.y + meteor.life * 3.2 };
          var gradient = ctx.createLinearGradient(head.x, head.y, head.x + 90, head.y - 41);
          gradient.addColorStop(0, 'rgba(255,255,255,' + Math.max(0, 1 - meteor.life / 45) + ')');
          gradient.addColorStop(1, 'rgba(255,255,255,0)');
          ctx.strokeStyle = gradient; ctx.lineWidth = 1.6;
          ctx.beginPath(); ctx.moveTo(head.x, head.y); ctx.lineTo(head.x + 90, head.y - 41); ctx.stroke();
          if (meteor.life > 45) meteor = null;
        }
        window.requestAnimationFrame(draw);
      }
    }

    resize();
    window.addEventListener('resize', function () { resize(); if (still) draw(0); });
    if (still) draw(0); else window.requestAnimationFrame(draw);
  };

  document.querySelectorAll('canvas[data-starfield]').forEach(Sky.starfield);

  // ---- the Moon -----------------------------------------------------------------------
  // Phase from a known new moon and the mean synodic month. Good to within a few hours,
  // which is all a phase name and a drawing need.
  var SYNODIC = 29.530588853;
  var KNOWN_NEW = Date.UTC(2000, 0, 6, 18, 14); // 2000-01-06 18:14 UTC
  var DAY = 86400000;

  Sky.moon = function (when) {
    var days = (when.getTime() - KNOWN_NEW) / DAY;
    var age = ((days % SYNODIC) + SYNODIC) % SYNODIC;
    var angle = 2 * Math.PI * age / SYNODIC;
    var illumination = (1 - Math.cos(angle)) / 2;
    var cycles = Math.floor(days / SYNODIC);

    var names = ['New Moon', 'Waxing Crescent', 'First Quarter', 'Waxing Gibbous', 'Full Moon', 'Waning Gibbous', 'Last Quarter', 'Waning Crescent'];
    var name = names[Math.floor(((age / SYNODIC) * 8) + 0.5) % 8];

    function next(offset) {
      var t = KNOWN_NEW + (cycles + offset) * SYNODIC * DAY;
      while (t < when.getTime()) t += SYNODIC * DAY;
      return new Date(t);
    }

    return { age: age, illumination: illumination, name: name, waxing: age < SYNODIC / 2, nextFull: next(0.5), nextNew: next(1) };
  };

  var moonCount = 0;

  /** An SVG Moon lit as it is at `age` days: the lit limb, then the terminator as half an ellipse. */
  Sky.moonSvg = function (age) {
    // IDs inside inline SVG are page-wide, so each drawing needs its own or they share one clip.
    var id = 'moon' + (++moonCount);
    var r = 48, cx = 50, cy = 50;
    var k = Math.cos(2 * Math.PI * age / SYNODIC); // 1 at new, -1 at full
    var rx = Math.abs(k) * r;
    var waxing = age < SYNODIC / 2;
    var crescent = k > 0;

    // Waxing lights the right limb, waning the left. The terminator bulges towards the lit
    // side for a crescent and away from it for a gibbous Moon.
    var limbSweep = waxing ? 1 : 0;
    var termSweep = waxing ? (crescent ? 0 : 1) : (crescent ? 1 : 0);
    var lit = 'M' + cx + ' ' + (cy - r) +
              ' A' + r + ' ' + r + ' 0 0 ' + limbSweep + ' ' + cx + ' ' + (cy + r) +
              ' A' + rx.toFixed(2) + ' ' + r + ' 0 0 ' + termSweep + ' ' + cx + ' ' + (cy - r) + ' Z';

    return '<svg viewBox="0 0 100 100" role="img" aria-label="The Moon tonight">' +
      '<defs><radialGradient id="' + id + '-lit" cx="45%" cy="40%" r="65%"><stop offset="0" stop-color="#fff8e1"/><stop offset="1" stop-color="#d9c79a"/></radialGradient></defs>' +
      '<circle cx="50" cy="50" r="48" fill="#1c2340"/>' +
      '<path d="' + lit + '" fill="url(#' + id + '-lit)"/>' +
      // A few maria, clipped to the lit part so they only show in daylight.
      '<clipPath id="' + id + '-clip"><path d="' + lit + '"/></clipPath>' +
      '<g clip-path="url(#' + id + '-clip)" fill="#bfae83" opacity="0.55">' +
      '<ellipse cx="38" cy="36" rx="11" ry="8"/><ellipse cx="58" cy="30" rx="8" ry="6"/><ellipse cx="62" cy="56" rx="12" ry="9"/>' +
      '<ellipse cx="40" cy="62" rx="7" ry="5"/><circle cx="46" cy="80" r="3"/></g>' +
      '<circle cx="50" cy="50" r="48" fill="none" stroke="rgba(255,255,255,0.08)"/></svg>';
  };
})();
