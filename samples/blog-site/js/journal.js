/* The Journal: every post, filterable by topic. */
(function () {
  'use strict';

  var list = document.getElementById('posts');
  var tagBar = document.getElementById('tags');
  var active = new URLSearchParams(window.location.search).get('tag');
  var all = [];

  function render() {
    var posts = all.filter(function (p) { return !active || p.tags.indexOf(active) >= 0; });
    list.innerHTML = posts.length ? posts.map(function (post) {
      return '<a class="card" href="' + Sky.postUrl(post.slug) + '">' +
        '<div class="meta"><span>' + Sky.date(post.createdUtc) + '</span><span>' + post.readingMinutes + ' min read</span>' +
        (post.published ? '' : '<span class="tag draft">draft</span>') + '</div>' +
        '<h3>' + Sky.escape(post.title) + '</h3><p>' + Sky.escape(post.summary) + '</p>' +
        '<div class="meta">' + post.tags.map(function (t) { return '<span class="tag">' + Sky.escape(t) + '</span>'; }).join('') + '</div></a>';
    }).join('') : '<p class="muted">Nothing under this topic yet.</p>';

    var tags = [];
    all.forEach(function (p) { p.tags.forEach(function (t) { if (tags.indexOf(t) < 0) tags.push(t); }); });
    tags.sort();
    tagBar.innerHTML = ['<button type="button" class="tag" data-tag="" aria-pressed="' + !active + '">all</button>']
      .concat(tags.map(function (t) {
        return '<button type="button" class="tag" data-tag="' + Sky.escape(t) + '" aria-pressed="' + (t === active) + '">' + Sky.escape(t) + '</button>';
      })).join('');
  }

  tagBar.addEventListener('click', function (event) {
    var button = event.target.closest('[data-tag]');
    if (!button) return;
    active = button.getAttribute('data-tag') || null;
    history.replaceState(null, '', active ? '?tag=' + encodeURIComponent(active) : window.location.pathname);
    render();
  });

  Sky.api('/api/posts').then(function (result) {
    all = result.data.posts || [];
    render();
  });
})();
