(function () {
  'use strict';

  var MONACO = 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min';
  var INLINE_LIMIT = 10 * 1024 * 1024;

  var data = JSON.parse(document.getElementById('fe-data').textContent);
  var csrf = document.getElementById('fe-csrf').value;

  var el = {
    tabs: document.getElementById('fe-tabs'),
    editor: document.getElementById('fe-editor'),
    check: document.getElementById('fe-check'),
    deploy: document.getElementById('fe-deploy'),
    status: document.getElementById('fe-status'),
    problems: document.getElementById('fe-problems'),
    target: document.getElementById('fe-target'),
    route: document.getElementById('fe-route'),
    method: document.getElementById('fe-method'),
    path: document.getElementById('fe-path'),
    query: document.getElementById('fe-query'),
    host: document.getElementById('fe-host'),
    params: document.getElementById('fe-params'),
    headers: document.getElementById('fe-headers'),
    body: document.getElementById('fe-body'),
    run: document.getElementById('fe-run'),
    result: document.getElementById('fe-result')
  };

  // { name, text, model } — model only once Monaco is up.
  var files = data.files.map(function (f) { return { name: f.name, text: f.text, model: null }; });
  var active = 0;
  var draft = null;          // { id, routes, changedSince }
  var liveRoutes = data.live || [];
  var monaco = null;
  var editor = null;
  var textarea = null;
  var busy = false;

  // ?file=Orders.cs&line=12 — how the Functions table's File column opens a handler.
  var query = new URLSearchParams(window.location.search);
  var openLine = parseInt(query.get('line'), 10) || 0;
  (function () {
    var wanted = (query.get('file') || '').toLowerCase();
    var index = files.findIndex(function (f) { return f.name.toLowerCase() === wanted; });
    if (index >= 0) active = index;
  })();

  // ------------------------------------------------------------------ helpers

  function escapeHtml(value) {
    var div = document.createElement('div');
    div.textContent = value == null ? '' : String(value);
    return div.innerHTML;
  }

  function handlerUrl(handler) {
    return window.location.pathname + '?handler=' + handler;
  }

  function status(text, kind) {
    el.status.textContent = text || '';
    el.status.className = kind ? 'fe-status-' + kind : 'muted';
  }

  function post(handler, payload) {
    return fetch(handlerUrl(handler), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf },
      body: JSON.stringify(payload),
      credentials: 'same-origin'
    });
  }

  function currentText(file) {
    if (file.model) return file.model.getValue();
    if (textarea && files[active] === file) return textarea.value;
    return file.text;
  }

  function snapshot() {
    return files.map(function (f) { return { name: f.name, text: currentText(f) }; });
  }

  // ------------------------------------------------------------------ editor

  function startMonaco() {
    if (typeof window.require !== 'function' || !window.require.config) return startTextarea();

    window.MonacoEnvironment = {
      getWorkerUrl: function () {
        // Workers must come from our origin; this one just pulls the real worker from the CDN.
        return 'data:text/javascript;charset=utf-8,' + encodeURIComponent(
          "self.MonacoEnvironment={baseUrl:'" + MONACO + "/'};importScripts('" + MONACO + "/vs/base/worker/workerMain.js');");
      }
    };

    var settled = false;
    var fallback = setTimeout(function () { if (!settled) { settled = true; startTextarea(); } }, 8000);

    window.require.config({ paths: { vs: MONACO + '/vs' } });
    window.require(['vs/editor/editor.main'], function () {
      if (settled) return;
      settled = true;
      clearTimeout(fallback);

      monaco = window.monaco;
      files.forEach(createModel);

      editor = monaco.editor.create(el.editor, {
        model: files[active].model,
        automaticLayout: true,
        minimap: { enabled: false },
        fontSize: 13,
        scrollBeyondLastLine: false,
        tabSize: 4,
        theme: window.matchMedia('(prefers-color-scheme: dark)').matches ? 'vs-dark' : 'vs'
      });

      editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, check);
      if (openLine > 0) goToLine(openLine);
    }, function () {
      if (!settled) { settled = true; clearTimeout(fallback); startTextarea(); }
    });
  }

  function createModel(file) {
    // A .linq file is an XML header over C#; highlighting it as C# serves the part that matters.
    file.model = monaco.editor.createModel(file.text, 'csharp');
    file.model.onDidChangeContent(markChanged);
  }

  // Without the CDN (offline, or blocked) editing still works, just without highlighting.
  function startTextarea() {
    el.editor.classList.add('fe-plain');
    textarea = document.createElement('textarea');
    textarea.className = 'fe-textarea';
    textarea.spellcheck = false;
    textarea.wrap = 'off';
    textarea.value = files[active].text;
    textarea.addEventListener('input', markChanged);
    el.editor.appendChild(textarea);
    if (openLine > 0) goToLine(openLine);
  }

  function select(index) {
    if (textarea) files[active].text = textarea.value;
    active = index;

    if (editor) editor.setModel(files[active].model);
    if (textarea) textarea.value = files[active].text;

    renderTabs();
  }

  function goToLine(line) {
    if (editor) {
      editor.revealLineInCenter(line);
      editor.setPosition({ lineNumber: line, column: 1 });
      editor.focus();
      return;
    }
    if (textarea) {
      var lines = textarea.value.split('\n');
      var offset = lines.slice(0, line - 1).join('\n').length + (line > 1 ? 1 : 0);
      textarea.focus();
      textarea.setSelectionRange(offset, offset);
    }
  }

  function markChanged() {
    if (draft && !draft.changedSince) {
      draft.changedSince = true;
      status('Changed since the last check — tests still run the checked build.', 'warn');
    }
  }

  // ------------------------------------------------------------------ tabs

  function renderTabs() {
    el.tabs.innerHTML = '';

    files.forEach(function (file, index) {
      var tab = document.createElement('div');
      tab.className = 'fe-tab' + (index === active ? ' active' : '');
      tab.setAttribute('role', 'tab');
      tab.setAttribute('aria-selected', index === active ? 'true' : 'false');
      tab.title = 'Double-click to rename';

      var label = document.createElement('button');
      label.type = 'button';
      label.className = 'fe-tab-name';
      label.textContent = file.name;
      label.addEventListener('click', function () { select(index); });
      label.addEventListener('dblclick', function () { rename(index); });
      tab.appendChild(label);

      if (files.length > 1) {
        var close = document.createElement('button');
        close.type = 'button';
        close.className = 'fe-tab-close';
        close.setAttribute('aria-label', 'Remove ' + file.name);
        close.textContent = '×';
        close.addEventListener('click', function () { remove(index); });
        tab.appendChild(close);
      }

      el.tabs.appendChild(tab);
    });

    var add = document.createElement('button');
    add.type = 'button';
    add.className = 'fe-tab-add';
    add.textContent = '+ File';
    add.addEventListener('click', addFile);
    el.tabs.appendChild(add);
  }

  function validName(name, except) {
    if (!/^[A-Za-z0-9._-]+\.(cs|linq)$/i.test(name)) {
      alert('Use a name like Orders.cs or Reports.linq: letters, digits, dots, dashes and underscores.');
      return false;
    }
    var taken = files.some(function (f, i) { return i !== except && f.name.toLowerCase() === name.toLowerCase(); });
    if (taken) { alert('There is already a file named ' + name + '.'); return false; }
    return true;
  }

  function addFile() {
    var name = prompt('Name for the new file (.cs or .linq):', 'Handlers' + (files.length + 1) + '.cs');
    if (!name) return;
    name = name.trim();
    if (!validName(name, -1)) return;

    // A complete example, like the starter, so a new file compiles and answers straight away.
    // Its class and route are named after the file, so it cannot clash with one already there.
    var type = (name.replace(/\.(cs|linq)$/i, '').replace(/[^A-Za-z0-9_]/g, '') || 'More').replace(/^[0-9]/, '_$&');
    var route = '/' + type.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase();
    // A .linq file declares its imports in LINQPad's own header, not as using lines.
    var header = /\.linq$/i.test(name)
      ? ['<Query Kind="Program">',
         '  <Namespace>System.Text.Json</Namespace>',
         '  <Namespace>Microsoft.AspNetCore.Http</Namespace>',
         '  <Namespace>Microsoft.AspNetCore.Mvc</Namespace>',
         '  <IncludeAspNet>true</IncludeAspNet>',
         '</Query>',
         '']
      : ['#:sdk Microsoft.NET.Sdk.Web',
         '',
         'using System.Text.Json;',
         'using Microsoft.AspNetCore.Http;',
         'using Microsoft.AspNetCore.Mvc;',
         ''];
    var text = header.concat([
      'public static class ' + type + 'Handlers',
      '{',
      '    // Serialise with options owned by this file, so they are discarded with it on redeploy.',
      '    private static readonly JsonSerializerOptions Json = new();',
      '',
      '    [HttpGet("' + route + '")]',
      '    public static IResult Get() =>',
      '        Results.Json(new { from = "' + name + '" }, Json);',
      '}',
      ''
    ]).join('\n');

    var file = { name: name, text: text, model: null };
    if (monaco) createModel(file);
    files.push(file);
    select(files.length - 1);
    markChanged();
  }

  function rename(index) {
    var name = prompt('Rename file:', files[index].name);
    if (!name || name.trim() === files[index].name) return;
    name = name.trim();
    if (!validName(name, index)) return;
    files[index].name = name;
    renderTabs();
    markChanged();
  }

  function remove(index) {
    if (!confirm('Remove ' + files[index].name + ' from the editor? It stays live until you deploy.')) return;
    if (textarea) files[active].text = textarea.value;
    var removed = files.splice(index, 1)[0];
    if (removed.model) removed.model.dispose();
    select(Math.min(active, files.length - 1));
    markChanged();
  }

  // ------------------------------------------------------------------ check & deploy

  function setBusy(value, text) {
    busy = value;
    el.check.disabled = value;
    el.deploy.disabled = value;
    if (value) status(text, null);
  }

  function showProblems(result) {
    var diagnostics = (result.diagnostics || []).filter(function (d) { return d.severity === 'error' || d.severity === 'warning'; });

    if (monaco) {
      files.forEach(function (file) {
        var markers = diagnostics
          .filter(function (d) { return d.file === file.name && d.line > 0; })
          .map(function (d) {
            return {
              startLineNumber: d.line, endLineNumber: d.line,
              startColumn: Math.max(1, d.column), endColumn: Math.max(1, d.column) + 1,
              message: (d.code ? d.code + ': ' : '') + d.message,
              severity: d.severity === 'error' ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning
            };
          });
        monaco.editor.setModelMarkers(file.model, 'build', markers);
      });
    }

    if (result.ok && diagnostics.length === 0) { el.problems.innerHTML = ''; return; }

    var html = '';
    if (!result.ok) html += '<div class="alert alert-error"><strong>' + escapeHtml(result.error) + '</strong></div>';

    if (diagnostics.length) {
      html += '<ul class="fe-problem-list">' + diagnostics.slice(0, 50).map(function (d, i) {
        var where = d.file ? d.file + (d.line > 0 ? ':' + d.line : '') : '';
        return '<li class="fe-problem-' + d.severity + '" data-index="' + i + '">' +
               '<span class="mono">' + escapeHtml(where) + '</span> ' +
               '<span class="mono muted">' + escapeHtml(d.code) + '</span> ' + escapeHtml(d.message) + '</li>';
      }).join('') + '</ul>';
    }

    el.problems.innerHTML = html;
    el.problems.querySelectorAll('li[data-index]').forEach(function (li) {
      li.addEventListener('click', function () { reveal(diagnostics[+li.getAttribute('data-index')]); });
    });
  }

  function reveal(d) {
    var index = files.findIndex(function (f) { return f.name === d.file; });
    if (index < 0) return;
    select(index);
    if (editor && d.line > 0) {
      editor.revealLineInCenter(d.line);
      editor.setPosition({ lineNumber: d.line, column: Math.max(1, d.column) });
      editor.focus();
    }
  }

  function check() {
    if (busy) return;
    setBusy(true, 'Checking… the first build restores packages and can take a minute.');

    post('Check', { files: snapshot() })
      .then(function (response) { return response.json(); })
      .then(function (result) {
        showProblems(result);
        if (!result.ok) { status('Check failed.', 'error'); return; }

        draft = { id: result.draft, routes: result.routes, changedSince: false };
        el.target.options[0].disabled = false;
        el.target.options[0].textContent = 'Checked build (' + new Date().toLocaleTimeString() + ')';
        el.target.value = '';
        fillRoutes();
        status('Compiled. ' + result.routes.length + ' route(s) ready to test — nothing is live yet.', 'ok');
      })
      .catch(function () { status('The check could not reach the server.', 'error'); })
      .finally(function () { setBusy(false); });
  }

  function deploy() {
    if (busy) return;
    var scope = data.domain ? data.domain : 'every site';
    if (!confirm('Compile these ' + files.length + ' file(s) and make them the live functions on ' + scope + '?')) return;

    setBusy(true, 'Deploying…');
    post('Deploy', { files: snapshot() })
      .then(function (response) { return response.json(); })
      .then(function (result) {
        showProblems(result);
        if (!result.ok) { status('Nothing was deployed; the live functions are unchanged.', 'error'); return; }

        liveRoutes = result.routes;
        el.target.value = 'live';
        fillRoutes();
        status('Live: ' + result.label + ' with ' + result.routes.length + ' route(s).', 'ok');
      })
      .catch(function () { status('The deploy could not reach the server.', 'error'); })
      .finally(function () { setBusy(false); });
  }

  // ------------------------------------------------------------------ test form

  function selectedRoutes() {
    return el.target.value === 'live' ? liveRoutes : (draft ? draft.routes : []);
  }

  function fillRoutes() {
    var routes = selectedRoutes();
    el.route.innerHTML = routes.length
      ? routes.map(function (r) { return '<option>' + escapeHtml(r) + '</option>'; }).join('')
      : '<option value="">No routes</option>';
    applyRoute();
  }

  // A route fills in the method and the path, and grows one input per {parameter}.
  function applyRoute() {
    var route = el.route.value;
    el.params.innerHTML = '';
    if (!route) return;

    var space = route.indexOf(' ');
    var verb = route.slice(0, space);
    var template = route.slice(space + 1);
    el.method.value = verb === 'ANY' ? 'GET' : verb;

    var names = [];
    template.replace(/\{([^}?]+)\??\}/g, function (_, name) { names.push(name); });

    names.forEach(function (name) {
      var field = document.createElement('div');
      field.className = 'field';
      field.innerHTML = '<label>' + escapeHtml(name) + '</label>';
      var input = document.createElement('input');
      input.type = 'text';
      input.spellcheck = false;
      input.setAttribute('data-param', name);
      input.addEventListener('input', function () { buildPath(template); });
      field.appendChild(input);
      el.params.appendChild(field);
    });

    buildPath(template);
  }

  function buildPath(template) {
    var values = {};
    el.params.querySelectorAll('input[data-param]').forEach(function (input) {
      values[input.getAttribute('data-param')] = input.value;
    });

    var path = template.replace(/\{([^}?]+)(\?)?\}/g, function (_, name, optional) {
      var value = values[name];
      return value ? encodeURIComponent(value) : (optional ? '' : '{' + name + '}');
    });

    el.path.value = path.replace(/\/+$/, '') || '/';
  }

  // ------------------------------------------------------------------ run & render

  function run() {
    var target = el.target.value;
    if (target === '' && !draft) { renderMessage('error', 'Check the files first, or pick the live build.'); return; }

    el.run.disabled = true;
    el.run.textContent = 'Running…';
    el.result.innerHTML = '';

    post('Run', {
      target: target === 'live' ? 'live' : draft.id,
      method: el.method.value,
      path: el.path.value,
      query: el.query.value,
      headers: el.headers.value,
      body: el.body.value,
      host: el.host ? el.host.value : null
    })
      .then(function (response) {
        var encoded = response.headers.get('X-Fn-Meta');
        if (!encoded) throw new Error('HTTP ' + response.status);
        var meta = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(encoded), function (c) { return c.charCodeAt(0); })));
        return response.arrayBuffer().then(function (buffer) { render(meta, new Uint8Array(buffer)); });
      })
      .catch(function (error) { renderMessage('error', 'The test could not run: ' + error.message); })
      .finally(function () { el.run.disabled = false; el.run.textContent = 'Run'; });
  }

  function renderMessage(kind, text) {
    el.result.innerHTML = '<div class="alert alert-' + kind + '">' + escapeHtml(text) + '</div>';
  }

  function header(meta, name) {
    var found = (meta.headers || []).find(function (h) { return h.key.toLowerCase() === name; });
    return found ? found.value : null;
  }

  function sniff(bytes) {
    function starts(signature) {
      for (var i = 0; i < signature.length; i++) if (bytes[i] !== signature[i]) return false;
      return bytes.length >= signature.length;
    }
    if (starts([0x25, 0x50, 0x44, 0x46])) return 'application/pdf';
    if (starts([0x89, 0x50, 0x4e, 0x47])) return 'image/png';
    if (starts([0xff, 0xd8, 0xff])) return 'image/jpeg';
    if (starts([0x47, 0x49, 0x46, 0x38])) return 'image/gif';

    var text;
    try { text = new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(0, 4096)); }
    catch (e) { return 'application/octet-stream'; }

    var start = text.trimStart().slice(0, 64).toLowerCase();
    if (start[0] === '{' || start[0] === '[') return 'application/json';
    if (start.indexOf('<svg') === 0) return 'image/svg+xml';
    if (start.indexOf('<!doctype html') === 0 || start.indexOf('<html') === 0) return 'text/html';
    if (start.indexOf('<?xml') === 0) return 'application/xml';
    return 'text/plain';
  }

  var extensions = {
    'application/json': 'json', 'application/pdf': 'pdf', 'application/zip': 'zip', 'application/xml': 'xml',
    'text/html': 'html', 'text/plain': 'txt', 'text/csv': 'csv', 'text/xml': 'xml', 'image/png': 'png',
    'image/jpeg': 'jpg', 'image/gif': 'gif', 'image/svg+xml': 'svg', 'image/webp': 'webp',
    'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': 'xlsx'
  };

  function fileName(meta, type) {
    var disposition = header(meta, 'content-disposition') || '';
    var match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
    if (match) return decodeURIComponent(match[1]);
    return 'response.' + (extensions[type] || 'bin');
  }

  function formatBytes(bytes) {
    var units = ['B', 'KB', 'MB', 'GB'];
    var unit = 0;
    while (bytes >= 1024 && unit < units.length - 1) { bytes /= 1024; unit++; }
    return (unit === 0 ? bytes : bytes.toFixed(1)) + ' ' + units[unit];
  }

  // How a response is shown: readable things inline, images and PDFs rendered, anything else
  // offered as a download rather than dumped as bytes or downloaded unasked.
  function render(meta, bytes) {
    var type = ((meta.contentType || '').split(';')[0].trim().toLowerCase()) || (bytes.length ? sniff(bytes) : '');
    var blob = new Blob([bytes], { type: type || 'application/octet-stream' });
    var url = URL.createObjectURL(blob);
    var name = fileName(meta, type);

    var statusKind = meta.status >= 500 || meta.error ? 'error' : meta.status >= 400 ? 'warn' : 'ok';
    var html = '<div class="fe-response-head">' +
      (meta.status ? '<span class="badge badge-' + statusKind + '">' + meta.status + '</span>' : '') +
      '<span class="muted">' + escapeHtml(type || 'no body') + ' · ' + formatBytes(bytes.length) + ' · ' + meta.elapsedMs + ' ms</span>' +
      (bytes.length ? ' <a href="' + url + '" download="' + escapeHtml(name) + '">Download</a>' : '') +
      '</div>';

    if (meta.error) html += '<pre class="fe-error">' + escapeHtml(meta.error) + '</pre>';
    if (meta.truncated) html += '<div class="alert alert-warning">The body was cut off at 25 MB.</div>';

    var attachment = /^\s*attachment/i.test(header(meta, 'content-disposition') || '');

    if (bytes.length === 0) {
      // Status and headers say it all.
    } else if (attachment || bytes.length > INLINE_LIMIT) {
      html += '<p><a class="btn" href="' + url + '" download="' + escapeHtml(name) + '">Download ' + escapeHtml(name) +
              '</a> <span class="muted">' + escapeHtml(type) + ', ' + formatBytes(bytes.length) + '</span></p>';
    } else if (type.indexOf('image/') === 0) {
      html += '<div class="fe-image"><img src="' + url + '" alt="Response image" /></div>';
    } else if (type === 'application/pdf') {
      html += '<iframe class="fe-frame" src="' + url + '" title="PDF response"></iframe>';
    } else if (type === 'text/html') {
      var source = new TextDecoder().decode(bytes);
      html += '<div class="fe-view-tabs"><button type="button" class="btn-sm" data-view="source">Source</button>' +
              '<button type="button" class="btn-sm" data-view="preview">Preview</button></div>' +
              '<pre class="fe-body" data-pane="source">' + escapeHtml(source) + '</pre>' +
              // No allow-scripts and no allow-same-origin: the page is shown, never run.
              '<iframe class="fe-frame" data-pane="preview" sandbox="" hidden title="HTML preview"></iframe>';
      setTimeout(function () {
        var frame = el.result.querySelector('iframe[data-pane="preview"]');
        if (frame) frame.srcdoc = source;
      }, 0);
    } else if (isText(type)) {
      var text = new TextDecoder().decode(bytes);
      if (type.indexOf('json') >= 0) {
        try { text = JSON.stringify(JSON.parse(text), null, 2); } catch (e) { /* show it as sent */ }
      }
      html += '<pre class="fe-body">' + escapeHtml(text) + '</pre>';
    } else {
      html += '<p><a class="btn" href="' + url + '" download="' + escapeHtml(name) + '">Download ' + escapeHtml(name) +
              '</a> <span class="muted">' + escapeHtml(type || 'unknown type') + ', ' + formatBytes(bytes.length) + '</span></p>';
    }

    if (meta.headers && meta.headers.length) {
      html += '<details class="fe-headers"><summary>Response headers</summary><table><tbody>' +
        meta.headers.map(function (h) {
          return '<tr><td class="mono nowrap">' + escapeHtml(h.key) + '</td><td class="mono">' + escapeHtml(h.value) + '</td></tr>';
        }).join('') + '</tbody></table></details>';
    }

    el.result.innerHTML = html;

    el.result.querySelectorAll('[data-view]').forEach(function (button) {
      button.addEventListener('click', function () {
        var view = button.getAttribute('data-view');
        el.result.querySelectorAll('[data-pane]').forEach(function (pane) {
          pane.hidden = pane.getAttribute('data-pane') !== view;
        });
      });
    });
  }

  function isText(type) {
    return type.indexOf('text/') === 0 || /json|xml|javascript|ecmascript|x-www-form-urlencoded|yaml|csv/.test(type);
  }

  // ------------------------------------------------------------------ start

  el.check.addEventListener('click', check);
  el.deploy.addEventListener('click', deploy);
  el.run.addEventListener('click', run);
  el.target.addEventListener('change', fillRoutes);
  el.route.addEventListener('change', applyRoute);

  el.target.value = 'live';
  renderTabs();
  fillRoutes();
  startMonaco();
})();
