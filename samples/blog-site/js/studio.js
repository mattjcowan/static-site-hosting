/* The studio: write posts, manage accounts, change your password. */
(function () {
  'use strict';

  var me = null;
  var editing = null; // slug of the post open in the editor, or null for a new one
  var slugTouched = false;

  function $(id) { return document.getElementById(id); }

  function flash(kind, html) {
    $('studio-message').innerHTML = html ? '<div class="notice ' + kind + '">' + html + '</div>' : '';
    if (html) window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  function failed(result) {
    if (result.status === 401) { window.location.href = '/login.html'; return true; }
    if (!result.ok) { flash('error', Sky.escape(result.data.error || 'Something went wrong.')); return true; }
    return false;
  }

  // ---- tabs ---------------------------------------------------------------------
  function showTab(name) {
    document.querySelectorAll('[data-tab]').forEach(function (b) { b.setAttribute('aria-selected', String(b.getAttribute('data-tab') === name)); });
    document.querySelectorAll('[data-panel]').forEach(function (p) { p.hidden = p.getAttribute('data-panel') !== name; });
    flash();
    if (name === 'users') loadUsers();
  }
  document.querySelectorAll('[data-tab]').forEach(function (b) {
    b.addEventListener('click', function () { showTab(b.getAttribute('data-tab')); });
  });

  // ---- posts ----------------------------------------------------------------------
  function loadPosts() {
    Sky.api('/api/posts').then(function (result) {
      if (failed(result)) return;
      var posts = result.data.posts || [];
      $('post-list').innerHTML = posts.length ? posts.map(function (p) {
        return '<li><div class="grow"><strong>' + Sky.escape(p.title) + '</strong>' +
          (p.published ? '' : ' <span class="tag draft">draft</span>') +
          '<div class="meta"><span>/journal/' + Sky.escape(p.slug) + '</span><span>updated ' + Sky.date(p.updatedUtc) + '</span></div></div>' +
          '<a class="btn" href="' + Sky.postUrl(p.slug) + '">View</a>' +
          '<button type="button" class="btn" data-edit="' + Sky.escape(p.slug) + '">Edit</button></li>';
      }).join('') : '<li class="muted">No posts yet. Write the first one.</li>';
    });
  }

  $('post-list').addEventListener('click', function (event) {
    var button = event.target.closest('[data-edit]');
    if (button) openEditor(button.getAttribute('data-edit'));
  });

  function openEditor(slug) {
    editing = slug;
    slugTouched = !!slug;
    $('post-form').reset();
    $('post-preview').innerHTML = '';
    $('editor-heading').textContent = slug ? 'Edit post' : 'New post';
    $('post-slug').readOnly = !!slug;
    $('delete-post').hidden = !slug;
    $('post-editor').hidden = false;
    $('post-list-panel').hidden = true;

    if (!slug) { $('post-title-input').focus(); return; }

    Sky.api('/api/posts/' + encodeURIComponent(slug)).then(function (result) {
      if (failed(result)) return;
      var post = result.data.post;
      $('post-title-input').value = post.title;
      $('post-slug').value = post.slug;
      $('post-summary').value = post.summary;
      $('post-tags').value = post.tags.join(', ');
      $('post-published').checked = post.published;
      $('post-body').value = result.data.body || '';
      $('post-preview').innerHTML = result.data.html;
    });
  }

  function closeEditor() {
    $('post-editor').hidden = true;
    $('post-list-panel').hidden = false;
    history.replaceState(null, '', window.location.pathname);
    loadPosts();
  }

  $('new-post').addEventListener('click', function () { openEditor(null); });
  $('cancel-edit').addEventListener('click', closeEditor);

  // The address follows the title until someone edits it by hand.
  $('post-title-input').addEventListener('input', function () {
    if (slugTouched) return;
    $('post-slug').value = this.value.toLowerCase().normalize('NFKD').replace(/[^\w\s-]/g, '')
      .trim().replace(/[\s_-]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 80);
  });
  $('post-slug').addEventListener('input', function () { slugTouched = true; });

  // Live preview, rendered by the same server code as the published page.
  var previewTimer = null;
  $('post-body').addEventListener('input', function () {
    clearTimeout(previewTimer);
    var text = this.value;
    previewTimer = setTimeout(function () {
      Sky.api('/api/preview', { method: 'POST', body: { body: text } }).then(function (result) {
        if (result.ok) $('post-preview').innerHTML = result.data.html;
      });
    }, 300);
  });

  $('post-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var slug = $('post-slug').value.trim();
    Sky.api('/api/posts/' + encodeURIComponent(slug), {
      method: 'PUT',
      body: {
        title: $('post-title-input').value,
        summary: $('post-summary').value,
        body: $('post-body').value,
        tags: $('post-tags').value.split(',').map(function (t) { return t.trim(); }).filter(Boolean),
        published: $('post-published').checked
      }
    }).then(function (result) {
      if (failed(result)) return;
      closeEditor();
      flash('ok', 'Saved <a href="' + Sky.postUrl(slug) + '">' + Sky.escape(result.data.post.title) + '</a>.');
    });
  });

  $('delete-post').addEventListener('click', function () {
    if (!editing || !confirm('Delete this post? This cannot be undone.')) return;
    Sky.api('/api/posts/' + encodeURIComponent(editing), { method: 'DELETE' }).then(function (result) {
      if (failed(result)) return;
      closeEditor();
      flash('ok', 'The post was deleted.');
    });
  });

  // ---- users (administrators) ---------------------------------------------------------
  function loadUsers() {
    Sky.api('/api/users').then(function (result) {
      if (failed(result)) return;
      $('user-rows').innerHTML = result.data.users.map(function (u) {
        var self = u.id === me.id;
        return '<tr><td><strong>' + Sky.escape(u.displayName) + '</strong><div class="muted small">' + Sky.escape(u.username) + (self ? ' · you' : '') + '</div></td>' +
          '<td><select data-role="' + u.id + '"' + (self ? ' disabled' : '') + '>' +
            '<option value="editor"' + (u.role === 'editor' ? ' selected' : '') + '>editor</option>' +
            '<option value="admin"' + (u.role === 'admin' ? ' selected' : '') + '>admin</option></select></td>' +
          '<td>' + (u.mustChangePassword ? '<span class="tag draft">must set password</span>' : '<span class="tag">active</span>') + '</td>' +
          '<td class="muted small">' + (u.lastLoginUtc ? Sky.date(u.lastLoginUtc) : 'never') + '</td>' +
          '<td>' + (self ? '' : '<div class="actions" style="margin:0"><button type="button" class="btn" data-reset="' + u.id + '">Reset password</button>' +
            '<button type="button" class="btn danger" data-delete="' + u.id + '" data-name="' + Sky.escape(u.username) + '">Delete</button></div>') + '</td></tr>';
      }).join('');
    });
  }

  function showPassword(who, password) {
    flash('ok', 'Give ' + Sky.escape(who) + ' this password. It is shown once, and they choose their own at first sign-in:' +
      '<div class="secret" style="margin-top:.4rem">' + Sky.escape(password) + '</div>');
  }

  $('user-rows').addEventListener('change', function (event) {
    var select = event.target.closest('[data-role]');
    if (!select) return;
    Sky.api('/api/users/' + select.getAttribute('data-role'), { method: 'PUT', body: { role: select.value } }).then(function (result) {
      if (failed(result)) { loadUsers(); return; }
      flash('ok', Sky.escape(result.data.user.username) + ' is now ' + result.data.user.role + '.');
    });
  });

  $('user-rows').addEventListener('click', function (event) {
    var reset = event.target.closest('[data-reset]');
    var remove = event.target.closest('[data-delete]');

    if (reset) {
      if (!confirm('Reset this password? They will be signed out everywhere.')) return;
      Sky.api('/api/users/' + reset.getAttribute('data-reset') + '/reset-password', { method: 'POST', body: {} }).then(function (result) {
        if (failed(result)) return;
        showPassword('them', result.data.password);
        loadUsers();
      });
    }

    if (remove) {
      if (!confirm('Delete the account ' + remove.getAttribute('data-name') + '?')) return;
      Sky.api('/api/users/' + remove.getAttribute('data-delete'), { method: 'DELETE' }).then(function (result) {
        if (failed(result)) return;
        flash('ok', 'The account was deleted.');
        loadUsers();
      });
    }
  });

  $('user-form').addEventListener('submit', function (event) {
    event.preventDefault();
    Sky.api('/api/users', {
      method: 'POST',
      body: { username: $('new-username').value.trim(), displayName: $('new-display').value, role: $('new-role').value, password: $('new-password').value || null }
    }).then(function (result) {
      if (failed(result)) return;
      $('user-form').reset();
      if (result.data.password) showPassword(result.data.user.username, result.data.password);
      else flash('ok', Sky.escape(result.data.user.username) + ' was added. They choose their own password at first sign-in.');
      loadUsers();
    });
  });

  // ---- account --------------------------------------------------------------------------
  $('account-form').addEventListener('submit', function (event) {
    event.preventDefault();
    Sky.api('/api/auth/password', { method: 'POST', body: { current: $('current-password').value, next: $('account-next').value } })
      .then(function (result) {
        if (failed(result)) return;
        $('account-form').reset();
        flash('ok', 'Your password was changed. Any other sessions have been signed out.');
      });
  });

  // ---- start ----------------------------------------------------------------------------------
  Sky.me.then(function (user) {
    if (!user) { window.location.href = '/login.html'; return; }
    if (user.mustChangePassword) { window.location.href = '/login.html'; return; }

    me = user;
    $('studio-greeting').textContent = 'Clear skies, ' + user.displayName;
    $('users-tab').hidden = user.role !== 'admin';
    loadPosts();

    openFromHash();
    window.addEventListener('hashchange', openFromHash);
  });

  // /studio.html#edit=<slug> opens that post, whether arriving from a post page or changing the hash here.
  function openFromHash() {
    var edit = window.location.hash.match(/^#edit=(.+)$/);
    if (!edit) return;
    showTab('posts');
    openEditor(decodeURIComponent(edit[1]));
  }
})();
