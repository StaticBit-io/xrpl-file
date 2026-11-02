// Dev only, for themes-harness.html: the contrast of every visible text and of the non-text parts
// (spinner, progress bar, focus ring, badge and button edges) as rendered, with each colour composed
// over everything painted under it at that point, pictures included. Rows marked need 0 are informative.
(async () => {
  const parse = (c) => {
    const m = c.match(/rgba?\(([^)]+)\)/);
    if (!m) return null;
    const v = m[1].split(/[ ,/]+/).filter(Boolean).map(Number);
    return [v[0], v[1], v[2], v.length > 3 ? v[3] : 1];
  };
  const blend = (bottom, top) => [0, 1, 2].map((i) => top[i] * top[3] + bottom[i] * (1 - top[3])).concat(1);
  const lin = (v) => { v /= 255; return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; };
  const lum = (c) => 0.2126 * lin(c[0]) + 0.7152 * lin(c[1]) + 0.0722 * lin(c[2]);
  const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
  const hex = (c) => '#' + c.slice(0, 3).map((v) => Math.round(v).toString(16).padStart(2, '0')).join('');
  const canvas = document.createElement('canvas');
  const g = canvas.getContext('2d', { willReadFrequently: true });

  function pixel(img, x, y) {
    const r = img.getBoundingClientRect();
    canvas.width = Math.max(1, Math.round(r.width)); canvas.height = Math.max(1, Math.round(r.height));
    g.clearRect(0, 0, canvas.width, canvas.height);
    g.drawImage(img, 0, 0, canvas.width, canvas.height);
    const d = g.getImageData(Math.min(canvas.width - 1, Math.max(0, Math.round(x - r.left))), Math.min(canvas.height - 1, Math.max(0, Math.round(y - r.top))), 1, 1).data;
    return [d[0], d[1], d[2], d[3] / 255];
  }

  function layer(element, x, y) {
    const style = getComputedStyle(element);
    let opacity = 1;
    for (let e = element; e && e.nodeType === 1; e = e.parentNode instanceof ShadowRoot ? null : e.parentNode) opacity *= Number(getComputedStyle(e).opacity);
    const layers = [];
    const bg = parse(style.backgroundColor);
    if (bg && bg[3] > 0) layers.push(bg);
    const image = style.backgroundImage;
    if (image && image.startsWith('linear-gradient')) {
      const stops = [...image.matchAll(/rgba?\([^)]+\)/g)].map((m) => parse(m[0]));
      const r = element.getBoundingClientRect();
      const t = Math.min(1, Math.max(0, (r.bottom - y) / r.height)); // "to top": first stop at the bottom
      layers.push(stops[0].map((v, i) => v + (stops[1][i] - v) * t));
    }
    if (element.tagName === 'IMG' && element.complete && element.naturalWidth) layers.push(pixel(element, x, y));
    return layers.map((l) => [l[0], l[1], l[2], l[3] * opacity]);
  }

  function backdrop(root, element) {
    const r = element.getBoundingClientRect();
    const x = r.left + Math.min(r.width / 2, 6), y = r.top + r.height / 2;
    const stack = root.elementsFromPoint(x, y);
    const start = stack.indexOf(element);
    const below = stack.slice(Math.max(0, start)).reverse();
    let colour = [255, 255, 255, 1];
    for (const e of below) for (const l of layer(e, x, y)) colour = blend(colour, l);
    return colour;
  }

  const shown = (e) => {
    const r = e.getBoundingClientRect();
    return r.width > 2 && r.height > 2 && getComputedStyle(e).visibility !== 'hidden' && e.checkVisibility?.({ opacityProperty: true }) !== false;
  };

  const rows = [];
  for (const host of window.hosts) {
    host.scrollIntoView({ block: 'center' });
    const root = host.shadowRoot;
    for (const e of root.querySelectorAll('*')) {
      const own = [...e.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim());
      if (!own || !shown(e)) continue;
      const fg = parse(getComputedStyle(e).color);
      const bg = backdrop(root, e);
      const size = parseFloat(getComputedStyle(e).fontSize), weight = Number(getComputedStyle(e).fontWeight);
      const large = size >= 24 || (size >= 18.66 && weight >= 700);
      const text = [...e.childNodes].filter((n) => n.nodeType === 3).map((n) => n.textContent).join('').trim().slice(0, 28);
      rows.push({ kind: 'text', host: host.dataset.label, what: `${e.id || e.className || e.tagName} "${text}"`, fg: hex(blend(bg, fg)), bg: hex(bg), ratio: +ratio(blend(bg, fg), bg).toFixed(2), need: large ? 3 : 4.5 });
    }
    const p = (id) => root.getElementById(id);
    const frame = p('frame');
    const frameBg = backdrop(root, frame.querySelector('.view:not([hidden])') ?? frame);
    const add = (what, a, b, need = 3) => rows.push({ kind: 'ui', host: host.dataset.label, what, fg: hex(a), bg: hex(b), ratio: +ratio(a, b).toFixed(2), need });
    for (const ring of root.querySelectorAll('.view:not([hidden]) .ring, .overlay:not([hidden]) .ring')) {
      if (!shown(ring)) continue;
      const base = backdrop(root, ring);
      const s = getComputedStyle(ring);
      const track = blend(base, parse(s.borderBottomColor)), top = blend(base, parse(s.borderTopColor));
      add('spinner arc vs its track', top, track); add('spinner track vs background (informative)', track, base, 0);
    }
    for (const bar of root.querySelectorAll('.view:not([hidden]) .bar, .overlay:not([hidden]) .bar')) {
      if (!shown(bar)) continue;
      const r = bar.getBoundingClientRect();
      const bx = r.left + r.width * 0.75, by = r.top + 1;
      const stack = root.elementsFromPoint(bx, by);
      let base = [255, 255, 255, 1];
      for (const e of stack.slice(stack.indexOf(bar) + 1).reverse()) for (const l of layer(e, bx, by)) base = blend(base, l);
      const track = blend(base, parse(getComputedStyle(bar).backgroundColor));
      const fill = blend(track, parse(getComputedStyle(bar.firstElementChild).backgroundColor));
      add('progress fill vs track', fill, track); add('progress fill vs background', fill, base); add('progress track vs background (informative)', track, base, 0);
    }
    for (const btn of root.querySelectorAll('button')) {
      if (!shown(btn)) continue;
      const r = btn.getBoundingClientRect();
      const s = getComputedStyle(btn);
      const stack = root.elementsFromPoint(r.left - 3, r.top + r.height / 2);
      let around = [255, 255, 255, 1];
      for (const e of stack.reverse()) for (const l of layer(e, r.left - 3, r.top + r.height / 2)) around = blend(around, l);
      const fill = parse(s.backgroundColor);
      const face = blend(around, fill);
      if (fill[3] > 0) add(`${btn.id} fill vs surroundings`, face, around);
      if (parseFloat(s.borderTopWidth) > 0) add(`${btn.id} border vs surroundings`, blend(around, parse(s.borderTopColor)), around);
      // The ring sits outside the button, on what surrounds it; the icon tier draws it inside, on the face.
      const box = root.getElementById('frame').getBoundingClientRect();
      const inset = box.width < 120 || box.height < 56;
      // Read from the custom properties: the ring exists only under :focus-visible, which a script cannot set.
      const token = !inset || !btn.classList.contains('btn') ? '--accent' : btn.classList.contains('play') ? '--overlay-foreground' : '--on-accent';
      const probe = document.createElement('span');
      probe.style.color = s.getPropertyValue(token).trim();
      document.body.append(probe);
      const ringColour = parse(getComputedStyle(probe).color);
      probe.remove();
      add(`${btn.id} focus ring (${inset ? 'inside' : 'outside'})`, ringColour, inset ? face : around);
    }
    const badge = p('dlBadge');
    if (shown(badge)) add('type badge border', blend(frameBg, parse(getComputedStyle(badge).borderTopColor)), frameBg);
  }
  window.rows = rows;
  return rows;
})();
