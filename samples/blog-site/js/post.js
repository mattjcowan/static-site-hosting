/* One journal entry. The address is /journal/<slug>, which _redirects serves from this page. */
(function () {
  'use strict';

  var match = window.location.pathname.match(/^\/journal\/([a-z0-9-]+)\/?$/);
  var slug = match ? match[1] : new URLSearchParams(window.location.search).get('slug');

  var title = document.getElementById('post-title');
  var meta = document.getElementById('post-meta');
  var body = document.getElementById('post-body');

  if (!slug) { title.textContent = 'No post chosen'; body.innerHTML = ''; return; }

  Sky.api('/api/posts/' + encodeURIComponent(slug)).then(function (result) {
    if (!result.ok) {
      document.title = 'Not found · ' + Sky.name;
      title.textContent = 'Lost in space';
      body.innerHTML = '<p>There is no entry at this address. <a href="/journal">Back to the journal</a>.</p>';
      return;
    }

    var post = result.data.post;
    document.title = post.title + ' · ' + Sky.name;
    title.textContent = post.title;
    meta.innerHTML = '<span>' + Sky.date(post.createdUtc) + '</span><span>by ' + Sky.escape(post.author) + '</span>' +
      '<span>' + post.readingMinutes + ' min read</span>' +
      (post.published ? '' : '<span class="tag draft">draft</span>') +
      post.tags.map(function (t) { return '<a class="tag" href="/journal?tag=' + encodeURIComponent(t) + '">' + Sky.escape(t) + '</a>'; }).join('');

    // Rendered by the server from Markdown, with raw HTML and unsafe links already removed.
    body.innerHTML = result.data.html;

    Sky.me.then(function (user) {
      if (user) document.getElementById('post-actions').innerHTML =
        '<a class="btn" href="/studio#edit=' + encodeURIComponent(post.slug) + '">Edit in the studio</a>';
    });
  });
})();
