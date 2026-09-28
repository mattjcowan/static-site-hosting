/* The studio: write posts, manage accounts, see who is online, change your password. */
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
    if (result.status === 401) { window.location.href = '/login'; return true; }
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

  // Suggest: the site's AI writes a summary of the Markdown in the editor (Suggest in _functions/Posts.cs).
  // Without an AI provider chosen for the site, the function answers 503, and its message says so.
  $('suggest-summary').addEventListener('click', function () {
    var button = this;
    var slug = $('post-slug').value.trim();
    var body = $('post-body').value;
    if (!slug || !body.trim()) { flash('error', 'Give the post a title and some text first.'); return; }

    button.disabled = true;
    Sky.api('/api/posts/' + encodeURIComponent(slug) + '/summary', { method: 'POST', body: { body: body } }).then(function (result) {
      if (!failed(result)) $('post-summary').value = result.data.summary;
    }).then(function () { button.disabled = false; });
  });

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
    loadOnline();
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

  // Every page open on the site now: each realtime connection with its user and groups (/api/online, in
  // _functions/Realtime.cs). This tab is one of them.
  function loadOnline() {
    Sky.api('/api/online').then(function (result) {
      if (!result.ok) return;
      var connections = result.data.connections || [];
      $('online-list').innerHTML = connections.length ? connections.map(function (c) {
        return '<li><div class="grow"><strong>' + Sky.escape(c.user || 'anonymous') + '</strong>' +
          '<div class="meta"><span>' + (c.groups.length ? Sky.escape(c.groups.join(', ')) : 'no groups') + '</span>' +
          '<span>since ' + new Date(c.connectedUtc).toLocaleTimeString() + '</span></div></div></li>';
      }).join('') : '<li class="muted">Nobody has the site open.</li>';
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

  // ---- ask the sky -------------------------------------------------------------------------
  // A question for the site's AI, its answer written into the panel as it streams in. The host
  // says whether the site has AI for its pages (site.ai.enabled); _functions/Ai.cs decides that
  // only signed-in writers get an answer.
  function setUpAsk() {
    if (!window.site || !site.ai.enabled) return;
    $('ask-panel').hidden = false;

    $('ask-form').addEventListener('submit', function (event) {
      event.preventDefault();
      var question = $('ask-question').value.trim();
      if (!question || $('ask-send').disabled) return;

      $('ask-send').disabled = true;
      $('ask-error').innerHTML = '';
      $('ask-answer').textContent = '';

      site.ai.chat(question, {
        system: 'You help an astronomy blogger write field notes. Be brief.',
        onText: function (text) { $('ask-answer').textContent += text; }
      }).catch(function (error) {
        var message = error.status === 429 ? 'That is a lot of questions. Try again in a minute.' : error.message;
        $('ask-error').innerHTML = '<div class="notice error">' + Sky.escape(message) + '</div>';
      }).then(function () { $('ask-send').disabled = false; });
    });
  }

  // ---- news from the other writers ----------------------------------------------------------
  // The studio's group hears about every post saved or deleted, drafts included (Posts.cs sends
  // them; _functions/Realtime.cs keeps the group for signed-in writers), so the list stays current.
  function followStudio() {
    if (!window.site) return;
    site.realtime.join('studio').catch(function (error) { console.warn('Could not follow the studio:', error.message); });
    site.realtime.on('post.changed', function () { if (!$('post-list-panel').hidden) loadPosts(); });
  }

  // ---- news about your own account ------------------------------------------------------------
  // When an administrator changes your role, resets your password or removes your account, Users.cs
  // sends account.changed to every page you have open. A reset has already ended this session.
  function followAccount() {
    if (!window.site) return;
    site.realtime.on('account.changed', function (change) {
      var old = document.querySelector('.toast');
      if (old) old.remove();

      var next = change.change === 'role' ? ' <a href="">Reload</a> to see what changed.'
        : change.change === 'password' ? ' <a href="/login">Go to sign-in</a>.' : '';
      var toast = document.createElement('div');
      toast.className = 'toast';
      toast.setAttribute('role', 'status');
      toast.innerHTML = '<span>' + Sky.escape(change.message) + next + '</span><button type="button" aria-label="Dismiss">×</button>';
      toast.querySelector('button').addEventListener('click', function () { toast.remove(); });
      document.body.appendChild(toast);
    });
  }

  // ---- start ----------------------------------------------------------------------------------
  Sky.me.then(function (user) {
    if (!user) { window.location.href = '/login'; return; }
    if (user.mustChangePassword) { window.location.href = '/login'; return; }

    me = user;
    $('studio-greeting').textContent = 'Clear skies, ' + user.displayName;
    $('users-tab').hidden = user.role !== 'admin';
    loadPosts();
    setUpAsk();
    followStudio();
    followAccount();

    openFromHash();
    window.addEventListener('hashchange', openFromHash);
  });

  // /studio#edit=<slug> opens that post, whether arriving from a post page or changing the hash here.
  function openFromHash() {
    var edit = window.location.hash.match(/^#edit=(.+)$/);
    if (!edit) return;
    showTab('posts');
    openEditor(decodeURIComponent(edit[1]));
  }
})();
