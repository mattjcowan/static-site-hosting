(function () {
  'use strict';

  // The Files tab (Pages/Sites/Details.cshtml): the files of one release, the live one unless
  // another is picked, with an editor for text (Monaco from the CDN, or a plain text box without
  // it) and a preview for images. Saving writes into that release at once
  // (Endpoints/SiteFileEditorEndpoints.cs): in the live one, visitors get it straight away.
  // Duplicate makes a copy to go back to, or to work on before making it live.
  var root = document.querySelector('[data-files]');
  if (!root) return;

  var MONACO = 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min';
  var IMAGES = {
    png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp',
    avif: 'image/avif', svg: 'image/svg+xml', ico: 'image/x-icon', bmp: 'image/bmp'
  };
  var CONFIG = { '_headers': 'js.files.config.headers', '_redirects': 'js.files.config.redirects', '_variables.json': 'js.files.config.variables' };
  var FILTER_LIMIT = 500;

  var setup = JSON.parse(root.querySelector('[data-files-setup]').textContent);
  var csrf = root.getAttribute('data-csrf');
  var panel = root.closest('.tab-panel');
  var icons = setup.icons;

  var el = {
    release: root.querySelector('[data-files-release]'),
    duplicate: root.querySelector('[data-files-duplicate]'),
    makeLive: root.querySelector('[data-files-make-live]'),
    note: root.querySelector('[data-files-note]'),
    filter: root.querySelector('[data-files-filter]'),
    newFile: root.querySelector('[data-files-new]'),
    upload: root.querySelector('[data-files-upload]'),
    uploadInput: root.querySelector('[data-files-upload-input]'),
    side: root.querySelector('.files-side'),
    tree: root.querySelector('[data-files-tree]'),
    main: root.querySelector('[data-files-main]'),
    head: root.querySelector('[data-files-head]'),
    view: root.querySelector('[data-files-view]')
  };

  var notice = document.createElement('div');
  notice.className = 'files-notice';
  notice.setAttribute('role', 'status');
  el.main.insertBefore(notice, el.main.firstChild);

  var query = new URLSearchParams(location.search);
  var wanted = query.get('release') || null;   // the release asked for; null for the live one
  var listing = null;      // the server's last answer: { release, releases, files }
  var files = {};          // path -> { path, size, configuration }
  var selected = null;     // { folder: true|false, path }
  var docs = {};           // path -> { text, saved, bom, model, release } for text files opened
  var expanded = remembered();
  var replacing = null;    // the path an "Upload a new version" is for
  var monaco = null, editor = null, diffEditor = null, originalModel = null, textarea = null;
  var monacoState = 'idle';
  var editorBox = null;
  var comparing = false;
  var started = false, stale = false;

  // ------------------------------------------------------------------ helpers

  function folderOf(path) { var i = path.lastIndexOf('/'); return i < 0 ? '' : path.slice(0, i); }
  function nameOf(path) { return path.slice(path.lastIndexOf('/') + 1); }
  function extOf(path) { var name = nameOf(path), i = name.lastIndexOf('.'); return i <= 0 ? '' : name.slice(i + 1).toLowerCase(); }
  function join(folder, name) { return folder ? folder + '/' + name : name; }
  function within(path, folder) { return !folder || path === folder || path.indexOf(folder + '/') === 0; }

  function icon(name) {
    var span = document.createElement('span');
    span.className = 'files-icon';
    span.innerHTML = icons[name] || '';
    return span;
  }

  function button(label, iconName, onClick, css) {
    var b = document.createElement('button');
    b.type = 'button';
    b.className = 'btn btn-sm' + (css ? ' ' + css : '');
    if (iconName) b.appendChild(icon(iconName));
    b.appendChild(document.createTextNode(label));
    b.addEventListener('click', onClick);
    return b;
  }

  // A button that shows only its icon; its name is its tooltip and what a screen reader says.
  function iconButton(label, iconName, onClick, css) {
    var b = document.createElement('button');
    b.type = 'button';
    b.className = 'btn btn-sm btn-icon' + (css ? ' ' + css : '');
    b.title = label;
    b.setAttribute('aria-label', label);
    b.appendChild(icon(iconName));
    b.addEventListener('click', onClick);
    return b;
  }

  function iconLink(label, iconName, href) {
    var a = document.createElement('a');
    a.className = 'btn btn-sm btn-icon';
    a.href = href;
    a.title = label;
    a.setAttribute('aria-label', label);
    a.appendChild(icon(iconName));
    return a;
  }

  function formatBytes(bytes) {
    var units = ['B', 'KB', 'MB', 'GB'], unit = 0;
    while (bytes >= 1024 && unit < units.length - 1) { bytes /= 1024; unit++; }
    return (unit === 0 ? bytes : bytes.toFixed(1)) + ' ' + units[unit];
  }

  function when(utc) {
    try {
      return new Date(utc).toLocaleString(document.documentElement.lang, { dateStyle: 'medium', timeStyle: 'short' });
    } catch (e) {
      return new Date(utc).toLocaleString();
    }
  }

  function remembered() {
    try { return JSON.parse(sessionStorage.getItem('ssh.files.' + setup.domain) || '{}') || {}; } catch (e) { return {}; }
  }

  function remember() {
    try { sessionStorage.setItem('ssh.files.' + setup.domain, JSON.stringify(expanded)); } catch (e) { }
  }

  function dirty(path) { var doc = docs[path]; return !!doc && current(doc) !== doc.saved; }
  function anyDirty() { return Object.keys(docs).some(dirty); }
  function current(doc) { return doc.model ? doc.model.getValue() : doc.text; }

  // The release open now, and whether it is the live one.
  function release() { return listing && listing.release; }
  function isLive() { return !!release() && release().live; }

  // Sentences follow one another with a space, except in Chinese and Japanese, whose full stop needs none.
  var GAP = /^(zh|ja)/i.test(document.documentElement.lang) ? '' : ' ';

  // ------------------------------------------------------------------ the server

  function url(action, values) {
    var q = [];
    for (var k in values || {}) if (values[k] != null) q.push(encodeURIComponent(k) + '=' + encodeURIComponent(values[k]));
    return setup.api + (action ? '/' + action : '') + (q.length ? '?' + q.join('&') : '');
  }

  // Every answer as { ok, status, data }. A session that ended answers 401.
  function request(method, address, body, type) {
    var headers = { 'X-CSRF-TOKEN': csrf };
    if (type) headers['Content-Type'] = type;
    return fetch(address, { method: method, headers: headers, body: body, credentials: 'same-origin' }).then(answer, function () {
      return { ok: false, status: 0, data: { error: siteHost.t('js.files.offline') } };
    });
  }

  function answer(res) {
    var json = (res.headers.get('Content-Type') || '').indexOf('json') >= 0;
    if (!json && (res.redirected || res.status === 401)) return { ok: false, status: 401, data: { error: siteHost.t('js.files.signedOut') } };
    return (json ? res.json() : Promise.resolve({})).then(function (data) {
      if (!res.ok && !data.error) data.error = siteHost.t('js.files.failed', { status: res.status });
      return { ok: res.ok, status: res.status, data: data };
    });
  }

  function post(action, payload) { return request('POST', url(action), JSON.stringify(payload || {}), 'application/json'); }

  function put(path, blob, create, releaseId) {
    return request('PUT', url('content', { release: releaseId || release().id, path: path, create: create ? 'true' : null }), blob, 'application/octet-stream');
  }

  // A file's bytes in the release open now, or in the live one.
  function bytes(path, live) {
    return fetch(url('content', { release: live ? null : release().id, path: path }), { credentials: 'same-origin' }).then(function (res) {
      if (res.status === 401 || res.redirected) throw new Error('signedOut');
      if (!res.ok) throw new Error(String(res.status));
      return res.arrayBuffer();
    });
  }

  // ------------------------------------------------------------------ messages

  var noticeTimer = null;

  // A success clears itself after a few seconds; what needs reading or acting on stays.
  function notify(kind, text, list, actions) {
    clearTimeout(noticeTimer);
    if (kind === 'success' && !actions) noticeTimer = setTimeout(function () { notify(null); }, 6000);
    notice.textContent = '';
    notice.className = 'files-notice' + (kind ? ' alert alert-' + kind : '');
    if (!kind) return;
    var p = document.createElement('p');
    p.textContent = text;
    notice.appendChild(p);
    if (list && list.length) {
      var ul = document.createElement('ul');
      list.forEach(function (item) { var li = document.createElement('li'); li.textContent = item; ul.appendChild(li); });
      notice.appendChild(ul);
    }
    if (actions && actions.length) {
      var row = document.createElement('div');
      row.className = 'files-notice-actions';
      actions.forEach(function (action) { row.appendChild(button(action.label, null, function () { notify(null); action.run(); })); });
      notice.appendChild(row);
    }
  }

  function refused(res) { notify('error', res.data.error, res.data.details); }

  // ------------------------------------------------------------------ the listing

  function refresh() {
    return request('GET', url('', { release: wanted })).then(function (res) {
      if (!res.ok) {
        // A release that is gone (pruned, or never there): the live one instead.
        if (res.status === 404 && wanted) { wanted = null; return refresh(); }
        notify('error', res.data.error);
        el.note.textContent = siteHost.t('js.files.loadFailed');
        return false;
      }
      var before = release() ? release().id : null;
      listing = res.data;
      files = {};
      listing.files.forEach(function (f) { files[f.path] = f; });

      Object.keys(docs).forEach(function (path) {
        var doc = docs[path];
        if (dirty(path)) {
          // Removed by someone else while edits here are not saved: kept, so they can be saved again.
          if (!files[path]) files[path] = { path: path, size: 0, configuration: false, gone: true };
          return;
        }
        // Opened from a release that is not the one open now, or since replaced: opened afresh next time.
        if (!files[path] || !release() || doc.release !== release().id) drop(path);
      });
      if (release() && before && before !== release().id) comparing = false;
      if (selected && !selected.folder && !files[selected.path]) selected = null;
      if (selected && selected.folder && !Object.keys(files).some(function (p) { return within(p, selected.path); })) selected = null;

      bar();
      renderTree();
      return true;
    });
  }

  // The release menu, Duplicate and Make live, and what saving here does.
  function bar() {
    el.release.textContent = '';
    (listing.releases || []).forEach(function (r) {
      var option = document.createElement('option');
      option.value = r.id;
      option.textContent = r.live ? siteHost.t('js.files.option.live', { id: r.id })
        : r.copiedFrom ? siteHost.t('js.files.option.copy', { id: r.id, from: r.copiedFrom }) : r.id;
      option.selected = !!release() && r.id === release().id;
      el.release.appendChild(option);
    });
    el.release.disabled = !release();
    el.duplicate.disabled = !release();

    // The number beside the Releases tab follows copies made here.
    var count = document.querySelector('.tabs a[data-tab="releases"] .count');
    if (count) count.textContent = (listing.releases || []).length;
    el.makeLive.hidden = !release() || isLive();

    var text;
    if (!release()) text = siteHost.t('js.files.noRelease');
    else if (isLive()) text = siteHost.t('js.files.note.live', { id: release().id });
    else text = siteHost.t('js.files.note.notLive', { id: release().id });
    if (release() && release().editedUtc) text += GAP + siteHost.t('js.files.note.edited', { name: release().editedBy || '—', time: when(release().editedUtc) });
    var unsaved = Object.keys(docs).filter(dirty).length;
    if (unsaved > 0) text += GAP + siteHost.tp('js.files.unsavedCount', unsaved);
    el.note.textContent = text;
    el.note.className = 'files-note' + (isLive() ? ' files-note-live' : '');
  }

  // Picking another release: unsaved edits would be lost, so it asks first.
  el.release.addEventListener('change', function () {
    var id = el.release.value;
    var chosen = (listing.releases || []).filter(function (r) { return r.id === id; })[0];
    if (anyDirty() && !window.confirm(siteHost.t('js.files.switchConfirm', { id: id }))) {
      el.release.value = release().id;
      return;
    }
    openRelease(chosen && chosen.live ? null : id);
  });

  // Opens a release in the tab: null for the live one.
  function openRelease(id) {
    Object.keys(docs).forEach(drop);
    wanted = id;
    selected = null;
    comparing = false;
    notify(null);
    try {
      var address = new URL(location.href);
      address.searchParams.delete('file');
      if (id) address.searchParams.set('release', id); else address.searchParams.delete('release');
      history.replaceState(history.state, '', address);
    } catch (e) { }
    return refresh().then(show);
  }

  el.duplicate.addEventListener('click', function () {
    if (!release()) return;
    el.duplicate.disabled = true;
    post('duplicate', { release: release().id }).then(function (res) {
      el.duplicate.disabled = false;
      if (!res.ok) { refused(res); return; }
      stale = true;
      var copy = res.data.release;
      refresh().then(function () {
        notify('success', res.data.message, null, [{ label: siteHost.t('js.files.openCopy'), run: function () { openRelease(copy); } }]);
      });
    });
  });

  el.makeLive.addEventListener('click', function () {
    if (!release() || isLive()) return;
    if (!window.confirm(siteHost.t('js.files.makeLiveConfirm', { id: release().id, domain: setup.domain }))) return;
    var id = release().id;
    post('make-live', { release: id }).then(function (res) {
      if (!res.ok) { refused(res); return; }
      stale = true;
      openRelease(null).then(function () { notify('success', res.data.message); });
    });
  });

  // ------------------------------------------------------------------ the tree

  function buildTree() {
    var top = { path: '', folders: {}, files: [], unsaved: 0 };
    Object.keys(files).sort().forEach(function (path) {
      var parts = path.split('/'), node = top, at = '';
      for (var i = 0; i < parts.length - 1; i++) {
        at = join(at, parts[i]);
        node = node.folders[parts[i]] || (node.folders[parts[i]] = { path: at, folders: {}, files: [], unsaved: 0 });
      }
      node.files.push(files[path]);
    });
    (function count(node) {
      var n = node.files.filter(function (f) { return dirty(f.path); }).length;
      Object.keys(node.folders).forEach(function (k) { n += count(node.folders[k]); });
      node.unsaved = n;
      return n;
    })(top);
    return top;
  }

  function dot(title, css) {
    var span = document.createElement('span');
    span.className = 'files-dot ' + css;
    span.title = title;
    var label = document.createElement('span');
    label.className = 'sr-only';
    label.textContent = title;
    span.appendChild(label);
    return span;
  }

  function item(options) {
    var li = document.createElement('li');
    li.setAttribute('role', 'none');
    var row = document.createElement('button');
    row.type = 'button';
    row.className = 'files-row';
    row.setAttribute('role', 'treeitem');
    row.setAttribute('data-path', options.path);
    if (options.folder) row.setAttribute('data-folder', '');
    row.style.setProperty('--depth', options.depth);
    row.setAttribute('aria-level', options.depth + 1);
    var isSelected = !!selected && selected.path === options.path && !!selected.folder === !!options.folder;
    row.setAttribute('aria-selected', isSelected ? 'true' : 'false');
    row.tabIndex = -1;

    var chevron = document.createElement('span');
    chevron.className = 'files-chevron';
    if (options.folder) {
      chevron.innerHTML = icons['chevron-right'];
      row.setAttribute('aria-expanded', options.open ? 'true' : 'false');
    }
    row.appendChild(chevron);
    row.appendChild(icon(options.folder ? 'folder' : IMAGES[extOf(options.path)] ? 'image' : 'file'));

    var name = document.createElement('span');
    name.className = 'files-name';
    name.textContent = options.label;
    row.appendChild(name);
    if (options.unsaved) row.appendChild(dot(siteHost.t('js.files.state.unsaved'), options.folder ? 'files-dot-folder' : 'files-dot-unsaved'));

    row.addEventListener('click', function () {
      if (options.folder) {
        var opening = !(selected && selected.folder && selected.path === options.path) || !expanded[options.path];
        expanded[options.path] = opening;
        remember();
      }
      choose({ folder: !!options.folder, path: options.path });
    });
    li.appendChild(row);
    return li;
  }

  function renderNode(node, list, depth) {
    Object.keys(node.folders).sort().forEach(function (name) {
      var folder = node.folders[name];
      var isOpen = !!expanded[folder.path];
      var li = item({ folder: true, path: folder.path, label: name, depth: depth, open: isOpen, unsaved: folder.unsaved > 0 });
      if (isOpen) {
        var group = document.createElement('ul');
        group.setAttribute('role', 'group');
        renderNode(folder, group, depth + 1);
        li.appendChild(group);
      }
      list.appendChild(li);
    });
    node.files.forEach(function (file) {
      list.appendChild(item({ path: file.path, label: nameOf(file.path), depth: depth, unsaved: dirty(file.path) }));
    });
  }

  function renderTree() {
    var focused = document.activeElement && el.tree.contains(document.activeElement) ? document.activeElement.getAttribute('data-path') : null;
    el.tree.textContent = '';
    var words = el.filter.value.trim().toLowerCase();

    if (Object.keys(files).length === 0) {
      var empty = document.createElement('li');
      empty.className = 'files-tree-note';
      empty.textContent = siteHost.t(release() ? 'js.files.noFiles' : 'js.files.noRelease');
      el.tree.appendChild(empty);
    } else if (words) {
      // Filtered: a flat list of the paths that match, so a deep file is one step away.
      var matches = Object.keys(files).sort().filter(function (p) { return p.toLowerCase().indexOf(words) >= 0; });
      matches.slice(0, FILTER_LIMIT).forEach(function (path) {
        el.tree.appendChild(item({ path: path, label: path, depth: 0, unsaved: dirty(path) }));
      });
      if (matches.length === 0 || matches.length > FILTER_LIMIT) {
        var note = document.createElement('li');
        note.className = 'files-tree-note';
        note.textContent = matches.length === 0 ? siteHost.t('js.files.noMatch') : siteHost.tp('js.files.moreMatches', matches.length - FILTER_LIMIT);
        el.tree.appendChild(note);
      }
    } else {
      renderNode(buildTree(), el.tree, 0);
    }

    // One row takes Tab: the selected one, or the first.
    var rows = el.tree.querySelectorAll('.files-row');
    var active = el.tree.querySelector('.files-row[aria-selected="true"]') || rows[0];
    if (active) active.tabIndex = 0;
    if (focused) {
      var again = el.tree.querySelector('.files-row[data-path="' + CSS.escape(focused) + '"]');
      if (again) again.focus();
    }
  }

  // Up and down move along the rows; left and right close and open folders.
  el.tree.addEventListener('keydown', function (event) {
    var row = event.target.closest('.files-row');
    if (!row) return;
    var rows = Array.prototype.slice.call(el.tree.querySelectorAll('.files-row'));
    var index = rows.indexOf(row), next = null;
    if (event.key === 'ArrowDown') next = rows[index + 1];
    else if (event.key === 'ArrowUp') next = rows[index - 1];
    else if (event.key === 'Home') next = rows[0];
    else if (event.key === 'End') next = rows[rows.length - 1];
    else if ((event.key === 'ArrowRight' || event.key === 'ArrowLeft') && row.hasAttribute('data-folder')) {
      var path = row.getAttribute('data-path');
      var opening = event.key === 'ArrowRight';
      if (!!expanded[path] !== opening) { expanded[path] = opening; remember(); renderTree(); }
      event.preventDefault();
      return;
    }
    if (!next) return;
    event.preventDefault();
    rows.forEach(function (r) { r.tabIndex = -1; });
    next.tabIndex = 0;
    next.focus();
  });

  el.filter.addEventListener('input', renderTree);

  // ------------------------------------------------------------------ choosing what to show

  function choose(selection) {
    // Folders above a chosen file open, so it shows in the tree.
    if (!selection.folder) {
      for (var folder = folderOf(selection.path); folder; folder = folderOf(folder)) expanded[folder] = true;
      remember();
    }
    selected = selection;
    comparing = false;
    try {
      var address = new URL(location.href);
      if (selection.folder) address.searchParams.delete('file'); else address.searchParams.set('file', selection.path);
      history.replaceState(history.state, '', address);
    } catch (e) { }
    renderTree();
    return show();
  }

  function show() {
    el.view.textContent = '';
    el.head.textContent = '';
    el.head.hidden = !selected;
    if (!selected) {
      var p = document.createElement('p');
      p.className = 'files-empty';
      p.textContent = siteHost.t(Object.keys(files).length ? 'js.files.choose' : release() ? 'js.files.noFiles' : 'js.files.noRelease');
      el.view.appendChild(p);
      return Promise.resolve();
    }
    return selected.folder ? showFolder(selected.path) : showFile(files[selected.path]);
  }

  function heading(path) {
    var title = document.createElement('div');
    title.className = 'files-title';
    var name = document.createElement('code');
    name.textContent = path || siteHost.t('js.files.root');
    title.appendChild(name);
    el.head.appendChild(title);
    var actions = document.createElement('div');
    actions.className = 'files-head-actions';
    el.head.appendChild(actions);
    return actions;
  }

  function showFolder(path) {
    var actions = heading(path);
    actions.appendChild(button(siteHost.t('js.files.newHere'), 'file-plus', function () { askNewFile(path); }));
    actions.appendChild(button(siteHost.t('js.files.uploadHere'), 'upload', function () { pickUploads(path); }));
    actions.appendChild(iconButton(siteHost.t('js.files.rename'), 'rename', function () { askRename(path, true); }));
    actions.appendChild(iconButton(siteHost.t('js.files.delete'), 'trash', function () { remove(path, true); }, 'btn-danger'));

    var inside = Object.keys(files).filter(function (p) { return within(p, path); });
    var p = document.createElement('p');
    p.className = 'files-empty';
    p.textContent = siteHost.tp('js.files.folderSummary', inside.length);
    el.view.appendChild(p);
    return Promise.resolve();
  }

  function showFile(file) {
    var path = file.path;
    var actions = heading(path);
    var ext = extOf(path);
    var config = CONFIG[path];

    var asText = !!docs[path] || (!IMAGES[ext] && file.size <= setup.maxEditBytes);
    if (asText) {
      var save = button(siteHost.t('js.files.save'), 'save', function () { saveDoc(path); }, 'btn-primary');
      save.setAttribute('data-files-save', '');
      save.disabled = !dirty(path);
      actions.appendChild(save);
      if (monacoState !== 'failed') {
        // In the live release, against what is saved; in another, against the live file.
        var label = comparing ? 'js.files.closeCompare' : isLive() ? 'js.files.compareSaved' : 'js.files.compare';
        var compare = button(siteHost.t(label), 'compare', toggleCompare);
        compare.setAttribute('data-files-compare', '');
        compare.hidden = !comparing && isLive() && !dirty(path);
        actions.appendChild(compare);
      }
      var revert = button(siteHost.t('js.files.revert'), 'undo', function () { revertDoc(path); });
      revert.setAttribute('data-files-revert', '');
      revert.hidden = !dirty(path);
      actions.appendChild(revert);
    }
    if (ext === 'svg' && docs[path] && !dirty(path)) {
      actions.appendChild(button(siteHost.t('js.files.showPicture'), 'image', function () { drop(path); show(); }));
    }

    // The ones used less often show as icons, so the row stays one line.
    var more = document.createElement('span');
    more.className = 'files-head-more';
    more.appendChild(iconButton(siteHost.t('js.files.rename'), 'rename', function () { askRename(path, false); }));
    more.appendChild(iconButton(siteHost.t('js.files.replace'), 'upload', function () { pickReplacement(path); }));
    var download = iconLink(siteHost.t('js.files.download'), 'download', url('content', { release: release().id, path: path, download: 'true' }));
    download.setAttribute('download', nameOf(path));
    more.appendChild(download);
    if (!config && isLive() && !file.gone) {
      var live = iconLink(siteHost.t('js.files.openLive'), 'external', setup.siteUrl.replace(/\/$/, '') + '/' + path.split('/').map(encodeURIComponent).join('/'));
      live.target = '_blank';
      live.rel = 'noopener';
      more.appendChild(live);
    }
    more.appendChild(iconButton(siteHost.t('js.files.delete'), 'trash', function () { remove(path, false); }, 'btn-danger'));
    actions.appendChild(more);

    if (file.gone) note('warning', siteHost.t('js.files.goneElsewhere', { path: path }));
    if (config) note('info', siteHost.t(config));

    if (IMAGES[ext] && !(docs[path] && ext === 'svg')) return showImage(file);
    if (!docs[path] && file.size > setup.maxEditBytes) {
      note('info', siteHost.t('js.files.tooLargeToEdit', { size: formatBytes(setup.maxEditBytes) }));
      return Promise.resolve();
    }
    return openDoc(path).then(function (doc) {
      if (!selected || selected.path !== path) return;
      if (!doc) {
        ['[data-files-save]', '[data-files-compare]', '[data-files-revert]'].forEach(function (s) {
          var b = el.head.querySelector(s);
          if (b) b.remove();
        });
        note('info', siteHost.t('js.files.binary'));
        return;
      }
      showEditor(path);
    });
  }

  function note(kind, text, list) {
    var box = document.createElement('div');
    box.className = 'alert alert-' + (kind === 'info' ? 'info files-note-box' : kind);
    var p = document.createElement('p');
    p.textContent = text;
    box.appendChild(p);
    if (list && list.length) {
      var ul = document.createElement('ul');
      list.forEach(function (line) { var li = document.createElement('li'); li.textContent = line; ul.appendChild(li); });
      box.appendChild(ul);
    }
    el.view.appendChild(box);
  }

  // ------------------------------------------------------------------ images

  function showImage(file) {
    var path = file.path;
    var figure = document.createElement('figure');
    figure.className = 'files-image';
    var img = document.createElement('img');
    img.alt = '';
    var caption = document.createElement('figcaption');
    caption.className = 'hint';
    caption.textContent = formatBytes(file.size);
    figure.appendChild(img);
    figure.appendChild(caption);
    el.view.appendChild(figure);

    if (extOf(path) === 'svg') {
      var edit = button(siteHost.t('js.files.editText'), 'rename', function () { openDoc(path).then(function (doc) { if (doc) show(); }); });
      el.view.insertBefore(edit, figure);
    }

    // From the server, which sends it under a sandbox policy: an SVG's script never runs, not even
    // when the picture is opened in a tab of its own.
    img.onload = function () {
      caption.textContent = siteHost.t('js.files.imageSize', { width: img.naturalWidth, height: img.naturalHeight, size: formatBytes(file.size) });
    };
    img.onerror = function () { note('error', siteHost.t('js.files.readFailed')); };
    img.src = url('content', { release: release().id, path: path, preview: 'true', v: file.size + '-' + (release().editedUtc || '') });
    return Promise.resolve();
  }

  // ------------------------------------------------------------------ text, and the editor

  // Text is UTF-8: a file that is not, or that holds a NUL byte, is shown as not text.
  function decode(buffer) {
    var view = new Uint8Array(buffer);
    for (var i = 0; i < Math.min(view.length, 8000); i++) if (view[i] === 0) return null;
    try {
      var bom = view.length >= 3 && view[0] === 0xef && view[1] === 0xbb && view[2] === 0xbf;
      return { text: new TextDecoder('utf-8', { fatal: true }).decode(view), bom: bom };
    } catch (e) {
      return null;
    }
  }

  function openDoc(path) {
    if (docs[path]) return Promise.resolve(docs[path]);
    var from = release().id;
    return bytes(path, false).then(function (buffer) {
      var decoded = decode(buffer);
      if (!decoded) return null;
      if (docs[path]) return docs[path];
      var doc = { text: decoded.text, saved: decoded.text, bom: decoded.bom, model: null, release: from };
      docs[path] = doc;
      if (monaco) attachModel(path, doc);
      return doc;
    }, function (error) {
      note('error', siteHost.t(error && error.message === 'signedOut' ? 'js.files.signedOut' : 'js.files.readFailed'));
      return null;
    });
  }

  function closeDiff() {
    if (diffEditor) { diffEditor.dispose(); diffEditor = null; }
    if (originalModel) { originalModel.dispose(); originalModel = null; }
  }

  function drop(path) {
    var doc = docs[path];
    if (doc && doc.model) {
      if (diffEditor) closeDiff();
      if (editor && editor.getModel() === doc.model) editor.setModel(null);
      doc.model.dispose();
    }
    delete docs[path];
  }

  function languageOf(path) {
    var name = nameOf(path).toLowerCase(), ext = '.' + extOf(path);
    var languages = monaco.languages.getLanguages();
    for (var i = 0; i < languages.length; i++) {
      if ((languages[i].filenames || []).some(function (f) { return f.toLowerCase() === name; })) return languages[i].id;
    }
    for (var j = 0; j < languages.length; j++) {
      if ((languages[j].extensions || []).some(function (e) { return e.toLowerCase() === ext; })) return languages[j].id;
    }
    return 'plaintext';
  }

  function attachModel(path, doc) {
    var uri = monaco.Uri.from({ scheme: 'site', path: '/' + path });
    var existing = monaco.editor.getModel(uri);
    if (existing) existing.dispose();
    doc.model = monaco.editor.createModel(doc.text, languageOf(path), uri);
    doc.model.onDidChangeContent(changedText);
  }

  var changeTimer = null;
  function changedText() {
    // The tree's marks, the note and the buttons follow the typing, a moment behind it.
    clearTimeout(changeTimer);
    changeTimer = setTimeout(function () { bar(); renderTree(); updateButtons(); }, 250);
  }

  function updateButtons() {
    if (!selected || selected.folder) return;
    var isDirty = dirty(selected.path);
    var save = el.head.querySelector('[data-files-save]');
    if (save && !save.classList.contains('files-saving')) save.disabled = !isDirty;
    var revert = el.head.querySelector('[data-files-revert]');
    if (revert) revert.hidden = !isDirty;
    var compare = el.head.querySelector('[data-files-compare]');
    if (compare && isLive() && !comparing) compare.hidden = !isDirty;
  }

  function dark() {
    var theme = document.documentElement.getAttribute('data-theme');
    return theme === 'dark' || (theme !== 'light' && window.matchMedia('(prefers-color-scheme: dark)').matches);
  }

  function themeChanged() { if (monaco) monaco.editor.setTheme(dark() ? 'vs-dark' : 'vs'); }
  new MutationObserver(themeChanged).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', themeChanged);

  function loadMonaco() {
    if (monacoState !== 'idle') return;
    monacoState = 'loading';
    var settled = false;
    function fail() {
      if (settled) return;
      settled = true;
      monacoState = 'failed';
      if (selected && !selected.folder && docs[selected.path]) show();
    }
    var timer = setTimeout(fail, 10000);

    var script = document.createElement('script');
    script.src = MONACO + '/vs/loader.js';
    script.onerror = function () { clearTimeout(timer); fail(); };
    script.onload = function () {
      if (typeof window.require !== 'function' || !window.require.config) { clearTimeout(timer); fail(); return; }
      window.MonacoEnvironment = {
        getWorkerUrl: function () {
          // Workers must come from this origin; this one pulls the real worker from the CDN.
          return 'data:text/javascript;charset=utf-8,' + encodeURIComponent(
            "self.MonacoEnvironment={baseUrl:'" + MONACO + "/'};importScripts('" + MONACO + "/vs/base/worker/workerMain.js');");
        }
      };
      window.require.config({ paths: { vs: MONACO + '/vs' } });
      window.require(['vs/editor/editor.main'], function () {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        monaco = window.monaco;
        monacoState = 'ready';
        Object.keys(docs).forEach(function (path) {
          var doc = docs[path];
          if (textarea && selected && selected.path === path) doc.text = textarea.value;
          attachModel(path, doc);
        });
        if (selected && !selected.folder && docs[selected.path]) show();
      }, function () { clearTimeout(timer); fail(); });
    };
    document.head.appendChild(script);
  }

  function box() {
    if (!editorBox) {
      editorBox = document.createElement('div');
      editorBox.className = 'files-editor';
    }
    return editorBox;
  }

  function showEditor(path) {
    var doc = docs[path];
    if (!doc || !selected || selected.path !== path) return;
    var container = box();
    if (!container.isConnected) el.view.appendChild(container);

    if (monacoState === 'ready') {
      if (textarea) { textarea.remove(); textarea = null; }
      if (comparing) return showDiff(path);
      if (diffEditor) { closeDiff(); container.textContent = ''; editor = null; }
      if (!editor) {
        container.textContent = '';
        editor = monaco.editor.create(container, {
          model: doc.model,
          automaticLayout: true,
          minimap: { enabled: false },
          fontSize: 13,
          scrollBeyondLastLine: false,
          theme: dark() ? 'vs-dark' : 'vs'
        });
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, function () { if (selected && !selected.folder) saveDoc(selected.path); });
      } else if (editor.getModel() !== doc.model) {
        editor.setModel(doc.model);
      }
      return;
    }

    if (monacoState === 'loading' || monacoState === 'idle') {
      if (!container.querySelector('.files-loading')) {
        container.textContent = '';
        var wait = document.createElement('p');
        wait.className = 'files-loading hint';
        wait.textContent = siteHost.t('js.files.editorLoading');
        container.appendChild(wait);
      }
      return;
    }

    // Without the CDN (offline, or blocked) editing still works, just without highlighting.
    if (!textarea) {
      container.textContent = '';
      container.classList.add('files-plain');
      textarea = document.createElement('textarea');
      textarea.className = 'files-textarea';
      textarea.spellcheck = false;
      textarea.wrap = 'off';
      textarea.addEventListener('input', function () {
        var openPath = textarea.getAttribute('data-path');
        if (docs[openPath]) docs[openPath].text = textarea.value;
        changedText();
      });
      container.appendChild(textarea);
    }
    var hint = document.createElement('p');
    hint.className = 'hint';
    hint.textContent = siteHost.t('js.files.editorOffline');
    el.view.appendChild(hint);
    textarea.setAttribute('data-path', path);
    textarea.value = doc.text;
  }

  function toggleCompare() {
    comparing = !comparing;
    show();
  }

  // Beside what is saved (in the live release) or beside the live file (in another release).
  function showDiff(path) {
    var doc = docs[path];
    var container = box();
    var againstSaved = isLive();
    var original = againstSaved ? Promise.resolve(doc.saved) : bytes(path, true).then(function (buffer) {
      var decoded = decode(buffer);
      return decoded ? decoded.text : null;
    }, function () { return ''; });

    original.then(function (text) {
      if (text == null || !selected || selected.path !== path || !comparing) return;
      if (editor) { editor.dispose(); editor = null; }
      closeDiff();
      container.textContent = '';
      originalModel = monaco.editor.createModel(text, doc.model.getLanguageId());
      diffEditor = monaco.editor.createDiffEditor(container, {
        automaticLayout: true,
        minimap: { enabled: false },
        fontSize: 13,
        // Side by side where there is room, one column on a narrow screen.
        renderSideBySide: true,
        renderSideBySideInlineBreakpoint: 600,
        originalEditable: false,
        theme: dark() ? 'vs-dark' : 'vs'
      });
      diffEditor.setModel({ original: originalModel, modified: doc.model });
      // Opened at the first difference, once it has been worked out.
      var once = diffEditor.onDidUpdateDiff(function () {
        once.dispose();
        if (typeof diffEditor.revealFirstDiff === 'function') diffEditor.revealFirstDiff();
        else if (typeof diffEditor.goToDiff === 'function') diffEditor.goToDiff('next');
      });
      var legend = document.createElement('p');
      legend.className = 'hint';
      legend.textContent = siteHost.t(againstSaved ? 'js.files.compareSavedLegend' : 'js.files.compareLegend');
      el.view.insertBefore(legend, container);
    });
  }

  // ------------------------------------------------------------------ changes

  function saveDoc(path) {
    var doc = docs[path];
    if (!doc) return Promise.resolve(true);
    if (doc.saving) return doc.saving;
    if (!dirty(path)) return Promise.resolve(true);
    var text = current(doc);
    var saveButton = el.head.querySelector('[data-files-save]');
    if (saveButton) { saveButton.disabled = true; saveButton.classList.add('files-saving'); }
    doc.saving = put(path, new Blob([doc.bom ? '\ufeff' + text : text]), false, doc.release).then(function (res) {
      doc.saving = null;
      if (saveButton) saveButton.classList.remove('files-saving');
      if (!res.ok) { updateButtons(); refused(res); return false; }
      // Only what was sent counts as saved: typing since then stays unsaved.
      doc.saved = text;
      var message = res.data.live ? 'js.files.savedLive' : release() && res.data.release === release().id ? 'js.files.saved' : 'js.files.savedElsewhere';
      notify(res.data.warnings && res.data.warnings.length ? 'warning' : 'success', siteHost.t(message, { path: path, id: res.data.release }), res.data.warnings);
      if (res.data.live) stale = true;
      return refresh().then(function () {
        if (selected && selected.path === path) refreshHead();
        return true;
      });
    });
    return doc.saving;
  }

  // After a change, the buttons are drawn again; the editor keeps its place and focus.
  function refreshHead() {
    var focused = editor && editor.hasTextFocus();
    return show().then(function () { if (focused && editor) editor.focus(); });
  }

  function saveAll() {
    return Object.keys(docs).filter(dirty).reduce(function (chain, path) {
      return chain.then(function (ok) { return ok ? saveDoc(path) : false; });
    }, Promise.resolve(true));
  }

  // Back to what is saved: only edits not saved yet go.
  function revertDoc(path) {
    var doc = docs[path];
    if (!doc || !dirty(path) || !window.confirm(siteHost.t('js.files.revertConfirm', { path: path }))) return;
    if (doc.model) doc.model.setValue(doc.saved); else doc.text = doc.saved;
    if (files[path] && files[path].gone) { drop(path); delete files[path]; selected = null; }
    comparing = false;
    bar();
    renderTree();
    show();
  }

  function remove(path, folder) {
    var count = Object.keys(files).filter(function (p) { return within(p, path); }).length;
    var message = folder
      ? siteHost.tp(isLive() ? 'js.files.deleteFolderLiveConfirm' : 'js.files.deleteFolderConfirm', count, { path: path, id: release().id })
      : siteHost.t(isLive() ? 'js.files.deleteLiveConfirm' : 'js.files.deleteConfirm', { path: path, id: release().id });
    if (!window.confirm(message)) return;
    post('delete', { release: release().id, path: path }).then(function (res) {
      if (!res.ok) { refused(res); return; }
      if (res.data.live) stale = true;
      Object.keys(docs).forEach(function (p) { if (within(p, path)) drop(p); });
      notify('success', siteHost.t('js.files.deleted', { path: path }));
      selected = null;
      refresh().then(show);
    });
  }

  // A small form in the head: a path, and a button to act on it.
  function askPath(label, value, action, onSubmit) {
    el.head.hidden = false;
    el.head.textContent = '';
    var form = document.createElement('form');
    form.className = 'files-form';
    var id = 'files-path-' + Date.now();
    var caption = document.createElement('label');
    caption.htmlFor = id;
    caption.textContent = label;
    var input = document.createElement('input');
    input.id = id;
    input.type = 'text';
    input.value = value;
    input.spellcheck = false;
    input.autocomplete = 'off';
    input.required = true;
    var submit = document.createElement('button');
    submit.type = 'submit';
    submit.className = 'btn-primary btn-sm';
    submit.textContent = action;
    var cancel = button(siteHost.t('js.files.cancel'), null, function () { show(); });
    form.appendChild(caption);
    form.appendChild(input);
    form.appendChild(submit);
    form.appendChild(cancel);
    form.addEventListener('submit', function (event) {
      event.preventDefault();
      var path = input.value.trim().replace(/^\/+/, '');
      if (!path) return;
      submit.disabled = true;
      onSubmit(path).then(function (ok) { if (!ok) { submit.disabled = false; input.focus(); } });
    });
    el.head.appendChild(form);
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
  }

  function askNewFile(folder) {
    askPath(siteHost.t('js.files.newPath'), folder ? folder + '/' : '', siteHost.t('js.files.create'), function (path) {
      return put(path, new Blob([]), true).then(function (res) {
        if (!res.ok) { refused(res); return false; }
        if (res.data.live) stale = true;
        var created = res.data.path || path;
        notify('success', siteHost.t('js.files.created', { path: created }));
        // A site with no release has one now: the tab follows it.
        return refresh().then(function () {
          if (files[created]) choose({ folder: false, path: created });
          return true;
        });
      });
    });
  }

  function askRename(path, folder) {
    askPath(siteHost.t(folder ? 'js.files.renameFolderPath' : 'js.files.renamePath', { path: path }), path, siteHost.t('js.files.rename'), function (to) {
      var save = folder ? saveAll() : saveDoc(path);
      return save.then(function (saved) {
        if (!saved) return false;
        return post('rename', { release: release().id, path: path, to: to }).then(function (res) {
          if (!res.ok) { refused(res); return false; }
          if (res.data.live) stale = true;
          to = res.data.path || to;
          if (diffEditor) { closeDiff(); comparing = false; }
          // Open documents move with their files.
          Object.keys(docs).forEach(function (p) {
            if (!within(p, path)) return;
            var target = folder ? to + p.slice(path.length) : to;
            var doc = docs[p];
            if (doc.model) {
              doc.text = doc.model.getValue();
              if (editor && editor.getModel() === doc.model) editor.setModel(null);
              doc.model.dispose();
              doc.model = null;
            }
            delete docs[p];
            drop(target);
            docs[target] = doc;
            if (monaco) attachModel(target, doc);
          });
          if (folder) {
            Object.keys(expanded).forEach(function (p) { if (within(p, path)) expanded[to + p.slice(path.length)] = expanded[p]; });
            remember();
          }
          notify('success', siteHost.t('js.files.renamed', { from: path, to: to }));
          return refresh().then(function () {
            choose({ folder: folder, path: to });
            return true;
          });
        });
      });
    });
  }

  // ------------------------------------------------------------------ uploads

  function pickUploads(folder) {
    replacing = null;
    el.uploadInput.setAttribute('data-folder', folder || '');
    el.uploadInput.multiple = true;
    el.uploadInput.click();
  }

  function pickReplacement(path) {
    replacing = path;
    el.uploadInput.multiple = false;
    el.uploadInput.click();
  }

  el.uploadInput.addEventListener('change', function () {
    var chosen = Array.prototype.slice.call(el.uploadInput.files || []);
    el.uploadInput.value = '';
    if (!chosen.length) return;
    if (replacing) {
      upload([{ file: chosen[0], path: replacing }], false);
    } else {
      var folder = el.uploadInput.getAttribute('data-folder') || '';
      upload(chosen.map(function (file) { return { file: file, path: join(folder, file.name) }; }), true);
    }
  });

  el.upload.addEventListener('click', function () { pickUploads(selectedFolder()); });
  el.newFile.addEventListener('click', function () { askNewFile(selectedFolder()); });

  function selectedFolder() {
    if (!selected) return '';
    return selected.folder ? selected.path : folderOf(selected.path);
  }

  function upload(items, ask) {
    var replaced = items.filter(function (i) { return files[i.path]; }).map(function (i) { return i.path; });
    if (ask && replaced.length && !window.confirm(siteHost.tp('js.files.replaceConfirm', replaced.length, { paths: replaced.slice(0, 10).join(', ') }))) return Promise.resolve();

    var failed = [], done = 0, target = release() ? release().id : null;
    return items.reduce(function (chain, entry) {
      return chain.then(function () {
        notify('info', siteHost.t('js.files.uploading', { name: entry.path, done: done, count: items.length }));
        return put(entry.path, entry.file, false, target).then(function (res) {
          if (res.ok) {
            done++;
            drop(entry.path);
            if (res.data.path) entry.path = res.data.path;
            if (res.data.live) stale = true;
            // A site with no release got its first with this file: the rest go into it too.
            target = res.data.release;
          } else {
            failed.push(siteHost.t('js.files.uploadFailed', { name: entry.path, error: res.data.error }));
          }
        });
      });
    }, Promise.resolve()).then(function () {
      if (failed.length) notify('error', (done ? siteHost.tp('js.files.uploaded', done) + GAP : '') + siteHost.tp('js.files.notUploaded', failed.length), failed);
      else notify('success', siteHost.tp('js.files.uploaded', done));
      // The folders they went into open, so they show.
      items.forEach(function (entry) {
        for (var folder = folderOf(entry.path); folder; folder = folderOf(folder)) expanded[folder] = true;
      });
      remember();
      return refresh().then(function () {
        if (items.length === 1 && files[items[0].path]) choose({ folder: false, path: items[0].path });
        else show();
      });
    });
  }

  // Files and folders dropped on the tree go into the folder they were dropped on, or the root.
  el.side.addEventListener('dragover', function (event) {
    if (!event.dataTransfer || Array.prototype.indexOf.call(event.dataTransfer.types, 'Files') < 0) return;
    event.preventDefault();
    el.side.classList.add('files-drop');
  });
  el.side.addEventListener('dragleave', function (event) {
    if (!el.side.contains(event.relatedTarget)) el.side.classList.remove('files-drop');
  });
  el.side.addEventListener('drop', function (event) {
    event.preventDefault();
    el.side.classList.remove('files-drop');
    var row = event.target.closest('.files-row');
    var folder = row ? (row.hasAttribute('data-folder') ? row.getAttribute('data-path') : folderOf(row.getAttribute('data-path'))) : '';
    var entries = Array.prototype.slice.call(event.dataTransfer.items || [])
      .map(function (i) { return i.webkitGetAsEntry ? i.webkitGetAsEntry() : null; })
      .filter(Boolean);
    if (!entries.length) {
      upload(Array.prototype.slice.call(event.dataTransfer.files).map(function (file) { return { file: file, path: join(folder, file.name) }; }), true);
      return;
    }
    Promise.all(entries.map(function (entry) { return walk(entry, folder); })).then(function (lists) {
      upload([].concat.apply([], lists), true);
    });
  });

  function walk(entry, folder) {
    var path = join(folder, entry.name);
    if (entry.isFile) {
      return new Promise(function (resolve) { entry.file(function (file) { resolve([{ file: file, path: path }]); }, function () { resolve([]); }); });
    }
    var reader = entry.createReader(), all = [];
    return new Promise(function (resolve) {
      (function more() {
        reader.readEntries(function (batch) {
          if (!batch.length) {
            Promise.all(all.map(function (child) { return walk(child, path); })).then(function (lists) { resolve([].concat.apply([], lists)); });
            return;
          }
          all = all.concat(Array.prototype.slice.call(batch));
          more();
        }, function () { resolve([]); });
      })();
    });
  }

  // ------------------------------------------------------------------ the rest of the page

  document.addEventListener('click', function (event) {
    if (!stale) return;
    var tab = event.target.closest && event.target.closest('.tabs a[data-tab]');
    // The other tabs show the site as the page loaded it: the link loads them afresh instead.
    if (tab && tab.getAttribute('data-tab') !== 'files') event.stopPropagation();
  }, true);

  document.addEventListener('keydown', function (event) {
    if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 's' || !panel || panel.hidden) return;
    if (!selected || selected.folder || !docs[selected.path]) return;
    event.preventDefault();
    saveDoc(selected.path);
  });

  window.addEventListener('beforeunload', function (event) {
    if (!anyDirty()) return;
    event.preventDefault();
    event.returnValue = '';
  });

  // ------------------------------------------------------------------ start

  // Only once the tab shows: other tabs never load the editor or the file list.
  function start() {
    if (started || !panel || panel.hidden) return;
    started = true;
    loadMonaco();
    refresh().then(function (ok) {
      if (!ok) return;
      var file = query.get('file');
      if (file && files[file]) choose({ folder: false, path: file });
      else show();
    });
  }

  if (panel) new MutationObserver(start).observe(panel, { attributes: true, attributeFilter: ['hidden'] });
  start();
})();
