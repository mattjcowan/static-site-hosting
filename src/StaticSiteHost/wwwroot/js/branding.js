(function () {
  'use strict';

  // The branding editor (Pages/Shared/_BrandingEditor.cshtml): keeps each colour picker and its
  // #rrggbb box in step, shows every change in the light and dark previews, checks contrast as the
  // colours change, and fills the form from a theme starter. Nothing is saved until the form is.
  var form = document.querySelector('[data-branding]');
  if (!form) return;

  var TOKENS = ['bg', 'surface', 'surface-2', 'border', 'text', 'muted', 'accent', 'accent-text', 'danger', 'success', 'warning'];
  var SCHEMES = ['light', 'dark'];

  // The pairs worth checking, and the ratio each needs: as BrandingTheme.ContrastPairs.
  var PAIRS = [
    { fg: 'text', bg: 'bg', needs: 4.5, label: 'js.branding.pair.textOnBg' },
    { fg: 'text', bg: 'surface', needs: 4.5, label: 'js.branding.pair.textOnSurface' },
    { fg: 'muted', bg: 'surface', needs: 4.5, label: 'js.branding.pair.mutedOnSurface' },
    { fg: 'accent', bg: 'surface', needs: 3, label: 'js.branding.pair.accentOnSurface' },
    { fg: 'accent-text', bg: 'accent', needs: 4.5, label: 'js.branding.pair.accentTextOnAccent' },
    { fg: 'danger', bg: 'surface', needs: 4.5, label: 'js.branding.pair.dangerOnSurface' }
  ];

  function json(selector) {
    var el = form.querySelector(selector);
    try { return el ? JSON.parse(el.textContent) : null; } catch (e) { return null; }
  }

  var starters = json('[data-branding-starters]') || [];
  var inherited = json('[data-branding-inherited]');
  if (inherited) starters.push(inherited);
  var stacks = json('[data-branding-stacks]') || {};
  var panes = { light: form.querySelector('[data-preview="light"]'), dark: form.querySelector('[data-preview="dark"]') };

  function field(name) { return form.querySelector('[name="' + name + '"]'); }
  function hexBox(scheme, token) { return form.querySelector('[data-hex="' + scheme + ':' + token + '"]'); }
  function picker(scheme, token) { return form.querySelector('[data-color="' + scheme + ':' + token + '"]'); }
  function isHex(value) { return /^#[0-9a-fA-F]{6}$/.test(value); }

  function color(scheme, token) {
    var value = hexBox(scheme, token).value.trim();
    return isHex(value) ? value.toLowerCase() : null;
  }

  // ---- contrast, as WCAG measures it --------------------------------------
  function luminance(hex) {
    var c = [1, 3, 5].map(function (i) {
      var v = parseInt(hex.substr(i, 2), 16) / 255;
      return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
    });
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
  }

  function ratio(a, b) {
    var x = luminance(a), y = luminance(b);
    return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
  }

  function showContrast() {
    var box = form.querySelector('[data-contrast]');
    if (!box) return;
    box.textContent = '';
    SCHEMES.forEach(function (scheme) {
      var column = document.createElement('div');
      var heading = document.createElement('p');
      heading.className = 'side-group';
      heading.textContent = form.querySelector('[data-preview="' + scheme + '"] .preview-scheme').textContent;
      column.appendChild(heading);
      var list = document.createElement('ul');
      PAIRS.forEach(function (pair) {
        var fg = color(scheme, pair.fg), bg = color(scheme, pair.bg);
        if (!fg || !bg) return;
        var r = ratio(fg, bg);
        var item = document.createElement('li');
        var name = document.createElement('span');
        name.textContent = siteHost.t(pair.label);
        var value = document.createElement('span');
        value.className = 'contrast-value';
        var number = document.createElement('span');
        number.className = 'mono';
        number.textContent = siteHost.t('js.branding.ratio', { ratio: r.toFixed(1) });
        var badge = document.createElement('span');
        var ok = r >= pair.needs, large = !ok && r >= 3;
        badge.className = 'badge ' + (ok ? 'badge-ok' : large ? 'badge-warn' : 'badge-off');
        badge.textContent = siteHost.t(ok ? 'js.branding.pass' : large ? 'js.branding.large' : 'js.branding.fail');
        value.appendChild(number);
        value.appendChild(badge);
        item.appendChild(name);
        item.appendChild(value);
        list.appendChild(item);
      });
      column.appendChild(list);
      box.appendChild(column);
    });
  }

  // ---- the previews ----------------------------------------------------------
  function format(el, value) {
    var output = form.querySelector('[data-output="' + el.getAttribute('data-size') + '"]');
    if (output) output.textContent = (el.getAttribute('data-format') || '{size}').replace('{size}', value).replace('{value}', Number(value).toFixed(2));
  }

  function update() {
    SCHEMES.forEach(function (scheme) {
      var pane = panes[scheme];
      if (!pane) return;
      TOKENS.forEach(function (token) {
        var value = color(scheme, token);
        if (value) pane.style.setProperty('--' + token, value);
      });
      pane.style.setProperty('--font-body', stacks[field('BodyFont').value] || '');
      pane.style.setProperty('--font-heading', stacks[field('HeadingFont').value] || '');
      pane.style.setProperty('--text-size', field('TextSize').value + 'px');
      pane.style.setProperty('--heading-weight', field('HeadingWeight').value);
      var radius = Number(field('Radius').value);
      pane.style.setProperty('--radius', radius + 'px');
      pane.style.setProperty('--radius-sm', Math.round(radius * 0.75) + 'px');
      pane.style.setProperty('--density', field('Density').value);
    });

    form.querySelectorAll('[data-size]').forEach(function (el) { if (el.type === 'range') format(el, el.value); });

    var nameBox = form.querySelector('[data-brand-name]');
    var name = nameBox.value.trim() || nameBox.getAttribute('data-brand-default-name') || nameBox.placeholder;
    var logoHasName = form.querySelector('[data-brand-logo-has-name]').checked;
    form.querySelectorAll('.preview-pane').forEach(function (pane) {
      var logo = pane.querySelector('[data-preview-logo]');
      var hasLogo = logo && !logo.hidden;
      var nameEl = pane.querySelector('[data-preview-name]');
      nameEl.textContent = name;
      // A site's visitor screens show no name when neither the site nor the server has one.
      nameEl.hidden = (hasLogo && logoHasName) || !name;
      var mark = pane.querySelector('[data-preview-mark]');
      if (mark) mark.hidden = hasLogo;
    });

    showContrast();
  }

  // ---- colours: picker and text box in step -------------------------------
  form.addEventListener('input', function (event) {
    var target = event.target;
    var key = target.getAttribute('data-color');
    if (key) {
      var parts = key.split(':');
      hexBox(parts[0], parts[1]).value = target.value;
      hexBox(parts[0], parts[1]).removeAttribute('aria-invalid');
    }
    key = target.getAttribute('data-hex');
    if (key) {
      var value = target.value.trim();
      if (isHex(value)) {
        var p = key.split(':');
        picker(p[0], p[1]).value = value.toLowerCase();
        target.removeAttribute('aria-invalid');
        target.title = '';
      } else {
        target.setAttribute('aria-invalid', 'true');
        target.title = siteHost.t('js.branding.notHex');
      }
    }
    update();
  });
  form.addEventListener('change', update);

  // ---- theme starters ---------------------------------------------------------
  form.addEventListener('click', function (event) {
    var button = event.target.closest('[data-starter]');
    if (!button) return;
    var starter = starters.filter(function (s) { return s.id === button.getAttribute('data-starter'); })[0];
    if (!starter) return;

    SCHEMES.forEach(function (scheme) {
      TOKENS.forEach(function (token) {
        var value = starter[scheme][token];
        if (!value) return;
        hexBox(scheme, token).value = value;
        hexBox(scheme, token).removeAttribute('aria-invalid');
        picker(scheme, token).value = value;
      });
    });
    field('HeadingFont').value = starter.headingFont;
    field('BodyFont').value = starter.bodyFont;
    field('TextSize').value = starter.textSize;
    field('HeadingWeight').value = starter.headingWeight;
    field('Radius').value = starter.radius;
    field('Density').value = starter.density;
    update();

    var status = form.querySelector('[data-starter-status]');
    if (status) status.textContent = siteHost.t('js.branding.applied', { name: starter.name });
  });

  // ---- logos: shown in the preview as soon as they are chosen -----------------
  form.addEventListener('change', function (event) {
    var input = event.target;
    var which = input.getAttribute('data-logo-input') || input.getAttribute('data-logo-remove');
    if (!which || which === 'Favicon') return;

    var url = null;
    if (input.type === 'file' && input.files && input.files[0]) url = URL.createObjectURL(input.files[0]);
    var swatch = form.querySelector('[data-logo-swatch="' + which + '"]');

    if (url && swatch) {
      swatch.textContent = '';
      var img = document.createElement('img');
      img.src = url;
      img.alt = '';
      swatch.appendChild(img);
    }

    var targets = which === 'LogoLight' ? ['light'] : ['dark'];
    // With no dark logo of its own, the dark preview shows the light one, as the pages do.
    if (which === 'LogoLight' && !form.querySelector('[data-logo-swatch="LogoDark"] img:not(.inherited)')) targets.push('dark');
    targets.forEach(function (scheme) {
      var logo = form.querySelector('[data-preview-logo="' + scheme + '"]');
      if (!logo) return;
      if (url) { logo.src = url; logo.hidden = false; }
      else if (input.type === 'checkbox') logo.hidden = input.checked;
    });
    update();
  });

  update();
})();
