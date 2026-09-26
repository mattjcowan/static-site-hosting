/* Tonight: the Moon, what the sky is good for, the season's constellation, the latest posts. */
(function () {
  'use strict';

  var now = new Date();
  var moon = Sky.moon(now);

  document.getElementById('moon-art').innerHTML = Sky.moonSvg(moon.age);
  document.getElementById('moon-name').textContent = moon.name;
  document.getElementById('moon-lit').textContent =
    Math.round(moon.illumination * 100) + '% lit · ' + moon.age.toFixed(1) + ' days old';
  document.getElementById('next-full').textContent = Sky.date(moon.nextFull);
  document.getElementById('next-new').textContent = Sky.date(moon.nextNew);

  // What moonlight leaves visible decides what is worth setting up for.
  var advice =
    moon.illumination < 0.3 ? ['Dark skies', 'The Moon is out of the way. Go after galaxies, nebulae and the Milky Way: this is the week for faint things.']
    : moon.illumination > 0.8 ? ['The Moon and planets', 'Moonlight washes out the faint stuff. Look at planets, double stars and bright clusters, or the Moon itself along its edge.']
    : ['The terminator', 'Half-lit Moons are the best Moons. Follow the line between day and night, where crater walls throw long shadows.'];
  document.getElementById('advice-title').textContent = advice[0];
  document.getElementById('advice-text').textContent = advice[1];

  // ---- constellation of the season -------------------------------------------------
  // Hand-placed stars, roughly as they sit on the sky. Radius stands in for brightness.
  var constellations = {
    orion: {
      name: 'Orion, the Hunter',
      text: 'Find the three stars of the belt, then the sword hanging below it. The middle "star" of the sword is the Orion Nebula.',
      stars: {
        Betelgeuse: [70, 55, 4.6, '#ffb070'], Meissa: [140, 30, 2], Bellatrix: [210, 70, 3.2],
        Alnitak: [115, 140, 2.8], Alnilam: [145, 132, 3], Mintaka: [175, 124, 2.6],
        Saiph: [95, 222, 2.8], Rigel: [220, 212, 4.2, '#bcd4ff'], M42: [150, 176, 1.6]
      },
      lines: [['Betelgeuse', 'Meissa'], ['Meissa', 'Bellatrix'], ['Betelgeuse', 'Alnitak'], ['Bellatrix', 'Mintaka'],
              ['Alnitak', 'Alnilam'], ['Alnilam', 'Mintaka'], ['Alnitak', 'Saiph'], ['Mintaka', 'Rigel']],
      labels: ['Betelgeuse', 'Rigel', 'M42']
    },
    triangle: {
      name: 'The Summer Triangle',
      text: 'Three bright stars from three constellations: Vega in Lyra, Deneb in Cygnus, Altair in Aquila. The Milky Way runs right through it.',
      stars: {
        Vega: [80, 52, 4.4, '#dfe8ff'], Sheliak: [98, 88, 1.6], Sulafat: [112, 84, 1.6],
        Deneb: [232, 62, 3.8], Sadr: [196, 108, 2.4], Gienah: [232, 138, 2], Fawaris: [170, 82, 2], Albireo: [150, 170, 2.2, '#ffcf8a'],
        Altair: [128, 226, 4], Tarazed: [118, 208, 2], Alshain: [138, 244, 1.6]
      },
      lines: [['Vega', 'Deneb'], ['Deneb', 'Altair'], ['Altair', 'Vega'], ['Vega', 'Sheliak'], ['Sheliak', 'Sulafat'], ['Sulafat', 'Vega'],
              ['Deneb', 'Sadr'], ['Sadr', 'Albireo'], ['Fawaris', 'Sadr'], ['Sadr', 'Gienah'], ['Tarazed', 'Altair'], ['Altair', 'Alshain']],
      labels: ['Vega', 'Deneb', 'Altair', 'Albireo']
    }
  };

  // Orion owns the evening sky from October to March; the Summer Triangle the rest of the year.
  var month = now.getMonth();
  var shown = month >= 9 || month <= 2 ? constellations.orion : constellations.triangle;
  var mainLines = shown === constellations.triangle ? 3 : 0;

  var svg = '<svg viewBox="0 0 300 260" role="img" aria-label="' + shown.name + '">';
  svg += '<g class="lines">';
  shown.lines.forEach(function (pair, i) {
    var a = shown.stars[pair[0]], b = shown.stars[pair[1]];
    var style = i < mainLines ? ' style="stroke:rgba(246,217,139,.55);stroke-dasharray:3 4"' : '';
    svg += '<line x1="' + a[0] + '" y1="' + a[1] + '" x2="' + b[0] + '" y2="' + b[1] + '"' + style + '/>';
  });
  svg += '</g><g class="stars">';
  Object.keys(shown.stars).forEach(function (name) {
    var s = shown.stars[name];
    var colour = s[3] || '#ffffff';
    svg += '<circle cx="' + s[0] + '" cy="' + s[1] + '" r="' + (s[2] * 2.6) + '" style="fill:' + colour + ';opacity:.12"/>';
    svg += '<circle cx="' + s[0] + '" cy="' + s[1] + '" r="' + s[2] + '" style="fill:' + colour + '"/>';
  });
  svg += '</g>';
  shown.labels.forEach(function (name) {
    var s = shown.stars[name];
    svg += '<text x="' + (s[0] + 8) + '" y="' + (s[1] + 4) + '">' + name + '</text>';
  });
  svg += '</svg>';

  document.getElementById('constellation-name').textContent = shown.name;
  document.getElementById('constellation-art').innerHTML = svg;
  document.getElementById('constellation-text').textContent = shown.text;

  // ---- latest posts --------------------------------------------------------------------
  Sky.api('/api/posts').then(function (result) {
    var slot = document.getElementById('latest');
    var posts = (result.data.posts || []).filter(function (p) { return p.published; }).slice(0, 3);
    if (!posts.length) { slot.innerHTML = '<p class="muted">No notes yet. Clear skies soon.</p>'; return; }

    slot.innerHTML = posts.map(function (post) {
      return '<a class="card" href="' + Sky.postUrl(post.slug) + '">' +
        '<div class="meta"><span>' + Sky.date(post.createdUtc) + '</span><span>' + post.readingMinutes + ' min read</span></div>' +
        '<h3>' + Sky.escape(post.title) + '</h3><p>' + Sky.escape(post.summary) + '</p>' +
        '<div class="meta">' + post.tags.map(function (t) { return '<span class="tag">' + Sky.escape(t) + '</span>'; }).join('') + '</div></a>';
    }).join('');
  });
})();
