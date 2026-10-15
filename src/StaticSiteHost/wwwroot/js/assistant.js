// The management assistant panel (see Endpoints/AssistantEndpoints.cs). It sends a message, reads
// the answer as a stream of JSON lines and draws it as it comes: text, each tool as it runs, and
// Run/Cancel when a step needs the user's say. The conversation lives on the server; this page
// keeps its id and what it has drawn in sessionStorage, so the panel survives moving between pages.
// What is kept belongs to the user who is signed in: it is dropped on sign-out, and never shown
// to anyone else who signs in on the same tab, since an answer can hold an invite link or a password.
(function () {
  'use strict';

  var panel = document.getElementById('assistant');
  if (!panel) return;

  var log = panel.querySelector('.assistant-log');
  var form = panel.querySelector('.assistant-form');
  var input = form.querySelector('textarea');
  var send = form.querySelector('button[type=submit]');
  var csrf = panel.getAttribute('data-csrf');
  var domain = panel.getAttribute('data-domain') || null;
  var toggles = document.querySelectorAll('[data-assistant-toggle]');
  var user = panel.getAttribute('data-user') || '';
  var KEY = 'ssh.assistant';

  var state = load();
  var busy = false;

  function load() {
    try {
      var saved = JSON.parse(sessionStorage.getItem(KEY) || 'null');
      if (saved && Array.isArray(saved.entries) && saved.user === user) return saved;
      sessionStorage.removeItem(KEY); // someone else's, or damaged
    } catch (e) { /* storage unavailable or damaged: start empty */ }
    return { user: user, open: false, conversation: null, entries: [] };
  }

  function save() {
    try {
      // Only the last entries: enough to keep the thread readable, never enough to fill storage.
      sessionStorage.setItem(KEY, JSON.stringify({
        user: user, open: state.open, conversation: state.conversation, entries: state.entries.slice(-150)
      }));
    } catch (e) { /* not fatal */ }
  }

  // ---------------------------------------------------------------- drawing

  function escapeHtml(text) {
    return String(text).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  // Plain text with `code` and **bold**: the model is asked for plain text, and these two read well.
  function formatText(text) {
    return escapeHtml(text)
      .replace(/`([^`\n]+)`/g, '<code>$1</code>')
      .replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>');
  }

  var TOOL_MARK = { running: '…', done: '✓', failed: '✗', cancelled: '–' };

  function render() {
    log.innerHTML = '';
    if (state.entries.length === 0) {
      log.innerHTML = panel.dataset.empty;
      return;
    }
    state.entries.forEach(function (entry) { log.appendChild(draw(entry)); });
    log.scrollTop = log.scrollHeight;
  }

  function draw(entry) {
    var el = document.createElement('div');
    el.className = 'assistant-entry assistant-' + entry.kind;

    if (entry.kind === 'user' || entry.kind === 'assistant') {
      el.innerHTML = formatText(entry.text);
    } else if (entry.kind === 'tool') {
      el.classList.add('is-' + entry.status);
      el.innerHTML = '<span class="assistant-mark">' + (TOOL_MARK[entry.status] || '') + '</span> ' +
        '<span class="mono">' + escapeHtml(entry.summary) + '</span>' +
        (entry.detail ? '<div class="assistant-detail">' + escapeHtml(entry.detail) + '</div>' : '');
    } else if (entry.kind === 'confirm') {
      el.innerHTML = '<div>Run <span class="mono">' + escapeHtml(entry.summary) + '</span>? It cannot be undone.</div>';
      if (!entry.answered) {
        var actions = document.createElement('div');
        actions.className = 'assistant-actions';
        actions.innerHTML = '<button type="button" class="btn-danger" data-run>Run</button> <button type="button" data-cancel>Cancel</button>';
        actions.querySelector('[data-run]').addEventListener('click', function () { decide(entry, 'run'); });
        actions.querySelector('[data-cancel]').addEventListener('click', function () { decide(entry, 'cancel'); });
        el.appendChild(actions);
      } else {
        el.insertAdjacentHTML('beforeend', '<div class="muted">' + (entry.answered === 'run' ? 'Run.' : 'Cancelled.') + '</div>');
      }
    } else if (entry.kind === 'changed') {
      // Shown only when the page was not brought up to date by itself: it has unsaved edits.
      el.innerHTML = 'Changes were made. This page was left as it is, so nothing you typed on it is lost. ' +
        '<button type="button" class="btn-link">Update it anyway</button>';
      el.querySelector('button').addEventListener('click', function () {
        if (document.querySelector('main [data-no-swap]')) {
          location.reload();
          return;
        }
        // Out of the thread for good, so what is drawn stays in step with what is kept.
        var index = state.entries.indexOf(entry);
        if (index >= 0) state.entries.splice(index, 1);
        el.remove();
        save();
        refreshPage();
      });
    } else {
      el.textContent = entry.text;
    }

    return el;
  }

  function add(entry) {
    state.entries.push(entry);
    log.querySelector('.assistant-empty') && (log.innerHTML = '');
    log.appendChild(draw(entry));
    log.scrollTop = log.scrollHeight;
    save();
    return entry;
  }

  function redrawLast(entry) {
    var index = state.entries.lastIndexOf(entry);
    var old = log.children[index];
    if (old) log.replaceChild(draw(entry), old);
    log.scrollTop = log.scrollHeight;
    save();
  }

  // ---------------------------------------------------------------- talking

  function setBusy(value) {
    busy = value;
    send.disabled = value;
    input.disabled = value;
    panel.classList.toggle('is-busy', value);
    if (!value) input.focus();
  }

  function post(body) {
    setBusy(true);
    var reply = null;
    var steps = {}; // this reply's tool steps, by id: ids need only be unique within a reply

    return fetch('/assistant/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf },
      body: JSON.stringify(Object.assign({ conversation: state.conversation, domain: domain }, body))
    }).then(function (response) {
      if (!response.ok || !response.body) {
        return response.json().catch(function () { return {}; }).then(function (data) {
          add({ kind: 'error', text: data.error || ('The assistant could not answer (' + response.status + ').') });
        });
      }

      var reader = response.body.getReader();
      var decoder = new TextDecoder();
      var buffer = '';

      function handle(line) {
        if (!line.trim()) return;
        var event;
        try { event = JSON.parse(line); } catch (e) { return; }

        switch (event.type) {
          case 'start':
            state.conversation = event.conversation;
            break;
          case 'text':
            if (!reply) reply = add({ kind: 'assistant', text: '' });
            reply.text += event.text;
            redrawLast(reply);
            break;
          case 'tool':
            reply = null; // text after a tool starts a new bubble
            var existing = steps[event.id];
            if (existing) {
              existing.status = event.status;
              existing.detail = event.detail || null;
              redrawLast(existing);
            } else {
              steps[event.id] = add({ kind: 'tool', id: event.id, summary: event.summary, status: event.status, detail: event.detail || null });
            }
            break;
          case 'confirm':
            reply = null;
            add({ kind: 'confirm', id: event.id, summary: event.summary, answered: null });
            break;
          case 'error':
            add({ kind: 'error', text: event.message });
            break;
          case 'done':
            if (event.changed) {
              if (hasUnsavedEdits()) add({ kind: 'changed' });
              else refreshPage();
            }
            if (event.inputTokens || event.outputTokens) {
              add({
                kind: 'usage',
                text: event.inputTokens.toLocaleString() + ' tokens in, ' + event.outputTokens.toLocaleString() + ' out, ' +
                  event.rounds + ' call' + (event.rounds === 1 ? '' : 's')
              });
            }
            break;
        }
      }

      function pump() {
        return reader.read().then(function (chunk) {
          if (chunk.done) {
            handle(buffer);
            return;
          }
          buffer += decoder.decode(chunk.value, { stream: true });
          var lines = buffer.split('\n');
          buffer = lines.pop();
          lines.forEach(handle);
          return pump();
        });
      }

      return pump();
    }).catch(function () {
      add({ kind: 'error', text: 'The connection to the server was lost.' });
    }).then(function () {
      setBusy(false);
    });
  }

  // ---------------------------------------------------------------- the page under the panel

  // Whether the page has anything typed or chosen that a refresh would throw away.
  function hasUnsavedEdits() {
    var main = document.querySelector('main');
    if (!main) return false;
    if (main.querySelector('[data-no-swap]')) return true; // a page that holds its state in scripts

    return Array.prototype.some.call(main.querySelectorAll('input, textarea, select'), function (field) {
      if (field.type === 'hidden' || field.readOnly || field.disabled) return false;
      if (field.type === 'file') return field.files && field.files.length > 0;
      if (field.type === 'checkbox' || field.type === 'radio') return field.checked !== field.defaultChecked;
      if (field.tagName === 'SELECT') {
        return Array.prototype.some.call(field.options, function (o) { return o.selected !== o.defaultSelected; });
      }
      return field.value !== field.defaultValue;
    });
  }

  // Brings the page under the panel up to date without reloading it: fetches it again and swaps
  // in its content, inside a view transition where the browser has them, so it cross-fades rather
  // than flashing. The top bar, the panel and the scroll position stay as they are; sections the
  // user had opened stay open. A page that now redirects (its site was deleted or renamed) is
  // followed with an ordinary navigation, which CSS view transitions also smooth.
  var refreshing = null;

  function refreshPage() {
    if (refreshing) return refreshing;

    refreshing = fetch(location.href, { credentials: 'same-origin', cache: 'no-store' })
      .then(function (response) {
        if (response.redirected) {
          location.assign(response.url);
          return;
        }
        if (!response.ok) throw new Error(String(response.status));
        return response.text().then(swap);
      })
      .catch(function () { location.reload(); })
      .then(function () { refreshing = null; });

    return refreshing;
  }

  function swap(html) {
    var next = new DOMParser().parseFromString(html, 'text/html');
    var fresh = next.querySelector('main');
    var current = document.querySelector('main');
    if (!fresh || !current) throw new Error('no main');

    // <details> the user opened, matched by position, since they carry no ids of their own.
    var open = Array.prototype.map.call(current.querySelectorAll('details'), function (d) { return d.open; });

    function apply() {
      current.replaceChildren.apply(current, Array.prototype.slice.call(fresh.childNodes));
      var details = current.querySelectorAll('details');
      if (details.length === open.length) {
        Array.prototype.forEach.call(details, function (d, i) { d.open = open[i]; });
      }
      document.title = next.title;
      if (window.siteHost) window.siteHost.bind(current);
    }

    var calm = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (document.startViewTransition && !calm) document.startViewTransition(apply);
    else apply();
  }

  function decide(entry, decision) {
    if (busy) return;
    entry.answered = decision;
    redrawLast(entry);
    post({ decision: decision });
  }

  form.addEventListener('submit', function (event) {
    event.preventDefault();
    var text = input.value.trim();
    if (!text || busy) return;

    var waiting = state.entries.length && state.entries[state.entries.length - 1];
    if (waiting && waiting.kind === 'confirm' && !waiting.answered) {
      add({ kind: 'error', text: 'Choose Run or Cancel first.' });
      return;
    }

    input.value = '';
    add({ kind: 'user', text: text });
    post({ message: text });
  });

  input.addEventListener('keydown', function (event) {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      form.requestSubmit();
    }
  });

  panel.querySelector('[data-assistant-new]').addEventListener('click', function () {
    if (busy) return;
    if (state.conversation) {
      fetch('/assistant/end?conversation=' + encodeURIComponent(state.conversation), {
        method: 'POST', headers: { 'X-CSRF-TOKEN': csrf }
      });
    }
    state = { user: user, open: true, conversation: null, entries: [] };
    save();
    render();
    input.focus();
  });

  // ---------------------------------------------------------------- opening and closing

  function setOpen(open) {
    state.open = open;
    panel.hidden = !open;
    document.body.classList.toggle('assistant-open', open);
    toggles.forEach(function (t) { t.setAttribute('aria-expanded', String(open)); });
    save();
    if (open) {
      log.scrollTop = log.scrollHeight;
      input.focus();
    }
  }

  toggles.forEach(function (toggle) {
    toggle.addEventListener('click', function () { setOpen(panel.hidden); });
  });

  document.addEventListener('keydown', function (event) {
    if (event.key === 'Escape' && !panel.hidden && !busy) setOpen(false);
  });

  // Signing out takes the conversation with it.
  document.querySelectorAll('form[data-sign-out]').forEach(function (form) {
    form.addEventListener('submit', function () {
      try { sessionStorage.removeItem(KEY); } catch (e) { /* nothing kept */ }
    });
  });

  panel.dataset.empty = log.innerHTML;
  render();
  setOpen(state.open);
})();
