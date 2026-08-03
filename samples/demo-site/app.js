// Small helper for poking at the host's path resolution.
//
// Every page declares the file it actually is via <body data-source="…">. Comparing that
// to location.pathname shows whether you got the file you asked for or an index.html the
// server walked up to.

(function () {
  'use strict';

  var year = document.getElementById('year');
  if (year) year.textContent = new Date().getFullYear();

  var source = document.body.getAttribute('data-source');
  if (!source) return;

  var requested = window.location.pathname;
  var exact = requested === source || requested === source.replace(/index\.html$/, '');
  var viaHtmlSuffix = source === requested + '.html';

  var verdict;
  if (exact) verdict = ['hit', 'exact match'];
  else if (viaHtmlSuffix) verdict = ['hit', 'resolved to the .html file'];
  else verdict = ['fallback', 'fell back up the tree'];

  var banner = document.createElement('div');
  banner.className = 'resolved';
  banner.innerHTML =
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
