// Model name suggestions for the model fields (Endpoints/ModelSuggestionEndpoints.cs). A field with
// data-model-suggest becomes a combobox: a text box that takes any name, with a list of the models
// its provider offers under it, fetched the first time the field is used for that provider. The
// list is drawn here rather than with a <datalist>, whose popup a browser places and styles as it
// likes: in some, well away from the field. It is fixed to the window, under the field (or above
// it, near the bottom of the window), so a scrolling table around the field cannot clip it.
//
// Where the provider comes from:
//   data-model-provider="#select"   the provider chosen in that select (empty value: none)
//   data-model-provider-id="abc"    a saved provider, fixed
//   data-model-form                 the kind, base URL and key typed into the same form, for a
//                                   provider that is not saved yet
(function () {
  'use strict';

  var tokenField = document.querySelector('input[name="__RequestVerificationToken"]');
  if (!tokenField) return;

  var counter = 0;

  function source(input) {
    var select = input.getAttribute('data-model-provider');
    if (select) {
      var element = document.querySelector(select);
      return element && element.value ? { providerId: element.value } : null;
    }

    var id = input.getAttribute('data-model-provider-id');
    if (id) return { providerId: id };

    if (input.hasAttribute('data-model-form')) {
      var form = input.form;
      var kind = form.querySelector('[name="kind"]');
      if (!kind || !kind.value) return null;
      return {
        kind: kind.value,
        baseUrl: (form.querySelector('[name="baseUrl"]') || {}).value || '',
        apiKey: (form.querySelector('[name="apiKey"]') || {}).value || ''
      };
    }

    return null;
  }

  function attach(input) {
    var listId = 'model-list-' + (++counter);

    // The field, with a button inside its right edge that opens the list.
    var wrap = document.createElement('div');
    wrap.className = 'model-picker';
    input.parentNode.insertBefore(wrap, input);
    wrap.appendChild(input);

    var toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'model-picker-toggle';
    toggle.tabIndex = -1;
    toggle.setAttribute('aria-label', 'Show the models');
    toggle.innerHTML = '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 6l4 4 4-4" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"/></svg>';
    wrap.appendChild(toggle);

    var list = document.createElement('ul');
    list.className = 'model-picker-list';
    list.id = listId;
    list.setAttribute('role', 'listbox');
    list.hidden = true;
    document.body.appendChild(list);

    input.setAttribute('role', 'combobox');
    input.setAttribute('aria-autocomplete', 'list');
    input.setAttribute('aria-controls', listId);
    input.setAttribute('aria-expanded', 'false');

    var models = [];
    var note = '';
    var shown = [];
    var active = -1;
    var loadedFor = null;
    var pending = null;

    // ---------------------------------------------------------------- loading

    function load() {
      var from = source(input);
      var key = from ? JSON.stringify(from) : null;
      if (!from) {
        models = [];
        note = 'Choose a provider first.';
        return;
      }
      if (key === loadedFor || pending === key) return;
      pending = key;
      models = [];
      note = 'Asking the provider for its models…';
      if (!list.hidden) draw();

      fetch('/ui/ai/models', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': tokenField.value },
        body: JSON.stringify(from)
      })
        .then(function (response) { return response.json(); })
        .then(function (data) {
          if (pending !== key) return; // a newer request superseded this one
          pending = null;

          if (data.error || !data.models || !data.models.length) {
            loadedFor = null;
            models = [];
            note = data.error || 'The provider listed no chat models. Type the model name.';
          } else {
            loadedFor = key;
            models = data.models;
            var fast = models.filter(function (m) { return m.fast; }).length;
            note = models.length + ' models from the provider' + (fast ? ', fast ones first' : '') + '. Any name can be typed.';
          }
          if (document.activeElement === input) open();
        })
        .catch(function () {
          pending = null;
          models = [];
          note = 'The models could not be listed. Type the model name.';
          if (!list.hidden) draw();
        });
    }

    // ---------------------------------------------------------------- the list

    function matches() {
      var typed = input.value.trim().toLowerCase();
      // The whole list while the field still holds a full model name: it is a choice, not a search.
      var exact = models.some(function (m) { return m.id.toLowerCase() === typed; });
      if (!typed || exact) return models;
      return models.filter(function (m) {
        return m.id.toLowerCase().indexOf(typed) >= 0 || (m.name || '').toLowerCase().indexOf(typed) >= 0;
      });
    }

    function draw() {
      shown = matches();
      active = Math.min(active, shown.length - 1);
      list.innerHTML = '';

      var status = document.createElement('li');
      status.className = 'model-picker-note';
      status.setAttribute('role', 'presentation');
      status.textContent = shown.length || !models.length ? note : 'No model matches “' + input.value.trim() + '”. It can still be used.';
      list.appendChild(status);

      shown.forEach(function (model, index) {
        var item = document.createElement('li');
        item.id = listId + '-' + index;
        item.setAttribute('role', 'option');
        item.className = 'model-picker-item' + (index === active ? ' is-active' : '');
        item.setAttribute('aria-selected', String(model.id === input.value.trim()));

        var id = document.createElement('span');
        id.className = 'mono';
        id.textContent = model.id;
        item.appendChild(id);

        var label = [model.name, model.fast ? 'fast' : null].filter(Boolean).join(' · ');
        if (label) {
          var detail = document.createElement('span');
          detail.className = 'model-picker-detail';
          detail.textContent = label;
          item.appendChild(detail);
        }

        // On mousedown, before the field loses focus and closes the list.
        item.addEventListener('mousedown', function (event) {
          event.preventDefault();
          choose(index);
        });
        list.appendChild(item);
      });

      if (active >= 0) input.setAttribute('aria-activedescendant', listId + '-' + active);
      else input.removeAttribute('aria-activedescendant');
      place();
    }

    function place() {
      var box = input.getBoundingClientRect();
      var below = window.innerHeight - box.bottom;
      var height = Math.min(list.scrollHeight, 320);
      var above = below < height + 12 && box.top > below;

      // At least wide enough for a model's name and its label on one line, never past the window.
      var width = Math.min(Math.max(box.width, 420), window.innerWidth - 16);
      list.style.width = width + 'px';
      list.style.left = Math.max(8, Math.min(box.left, window.innerWidth - width - 8)) + 'px';
      list.style.top = above ? '' : (box.bottom + 4) + 'px';
      list.style.bottom = above ? (window.innerHeight - box.top + 4) + 'px' : '';
    }

    function open() {
      draw();
      list.hidden = false;
      input.setAttribute('aria-expanded', 'true');
      place();
    }

    function close() {
      list.hidden = true;
      active = -1;
      input.setAttribute('aria-expanded', 'false');
      input.removeAttribute('aria-activedescendant');
    }

    function choose(index) {
      var model = shown[index];
      if (!model) return;
      input.value = model.id;
      input.dispatchEvent(new Event('change', { bubbles: true }));
      close();
    }

    function move(step) {
      if (list.hidden) open();
      if (!shown.length) return;
      active = (active + step + shown.length) % shown.length;
      draw();
      var item = document.getElementById(listId + '-' + active);
      if (item) item.scrollIntoView({ block: 'nearest' });
    }

    // ---------------------------------------------------------------- events

    input.addEventListener('focus', function () { load(); open(); });
    input.addEventListener('input', function () { load(); active = -1; open(); });
    input.addEventListener('blur', close);
    input.addEventListener('keydown', function (event) {
      if (event.key === 'ArrowDown') { event.preventDefault(); move(1); }
      else if (event.key === 'ArrowUp') { event.preventDefault(); move(-1); }
      else if (event.key === 'Escape' && !list.hidden) { event.preventDefault(); event.stopPropagation(); close(); }
      else if (event.key === 'Enter' && !list.hidden && active >= 0) { event.preventDefault(); choose(active); }
    });

    toggle.addEventListener('mousedown', function (event) {
      event.preventDefault(); // keep the field's focus
      if (list.hidden) {
        input.focus();
        load();
        open();
      } else {
        close();
      }
    });

    // The list is fixed to the window, so it follows the field as the page or a table scrolls.
    window.addEventListener('scroll', function () { if (!list.hidden) place(); }, true);
    window.addEventListener('resize', function () { if (!list.hidden) place(); });

    // A different provider, kind, base URL or key means a different list: drop the old one.
    var selector = input.getAttribute('data-model-provider');
    var watched = selector ? [document.querySelector(selector)]
      : input.hasAttribute('data-model-form') ? Array.prototype.slice.call(input.form.querySelectorAll('[name="kind"], [name="baseUrl"], [name="apiKey"]'))
      : [];
    watched.forEach(function (el) {
      if (!el) return;
      el.addEventListener('change', function () {
        loadedFor = null;
        models = [];
        note = '';
      });
    });
  }

  document.querySelectorAll('input[data-model-suggest]').forEach(attach);
})();
