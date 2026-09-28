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

  // ---- news while the page is open ------------------------------------------------
  // The journal's group hears about posts going up, changing and coming down (Posts.cs sends
  // them), and a note in the corner offers the fresh list. Nothing is fetched until the reader asks.
  if (!window.site) return;

  var said = { created: 'A new post is up', published: 'A new post is up', updated: 'A post was updated', unpublished: 'A post was taken down', deleted: 'A post was taken down' };

  site.realtime.join('journal').catch(function (error) { console.warn('Could not follow the journal:', error.message); });

  // How many pages are reading the journal: Realtime.cs counts the group every 30 seconds and sends
  // readers.online when the number changes. This page counts too.
  site.realtime.on('readers.online', function (online) {
    var badge = document.getElementById('readers-online');
    badge.textContent = online.count + ' reading now';
    badge.hidden = !(online.count > 0);
  });

  site.realtime.on('post.changed', function (post) {
    var old = document.querySelector('.toast');
    if (old) old.remove();

    var toast = document.createElement('div');
    toast.className = 'toast';
    toast.setAttribute('role', 'status');
    toast.innerHTML = '<span>' + (said[post.action] || 'A post changed') + ': <strong>' + Sky.escape(post.title) + '</strong>. ' +
      '<a href="">Refresh</a> to see it.</span><button type="button" aria-label="Dismiss">×</button>';
    toast.querySelector('button').addEventListener('click', function () { toast.remove(); });
    document.body.appendChild(toast);
  });
})();
