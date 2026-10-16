(function () {
  'use strict';

  // ---- light, dark or the system's ----------------------------------------
  //
  // The choice goes in a cookie, so the server sends the page in it next time with no flash of
  // the other colours. "System" removes the cookie. The icons are the ones the server drew.
  var THEMES = [
    { name: '', label: 'js.common.theme.system', title: 'js.common.theme.systemTitle', icon: 'M2 3h20v14H2zM8 21h8M12 17v4' },
    { name: 'light', label: 'js.common.theme.light', title: 'js.common.theme.lightTitle', icon: 'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41' },
    { name: 'dark', label: 'js.common.theme.dark', title: 'js.common.theme.darkTitle', icon: 'M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z' }
  ];

  function showTheme(button, theme) {
    button.title = siteHost.t(theme.title);
    var label = button.querySelector('[data-theme-label]');
    if (label) label.textContent = siteHost.t(theme.label);
    var icon = button.querySelector('[data-theme-icon]');
    if (icon) icon.innerHTML = '<svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" ' +
      'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="' + theme.icon + '"/></svg>';
  }

  document.querySelectorAll('[data-theme-switch]').forEach(function (button) {
    var current = document.documentElement.getAttribute('data-theme') || '';
    showTheme(button, THEMES.filter(function (t) { return t.name === current; })[0] || THEMES[0]);
  });

  document.addEventListener('click', function (event) {
    var button = event.target.closest('[data-theme-switch]');
    if (!button) return;

    var current = document.documentElement.getAttribute('data-theme') || '';
    var index = THEMES.map(function (t) { return t.name; }).indexOf(current);
    var next = THEMES[(index + 1) % THEMES.length];

    if (next.name) document.documentElement.setAttribute('data-theme', next.name);
    else document.documentElement.removeAttribute('data-theme');

    document.cookie = 'ssh.theme=' + (next.name || '') + '; path=/; SameSite=Lax; max-age=' + (next.name ? 31536000 : 0) +
      (location.protocol === 'https:' ? '; Secure' : '');
    showTheme(button, next);
  });

  // ---- the sidebar: collapsed to icons on a wide screen, a drawer on a narrow one ----
  document.addEventListener('click', function (event) {
    if (event.target.closest('[data-sidebar-collapse]')) {
      var collapsed = document.documentElement.classList.toggle('sidebar-collapsed');
      try { localStorage.setItem('ssh.sidebar', collapsed ? 'collapsed' : 'open'); } catch (e) { /* private window */ }
      return;
    }

    var opener = event.target.closest('[data-sidebar-open]');
    if (opener) {
      var open = document.body.classList.toggle('sidebar-open');
      opener.setAttribute('aria-expanded', open ? 'true' : 'false');
      return;
    }

    // A tap outside the drawer, or on one of its links, closes it.
    if (document.body.classList.contains('sidebar-open') &&
        (!event.target.closest('.sidebar') || event.target.closest('.sidebar a'))) {
      document.body.classList.remove('sidebar-open');
    }
  });

  // ---- go to… (Ctrl K / ⌘K) ------------------------------------------------
  //
  // Every page in the menu, then every site and its tabs, fetched the first time it opens.
  // Type to narrow, arrows to move, Enter to go.
  var palette = document.querySelector('[data-palette]');
  if (palette) {
    var input = palette.querySelector('[data-palette-input]');
    var list = palette.querySelector('[data-palette-list]');
    var sitesLoaded = false;

    function items() { return Array.prototype.slice.call(list.querySelectorAll('li[data-href]')); }
    function visible() { return items().filter(function (li) { return !li.hidden; }); }

    function select(li) {
      items().forEach(function (other) { other.setAttribute('aria-selected', other === li ? 'true' : 'false'); });
      if (li) li.scrollIntoView({ block: 'nearest' });
    }

    function filter() {
      var words = input.value.toLowerCase().split(/\s+/).filter(Boolean);
      var shown = 0;
      items().forEach(function (li) {
        var text = (li.getAttribute('data-text') || '').toLowerCase();
        // A site's tabs show once its name, or the tab's, is typed; a bare list stays short.
        var match = words.every(function (w) { return text.indexOf(w) >= 0; }) &&
          (words.length > 0 || !li.hasAttribute('data-tab')) && shown < 60;
        li.hidden = !match;
        if (match) shown++;
      });
      var empty = list.querySelector('.palette-empty');
      if (!shown && !empty) {
        empty = document.createElement('li');
        empty.className = 'palette-empty';
        empty.textContent = siteHost.t('js.common.goTo.nothing');
        list.appendChild(empty);
      } else if (shown && empty) {
        empty.remove();
      }
      select(visible()[0]);
    }

    function loadSites() {
      if (sitesLoaded) return;
      sitesLoaded = true;
      fetch(palette.getAttribute('data-sites-url'), { credentials: 'same-origin', headers: { Accept: 'application/json' } })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (data) {
          if (!data) return;
          data.sites.forEach(function (site) {
            list.appendChild(entry(site.url, site.domain, site.domain, 'sites', siteHost.t('js.common.goTo.site')));
            data.tabs.forEach(function (tab) {
              var li = entry(site.url + '/' + tab.id, site.domain + ' › ' + tab.label, site.domain + ' ' + tab.label, 'arrow-right', '');
              li.setAttribute('data-tab', '');
              list.appendChild(li);
            });
          });
          filter();
        });
    }

    var ICON_PATHS = { sites: 'M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20M2 12h20', 'arrow-right': 'M5 12h14M12 5l7 7-7 7' };

    function entry(href, label, text, icon, sub) {
      var li = document.createElement('li');
      li.setAttribute('role', 'option');
      li.setAttribute('data-href', href);
      li.setAttribute('data-text', text);
      li.innerHTML = '<svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" ' +
        'stroke-linejoin="round" aria-hidden="true"><path d="' + ICON_PATHS[icon] + '"/></svg>';
      var span = document.createElement('span');
      span.textContent = label;
      li.appendChild(span);
      if (sub) {
        var small = document.createElement('span');
        small.className = 'palette-sub';
        small.textContent = sub;
        li.appendChild(small);
      }
      return li;
    }

    function openPalette() {
      if (palette.open) return;
      input.value = '';
      filter();
      palette.showModal();
      input.focus();
      loadSites();
    }

    function go(li) {
      if (li) location.href = li.getAttribute('data-href');
    }

    document.addEventListener('keydown', function (event) {
      if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        openPalette();
      }
    });
    document.addEventListener('click', function (event) {
      if (event.target.closest('[data-palette-open]')) openPalette();
    });

    input.addEventListener('input', filter);
    input.addEventListener('keydown', function (event) {
      var shown = visible();
      var at = shown.indexOf(list.querySelector('li[aria-selected="true"]'));
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        var next = event.key === 'ArrowDown' ? Math.min(at + 1, shown.length - 1) : Math.max(at - 1, 0);
        select(shown[next]);
      } else if (event.key === 'Enter') {
        event.preventDefault();
        go(shown[Math.max(at, 0)]);
      } else if (event.key === 'Escape') {
        // A search box's first Escape only clears it; here one closes the palette.
        event.preventDefault();
        palette.close();
      }
    });
    list.addEventListener('click', function (event) { go(event.target.closest('li[data-href]')); });
    list.addEventListener('mousemove', function (event) {
      var li = event.target.closest('li[data-href]');
      if (li && li.getAttribute('aria-selected') !== 'true') select(li);
    });
    // A click on the backdrop, outside the box, closes it.
    palette.addEventListener('click', function (event) { if (event.target === palette) palette.close(); });
  }

  // ---- tabs: switch in place, keep the address --------------------------------
  //
  // Each tab is a real link, so without this the page simply loads with that tab. With it, the
  // switch is instant. An address with #proxy or the like opens the tab that holds it.
  function showTab(id, push) {
    var panels = document.querySelectorAll('.tab-panel[data-tab]');
    if (!panels.length) return false;
    var found = false;
    panels.forEach(function (panel) {
      var on = panel.getAttribute('data-tab') === id;
      panel.hidden = !on;
      found = found || on;
    });
    if (!found) return false;
    document.querySelectorAll('.tabs a[data-tab]').forEach(function (a) {
      if (a.getAttribute('data-tab') === id) {
        a.setAttribute('aria-current', 'page');
        if (push) history.pushState({ tab: id }, '', a.href);
      } else {
        a.removeAttribute('aria-current');
      }
    });
    return true;
  }

  document.addEventListener('click', function (event) {
    var a = event.target.closest('.tabs a[data-tab]');
    if (!a || event.ctrlKey || event.metaKey || event.shiftKey || event.button !== 0) return;
    if (showTab(a.getAttribute('data-tab'), true)) event.preventDefault();
  });

  window.addEventListener('popstate', function () {
    var a = document.querySelector('.tabs a[href="' + location.pathname + '"]') ||
      document.querySelector('.tabs a[data-tab]');
    if (a) showTab(a.getAttribute('data-tab'), false);
  });

  function tabForHash() {
    if (!location.hash) return;
    var target = document.getElementById(decodeURIComponent(location.hash.slice(1)));
    if (!target) return;
    var moved = false;
    var panel = target.closest('.tab-panel[data-tab]');
    if (panel && panel.hidden) moved = showTab(panel.getAttribute('data-tab'), false);
    // A closed section that holds what the address points at opens for it.
    var section = target.closest('details.section');
    if (section && !section.open) { section.open = true; moved = true; }
    if (moved) target.scrollIntoView();
  }
  tabForHash();
  window.addEventListener('hashchange', tabForHash);

  // ---- sections that open and close (details.section) ----------------------
  //
  // The names of the closed ones go in a cookie, so the next page arrives with them closed. Names
  // are the section's, not the site's: closing Header rules on one site closes it on every site.
  // Sections not on this page keep whatever state they had.
  function closedSections() {
    var match = document.cookie.match(/(?:^|;\s*)ssh\.collapsed=([^;]*)/);
    return match ? match[1].split('.').filter(Boolean) : [];
  }

  document.addEventListener('toggle', function (event) {
    var section = event.target;
    if (!section.matches || !section.matches('details.section[data-section]')) return;

    var name = section.getAttribute('data-section');
    var closed = closedSections().filter(function (n) { return n !== name; });
    if (!section.open) closed.push(name);

    document.cookie = 'ssh.collapsed=' + closed.join('.') + '; path=/; SameSite=Lax; max-age=' + (closed.length ? 31536000 : 0) +
      (location.protocol === 'https:' ? '; Secure' : '');
  }, true);

  // ---- copy-to-clipboard buttons -----------------------------------------
  document.addEventListener('click', function (event) {
    var button = event.target.closest('[data-copy]');
    if (!button) return;

    var source = document.querySelector(button.getAttribute('data-copy'));
    if (!source) return;

    var original = button.textContent;
    var confirm = function () {
      button.textContent = siteHost.t('js.app.copy.done');
      setTimeout(function () { button.textContent = original; }, 1600);
    };

    source.focus();
    source.select();

    if (navigator.clipboard && window.isSecureContext) {
      navigator.clipboard.writeText(source.value).then(confirm, function () {
        document.execCommand('copy');
        confirm();
      });
    } else {
      document.execCommand('copy');
      confirm();
    }
  });

  // ---- example rules, appended into a rule editor --------------------------
  document.addEventListener('click', function (event) {
    var button = event.target.closest('[data-insert]');
    if (!button) return;

    var target = document.querySelector(button.getAttribute('data-insert-into'));
    if (!target) return;

    var snippet = button.getAttribute('data-insert');
    var current = target.value.replace(/\s+$/, '');
    target.value = current ? current + '\n\n' + snippet + '\n' : snippet + '\n';

    // Land the caret at the end of what was just added, so typing carries on from there.
    target.focus();
    target.setSelectionRange(target.value.length, target.value.length);
    target.scrollTop = target.scrollHeight;
  });

  // ---- confirmation on destructive forms ----------------------------------
  document.addEventListener('submit', function (event) {
    var message = event.target.getAttribute('data-confirm');
    if (message && !window.confirm(message)) event.preventDefault();
  });

  // ---- slow forms: say what is happening and stop a second submit -----------
  document.addEventListener('submit', function (event) {
    var message = event.target.getAttribute('data-busy');
    if (!message || event.defaultPrevented) return;

    var button = event.target.querySelector('button[type=submit]');
    if (!button) return;

    // Disabled on the next tick: disabling the button inside its own submit event can
    // cancel the submission in some browsers.
    setTimeout(function () {
      button.disabled = true;
      button.textContent = message;
    }, 0);
  });

  // Everything above listens on the document, so it keeps working when the page's content is
  // swapped in place (the assistant does that after a change; see assistant.js). What follows
  // binds to elements, so it runs again on new content through window.siteHost.bind. Each
  // element is bound once, however often bind runs.
  function once(element) {
    if (element.hasAttribute('data-bound')) return false;
    element.setAttribute('data-bound', '');
    return true;
  }

  function bind(root) {
    bindDropzones(root);
    bindDeployForm(root);
  }

  // ---- small drop zones ([data-dropzone]) -----------------------------------
  //
  // A dashed box that takes files by click or by drop, around a hidden file input. The input's
  // accept list is honoured for dropped files too, which the browser only does for the picker.
  function bindDropzones(root) {
    root.querySelectorAll('[data-dropzone]').forEach(function (zone) {
      if (!once(zone)) return;

      var input = zone.querySelector('input[type=file]');
      var label = zone.querySelector('[data-dropzone-label]');
      var hint = zone.querySelector('[data-dropzone-hint]');
      var form = zone.closest('form');
      var emptyLabel = label.textContent;
      var emptyHint = hint.textContent;
      var accepted = (input.getAttribute('accept') || '').split(',')
        .map(function (a) { return a.trim().toLowerCase(); })
        .filter(function (a) { return a.charAt(0) === '.'; });

      function acceptable(file) {
        var name = file.name.toLowerCase();
        return !accepted.length || accepted.some(function (ext) { return name.slice(-ext.length) === ext; });
      }

      function show(rejected) {
        var files = Array.prototype.slice.call(input.files || []);
        zone.classList.toggle('has-file', files.length > 0);
        zone.classList.remove('invalid');
        label.textContent = files.length === 0 ? emptyLabel
          : files.length === 1 ? files[0].name
          : siteHost.tp('js.app.dropzone.files', files.length, { names: files.map(function (f) { return f.name; }).join(', ') });
        hint.textContent = rejected ? siteHost.t('js.app.dropzone.skipped', { names: rejected, types: accepted.join(', ') })
          : files.length ? siteHost.t('js.app.dropzone.change') : emptyHint;
      }

      zone.addEventListener('click', function () { input.click(); });
      zone.addEventListener('keydown', function (event) {
        if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); input.click(); }
      });
      input.addEventListener('click', function (event) { event.stopPropagation(); });
      input.addEventListener('change', function () { show(); });

      ['dragenter', 'dragover'].forEach(function (name) {
        zone.addEventListener(name, function (event) { event.preventDefault(); zone.classList.add('dragover'); });
      });
      ['dragleave', 'drop'].forEach(function (name) {
        zone.addEventListener(name, function (event) { event.preventDefault(); zone.classList.remove('dragover'); });
      });

      zone.addEventListener('drop', function (event) {
        var dropped = Array.prototype.slice.call((event.dataTransfer && event.dataTransfer.files) || []);
        if (!dropped.length) return;

        var keep = new DataTransfer();
        var rejected = [];
        dropped.forEach(function (file) { if (acceptable(file)) keep.items.add(file); else rejected.push(file.name); });

        input.files = keep.files;
        show(rejected.join(', '));
      });

      // On the form itself, so it runs before the document-level busy handler and can stop it.
      if (form) {
        form.addEventListener('submit', function (event) {
          if (event.submitter && event.submitter.getAttribute('form')) return; // another form's button
          if (input.files && input.files.length) return;
          event.preventDefault();
          zone.classList.add('invalid');
          hint.textContent = siteHost.t('js.app.dropzone.empty');
          zone.focus();
        });
      }
    });
  }

  // ---- deploy form --------------------------------------------------------
  function bindDeployForm(root) {
    var form = root.querySelector('#deploy-form');
    if (!form || !once(form)) return;

    var dropzone = root.querySelector('#dropzone');
    var fileInput = root.querySelector('#archive');
    var fileLabel = root.querySelector('#file-label');
    var fileHint = root.querySelector('#file-hint');
    var domainInput = root.querySelector('#domain');
    var domainSelect = root.querySelector('#domain-select');
    var submitButton = root.querySelector('#deploy-button');
    var progress = root.querySelector('#deploy-progress');
    var result = root.querySelector('#deploy-result');

    function formatBytes(bytes) {
      var units = ['B', 'KB', 'MB', 'GB'];
      var unit = 0;
      while (bytes >= 1024 && unit < units.length - 1) { bytes /= 1024; unit++; }
      return (unit === 0 ? bytes : bytes.toFixed(1)) + ' ' + units[unit];
    }

    function showFile(file) {
      if (!file) {
        dropzone.classList.remove('has-file');
        fileLabel.textContent = siteHost.t('js.app.deploy.drop');
        fileHint.textContent = siteHost.t('js.app.deploy.dropHint');
        return;
      }
      dropzone.classList.add('has-file');
      fileLabel.textContent = file.name;
      fileHint.textContent = siteHost.t('js.app.deploy.chosen', { size: formatBytes(file.size) });
    }

    function message(kind, html) {
      result.innerHTML = '<div class="alert alert-' + kind + '">' + html + '</div>';
    }

    function escapeHtml(value) {
      var div = document.createElement('div');
      div.textContent = value == null ? '' : String(value);
      return div.innerHTML;
    }

    // A string with values that are HTML, such as a link: the string's own text is escaped, then
    // each {name} in html is put in as it is.
    function withHtml(text, html) {
      var out = escapeHtml(text);
      Object.keys(html).forEach(function (name) { out = out.split('{' + name + '}').join(html[name]); });
      return out;
    }

    dropzone.addEventListener('click', function () { fileInput.click(); });
    dropzone.addEventListener('keydown', function (event) {
      if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); fileInput.click(); }
    });

    ['dragenter', 'dragover'].forEach(function (name) {
      dropzone.addEventListener(name, function (event) {
        event.preventDefault();
        dropzone.classList.add('dragover');
      });
    });

    ['dragleave', 'drop'].forEach(function (name) {
      dropzone.addEventListener(name, function (event) {
        event.preventDefault();
        dropzone.classList.remove('dragover');
      });
    });

    dropzone.addEventListener('drop', function (event) {
      var files = event.dataTransfer && event.dataTransfer.files;
      if (!files || !files.length) return;
      fileInput.files = files;
      showFile(files[0]);
    });

    fileInput.addEventListener('change', function () { showFile(fileInput.files[0]); });

    if (domainSelect) {
      domainSelect.addEventListener('change', function () {
        if (domainSelect.value) {
          domainInput.value = domainSelect.value;
        } else {
          domainInput.value = '';
          domainInput.focus();
        }
      });
      domainInput.addEventListener('input', function () {
        if (domainSelect.value && domainSelect.value !== domainInput.value) domainSelect.value = '';
      });
    }

    form.addEventListener('submit', function (event) {
      event.preventDefault();

      var file = fileInput.files[0];
      if (!file) { message('error', escapeHtml(siteHost.t('js.app.deploy.noFile'))); return; }
      if (!domainInput.value.trim()) { message('error', escapeHtml(siteHost.t('js.app.deploy.noDomain'))); return; }

      var data = new FormData(form);
      var request = new XMLHttpRequest();

      request.open('POST', form.getAttribute('action') || window.location.pathname + '?handler=Deploy');
      request.setRequestHeader('X-Requested-With', 'fetch');

      submitButton.disabled = true;
      submitButton.textContent = siteHost.t('js.app.deploy.uploading');
      progress.hidden = false;
      progress.value = 0;
      result.innerHTML = '';

      request.upload.addEventListener('progress', function (event) {
        if (!event.lengthComputable) return;
        progress.value = (event.loaded / event.total) * 100;
        if (event.loaded === event.total) submitButton.textContent = siteHost.t('js.app.deploy.unpacking');
      });

      request.addEventListener('load', function () {
        submitButton.disabled = false;
        submitButton.textContent = siteHost.t('js.app.deploy.submit');
        progress.hidden = true;

        var payload;
        try { payload = JSON.parse(request.responseText); } catch (error) { payload = null; }

        if (!payload) {
          message('error', escapeHtml(siteHost.t('js.app.deploy.badAnswer', { status: request.status })));
          return;
        }

        if (!payload.ok) {
          var failure = escapeHtml(payload.error || siteHost.t('js.app.deploy.failed'));
          if (payload.diagnostics && payload.diagnostics.length) {
            failure += '<ul class="mono">' + payload.diagnostics.slice(0, 20).map(function (d) {
              return '<li>' + escapeHtml(d) + '</li>';
            }).join('') + '</ul>';
          }
          message('error', failure);
          return;
        }

        var html = '<strong>' + withHtml(siteHost.tp('js.app.deploy.done', payload.fileCount, { size: payload.size }), {
          domain: '<a href="' + escapeHtml(payload.url) + '" target="_blank" rel="noopener">' + escapeHtml(payload.domain) + '</a>'
        }) + '</strong>';
        if (payload.functions && payload.functions.length) {
          html += '<p>' + withHtml(siteHost.t('js.app.deploy.functions'), {
            folder: '<span class="mono">_functions/</span>',
            routes: payload.functions.map(function (r) { return '<span class="mono">' + escapeHtml(r) + '</span>'; }).join(', ')
          }) + '</p>';
        }
        if (payload.warnings && payload.warnings.length) {
          html += '<ul>' + payload.warnings.map(function (w) { return '<li>' + escapeHtml(w) + '</li>'; }).join('') + '</ul>';
        }
        message('success', html);

        if (domainSelect && !Array.prototype.some.call(domainSelect.options, function (o) { return o.value === payload.domain; })) {
          var option = document.createElement('option');
          option.value = payload.domain;
          option.textContent = payload.domain;
          domainSelect.appendChild(option);
        }
        if (domainSelect) domainSelect.value = payload.domain;

        fileInput.value = '';
        showFile(null);
      });

      request.addEventListener('error', function () {
        submitButton.disabled = false;
        submitButton.textContent = siteHost.t('js.app.deploy.submit');
        progress.hidden = true;
        message('error', escapeHtml(siteHost.t('js.app.deploy.uploadFailed')));
      });

      request.send(data);
    });
  }

  window.siteHost = window.siteHost || {};
  window.siteHost.bind = bind;
  bind(document);
})();
