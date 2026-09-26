(function () {
  'use strict';

  // ---- copy-to-clipboard buttons -----------------------------------------
  document.addEventListener('click', function (event) {
    var button = event.target.closest('[data-copy]');
    if (!button) return;

    var source = document.querySelector(button.getAttribute('data-copy'));
    if (!source) return;

    var original = button.textContent;
    var confirm = function () {
      button.textContent = 'Copied';
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

  // ---- small drop zones ([data-dropzone]) -----------------------------------
  //
  // A dashed box that takes files by click or by drop, around a hidden file input. The input's
  // accept list is honoured for dropped files too, which the browser only does for the picker.
  document.querySelectorAll('[data-dropzone]').forEach(function (zone) {
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
        : files.length + ' files: ' + files.map(function (f) { return f.name; }).join(', ');
      hint.textContent = rejected ? 'Skipped ' + rejected + ': only ' + accepted.join(', ') + ' files are accepted.'
        : files.length ? 'Click to choose different files' : emptyHint;
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
        hint.textContent = 'Choose or drop at least one file first.';
        zone.focus();
      });
    }
  });

  // ---- deploy form --------------------------------------------------------
  var form = document.getElementById('deploy-form');
  if (!form) return;

  var dropzone = document.getElementById('dropzone');
  var fileInput = document.getElementById('archive');
  var fileLabel = document.getElementById('file-label');
  var fileHint = document.getElementById('file-hint');
  var domainInput = document.getElementById('domain');
  var domainSelect = document.getElementById('domain-select');
  var submitButton = document.getElementById('deploy-button');
  var progress = document.getElementById('deploy-progress');
  var result = document.getElementById('deploy-result');

  function formatBytes(bytes) {
    var units = ['B', 'KB', 'MB', 'GB'];
    var unit = 0;
    while (bytes >= 1024 && unit < units.length - 1) { bytes /= 1024; unit++; }
    return (unit === 0 ? bytes : bytes.toFixed(1)) + ' ' + units[unit];
  }

  function showFile(file) {
    if (!file) {
      dropzone.classList.remove('has-file');
      fileLabel.textContent = 'Drop a .zip here';
      fileHint.textContent = 'or click to choose a file';
      return;
    }
    dropzone.classList.add('has-file');
    fileLabel.textContent = file.name;
    fileHint.textContent = formatBytes(file.size) + ' — click to choose a different file';
  }

  function message(kind, html) {
    result.innerHTML = '<div class="alert alert-' + kind + '">' + html + '</div>';
  }

  function escapeHtml(value) {
    var div = document.createElement('div');
    div.textContent = value == null ? '' : String(value);
    return div.innerHTML;
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
    if (!file) { message('error', 'Choose a .zip archive first.'); return; }
    if (!domainInput.value.trim()) { message('error', 'Enter the domain this site should be published to.'); return; }

    var data = new FormData(form);
    var request = new XMLHttpRequest();

    request.open('POST', form.getAttribute('action') || window.location.pathname + '?handler=Deploy');
    request.setRequestHeader('X-Requested-With', 'fetch');

    submitButton.disabled = true;
    submitButton.textContent = 'Uploading…';
    progress.hidden = false;
    progress.value = 0;
    result.innerHTML = '';

    request.upload.addEventListener('progress', function (event) {
      if (!event.lengthComputable) return;
      progress.value = (event.loaded / event.total) * 100;
      if (event.loaded === event.total) submitButton.textContent = 'Extracting…';
    });

    request.addEventListener('load', function () {
      submitButton.disabled = false;
      submitButton.textContent = 'Deploy';
      progress.hidden = true;

      var payload;
      try { payload = JSON.parse(request.responseText); } catch (error) { payload = null; }

      if (!payload) {
        message('error', 'The server returned an unexpected response (HTTP ' + request.status + ').');
        return;
      }

      if (!payload.ok) {
        var failure = escapeHtml(payload.error || 'The deployment failed.');
        if (payload.diagnostics && payload.diagnostics.length) {
          failure += '<ul class="mono">' + payload.diagnostics.slice(0, 20).map(function (d) {
            return '<li>' + escapeHtml(d) + '</li>';
          }).join('') + '</ul>';
        }
        message('error', failure);
        return;
      }

      var html = '<strong>Published ' + payload.fileCount + ' file' + (payload.fileCount === 1 ? '' : 's') +
                 ' (' + escapeHtml(payload.size) + ') to <a href="' + escapeHtml(payload.url) + '" target="_blank" rel="noopener">' +
                 escapeHtml(payload.domain) + '</a>.</strong>';
      if (payload.functions && payload.functions.length) {
        html += '<p>Functions from <span class="mono">_functions/</span> are live: ' +
                payload.functions.map(function (r) { return '<span class="mono">' + escapeHtml(r) + '</span>'; }).join(', ') + '</p>';
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
      submitButton.textContent = 'Deploy';
      progress.hidden = true;
      message('error', 'The upload could not be completed. Check your connection and try again.');
    });

    request.send(data);
  });
})();
