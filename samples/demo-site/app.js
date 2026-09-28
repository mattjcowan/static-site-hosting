// Small helper for poking at the host's path resolution.
//
// Every page declares the file it actually is via <body data-source="…">. Comparing that
// to location.pathname shows whether you got the file you asked for or an index.html the
// server walked up to.

(function () {
  'use strict';

  var year = document.getElementById('year');
  if (year) year.textContent = new Date().getFullYear();

  // Realtime is on for every site, with nothing to set up. The host sends site.deployed to every open
  // page when a new version goes live, so a visitor with the old one can reload. A CI job can send
  // events of its own too, with POST /api/v1/sites/{domain}/realtime/publish. Only pages that load
  // /_host/site.js (the home page, here) have window.site.
  if (window.site) {
    window.site.realtime.on('site.deployed', function () {
      if (confirm('A new version of this site is up. Reload?')) location.reload();
    });
  }

  var source = document.body.getAttribute('data-source');
  if (!source) return;

  var requested = window.location.pathname;
  var exact = requested === source || requested === source.replace(/index\.html$/, '');
  var viaHtmlSuffix = source === requested + '.html';

  var verdict;
  if (exact) verdict = ['hit', 'exact match'];
  else if (viaHtmlSuffix) verdict = ['hit', 'resolved to the .html file'];
  else verdict = ['fallback', 'fell back up the tree'];

  // Pages that include /_host/site.js get window.site, with the variables _variables.json
  // declares public. SITE_TITLE defaults to "Sample site"; set it on the site page to change it.
  var title = window.site ? window.site.get('SITE_TITLE') : null;

  var banner = document.createElement('div');
  banner.className = 'resolved';
  banner.innerHTML =
    (title ? '<span>site <b>' + escapeHtml(title) + '</b></span>' : '') +
    '<span>requested <b>' + escapeHtml(requested) + '</b></span>' +
    '<span>served <b>' + escapeHtml(source) + '</b></span>' +
    '<span class="' + verdict[0] + '">' + verdict[1] + '</span>';

  document.body.appendChild(banner);

  function escapeHtml(value) {
    var div = document.createElement('div');
    div.textContent = value;
    return div.innerHTML;
  }
})();
