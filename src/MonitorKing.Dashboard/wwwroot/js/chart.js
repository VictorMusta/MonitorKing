// Courbes temporelles sur canvas : traits de 2 px, voile à 10 %, grille fine, réticule + infobulle au survol,
// légende dès deux séries, sélection d'une période à la souris (onglet Historique).
import { esc, value as fmtValue, time as fmtTime, shortTime, dateTime } from './format.js';

export const SERIES_VARS = ['--s1', '--s2', '--s3', '--s4', '--s5', '--s6', '--s7', '--s8'];

export const cssVar = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

function niceMax(v) {
  if (!(v > 0)) return 1;
  const exp = Math.pow(10, Math.floor(Math.log10(v)));
  const n = v / exp;
  const nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10;
  return nice * exp;
}

const MIN_SCALE = { 'o/s': 1024 * 1024, '/s': 100, ms: 10, '°C': 50, Go: 1, Mo: 512, W: 50 };

function xLabel(ts, span) {
  if (span > 36 * 3600_000) return dateTime(ts);
  if (span <= 5 * 60_000) return fmtTime(ts);
  return shortTime(ts);
}

/** Écart au-delà duquel deux points ne sont pas reliés (agent arrêté, trou de données). */
function gapThreshold(points) {
  if (points.length < 3) return Infinity;
  const diffs = [];
  for (let i = 1; i < points.length; i++) diffs.push(points[i][0] - points[i - 1][0]);
  diffs.sort((a, b) => a - b);
  return Math.max(diffs[Math.floor(diffs.length / 2)] * 3, 5000);
}

/** Couleur d'une série : la sienne si elle en déclare une (ex. « autres » en gris), sinon la palette. */
const seriesColor = (s, i) => s.color ?? SERIES_VARS[i % SERIES_VARS.length];

/** Grille (0, moitié, max) avec ses valeurs, puis l'axe du temps. */
function drawAxes(ctx, { h, pad, pw, ph }, from, to, yMax, unit) {
  const x = (ts) => pad.l + ((ts - from) / (to - from)) * pw;
  const y = (v) => pad.t + ph - (Math.min(v, yMax) / yMax) * ph;
  const grid = cssVar('--grid');
  const axis = cssVar('--axis');
  const muted = cssVar('--muted');

  ctx.font = `11px ${cssVar('--font') || 'system-ui'}`;
  ctx.textBaseline = 'middle';
  ctx.textAlign = 'right';
  for (const t of [0, 0.5, 1]) {
    const yy = Math.round(y(yMax * t)) + 0.5;
    ctx.strokeStyle = t === 0 ? axis : grid;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(pad.l, yy);
    ctx.lineTo(pad.l + pw, yy);
    ctx.stroke();
    ctx.fillStyle = muted;
    ctx.fillText(fmtValue(yMax * t, unit), pad.l - 8, yy);
  }

  const span = to - from;
  const ticks = Math.max(2, Math.min(6, Math.floor(pw / 110)));
  ctx.textBaseline = 'alphabetic';
  for (let i = 0; i <= ticks; i++) {
    const ts = from + (span * i) / ticks;
    ctx.textAlign = i === 0 ? 'left' : i === ticks ? 'right' : 'center';
    ctx.fillStyle = muted;
    ctx.fillText(xLabel(ts, span), x(ts), h - 6);
  }
}

export class LineChart {
  /**
   * @param {HTMLElement} el
   * @param {{series: {key: string, label: string}[], unit?: string, max?: number|null, height?: number,
   *          area?: boolean, selectable?: boolean, onSelect?: Function, showLastDot?: boolean}} options
   */
  constructor(el, options) {
    this.options = { unit: '', max: null, height: 160, area: true, selectable: false, onSelect: null, showLastDot: true, ...options };
    this.series = options.series;
    this.el = el;
    this.data = {};
    this.markers = [];
    this.from = Date.now() - 60_000;
    this.to = Date.now();
    this.hover = null;
    this.drag = null;
    this.selection = null;

    el.innerHTML = '';
    if (this.series.length > 1) {
      const legend = document.createElement('div');
      legend.className = this.options.swatch === 'area' ? 'chart-legend area' : 'chart-legend';
      legend.innerHTML = this.series
        .map((s, i) => `<span><i style="background:var(${seriesColor(s, i)})"></i>${esc(s.label)}</span>`)
        .join('');
      el.appendChild(legend);
    }

    this.plot = document.createElement('div');
    this.plot.className = 'chart-plot';
    this.plot.style.height = `${this.options.height}px`;
    this.canvas = document.createElement('canvas');
    this.canvas.setAttribute('role', 'img');
    this.canvas.setAttribute('aria-label', this.series.map((s) => s.label).join(', '));
    this.tip = document.createElement('div');
    this.tip.className = 'chart-tip';
    this.tip.hidden = true;
    this.plot.append(this.canvas, this.tip);
    el.appendChild(this.plot);

    this.plot.addEventListener('pointermove', (e) => this.onMove(e));
    this.plot.addEventListener('pointerleave', () => {
      this.hover = null;
      if (!this.drag) this.draw();
    });
    if (this.options.selectable) {
      this.plot.style.cursor = 'crosshair';
      this.plot.addEventListener('pointerdown', (e) => this.onDown(e));
      this.plot.addEventListener('pointerup', (e) => this.onUp(e));
    }

    this.redraw = () => this.draw();
    this.resizeObserver = new ResizeObserver(this.redraw);
    this.resizeObserver.observe(this.plot);
    window.addEventListener('themechange', this.redraw);
  }

  setData(data, from, to) {
    this.data = data;
    this.from = from;
    this.to = Math.max(to, from + 1000);
    this.draw();
  }

  setMarkers(markers) {
    this.markers = markers || [];
    this.draw();
  }

  setSelection(selection) {
    this.selection = selection;
    this.draw();
  }

  destroy() {
    this.resizeObserver.disconnect();
    window.removeEventListener('themechange', this.redraw);
  }

  layout() {
    const w = this.plot.clientWidth;
    const h = this.plot.clientHeight;
    const pad = { l: 58, r: this.options.padRight ?? 10, t: 8, b: 22 };
    return { w, h, pad, pw: Math.max(10, w - pad.l - pad.r), ph: Math.max(10, h - pad.t - pad.b) };
  }

  tsAt(x) {
    const { pad, pw } = this.layout();
    const ratio = Math.min(1, Math.max(0, (x - pad.l) / pw));
    return this.from + ratio * (this.to - this.from);
  }

  localX(e) {
    return e.clientX - this.plot.getBoundingClientRect().left;
  }

  onMove(e) {
    const x = this.localX(e);
    if (this.drag) this.drag.x1 = x;
    this.hover = x;
    this.draw();
  }

  onDown(e) {
    if (e.button !== 0) return;
    const x = this.localX(e);
    this.drag = { x0: x, x1: x };
    this.plot.setPointerCapture(e.pointerId);
  }

  onUp() {
    if (!this.drag) return;
    const { x0, x1 } = this.drag;
    this.drag = null;
    if (Math.abs(x1 - x0) < 8) {
      this.selection = null;
      this.options.onSelect?.(null);
    } else {
      this.selection = { from: Math.round(this.tsAt(Math.min(x0, x1))), to: Math.round(this.tsAt(Math.max(x0, x1))) };
      this.options.onSelect?.(this.selection);
    }
    this.draw();
  }

  visiblePoints(key) {
    return (this.data[key] || []).filter((p) => p[0] >= this.from && p[0] <= this.to && p[1] != null && !Number.isNaN(p[1]));
  }

  /** Dimensionne le canvas et renvoie le contexte prêt à dessiner, ou null si le graphique est masqué. */
  prepare() {
    const geom = this.layout();
    if (!geom.w || !geom.h) return null;
    const dpr = window.devicePixelRatio || 1;
    if (this.canvas.width !== Math.round(geom.w * dpr) || this.canvas.height !== Math.round(geom.h * dpr)) {
      this.canvas.width = Math.round(geom.w * dpr);
      this.canvas.height = Math.round(geom.h * dpr);
    }

    const ctx = this.canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, geom.w, geom.h);
    return { ctx, ...geom };
  }

  draw() {
    const geom = this.prepare();
    if (!geom) return;
    const { ctx, w, pad, pw, ph } = geom;

    const colors = this.series.map((s, i) => cssVar(seriesColor(s, i)));
    const axis = cssVar('--axis');
    const surface = cssVar('--surface');
    const accent = cssVar('--accent');
    const unit = this.options.unit;

    const sets = this.series.map((s) => this.visiblePoints(s.key));
    let peak = 0;
    for (const points of sets) for (const p of points) peak = Math.max(peak, p[2] ?? p[1], p[1]);
    const yMax = this.options.max ?? niceMax(Math.max(peak * 1.08, MIN_SCALE[unit] ?? 1));

    const x = (ts) => pad.l + ((ts - this.from) / (this.to - this.from)) * pw;
    const y = (v) => pad.t + ph - (Math.min(v, yMax) / yMax) * ph;
    drawAxes(ctx, geom, this.from, this.to, yMax, unit);
    const span = this.to - this.from;

    // Marqueurs d'événements (plantages, gels…) en haut du graphique.
    for (const m of this.markers) {
      if (m.ts < this.from || m.ts > this.to) continue;
      const mx = Math.round(x(m.ts)) + 0.5;
      ctx.strokeStyle = m.color;
      ctx.globalAlpha = 0.5;
      ctx.beginPath();
      ctx.moveTo(mx, pad.t);
      ctx.lineTo(mx, pad.t + ph);
      ctx.stroke();
      ctx.globalAlpha = 1;
      ctx.fillStyle = m.color;
      ctx.beginPath();
      ctx.arc(mx, pad.t + 4, 4, 0, Math.PI * 2);
      ctx.fill();
    }

    // Séries.
    sets.forEach((points, i) => {
      if (points.length === 0) return;
      const gap = gapThreshold(points);
      const segments = [];
      let current = [];
      for (let k = 0; k < points.length; k++) {
        if (k > 0 && points[k][0] - points[k - 1][0] > gap) {
          segments.push(current);
          current = [];
        }
        current.push(points[k]);
      }
      segments.push(current);

      for (const seg of segments) {
        if (this.options.area && this.series.length === 1 && seg.length > 1) {
          ctx.globalAlpha = 0.1;
          ctx.fillStyle = colors[i];
          ctx.beginPath();
          ctx.moveTo(x(seg[0][0]), y(0));
          for (const p of seg) ctx.lineTo(x(p[0]), y(p[1]));
          ctx.lineTo(x(seg[seg.length - 1][0]), y(0));
          ctx.closePath();
          ctx.fill();
          ctx.globalAlpha = 1;
        }

        ctx.strokeStyle = colors[i];
        ctx.lineWidth = 2;
        ctx.lineJoin = 'round';
        ctx.lineCap = 'round';
        ctx.beginPath();
        seg.forEach((p, k) => (k === 0 ? ctx.moveTo(x(p[0]), y(p[1])) : ctx.lineTo(x(p[0]), y(p[1]))));
        if (seg.length === 1) ctx.lineTo(x(seg[0][0]) + 1, y(seg[0][1]));
        ctx.stroke();
      }

      if (this.options.showLastDot && !this.hover) {
        const last = points[points.length - 1];
        this.dot(ctx, x(last[0]), y(last[1]), colors[i], surface);
      }
    });

    // Sélection (glisser) ou période retenue.
    const sel = this.drag
      ? { a: Math.min(this.drag.x0, this.drag.x1), b: Math.max(this.drag.x0, this.drag.x1) }
      : this.selection
        ? { a: x(this.selection.from), b: x(this.selection.to) }
        : null;
    if (sel) {
      ctx.fillStyle = accent;
      ctx.globalAlpha = 0.12;
      ctx.fillRect(Math.max(pad.l, sel.a), pad.t, Math.min(pad.l + pw, sel.b) - Math.max(pad.l, sel.a), ph);
      ctx.globalAlpha = 1;
      ctx.strokeStyle = accent;
      ctx.lineWidth = 1;
      for (const edge of [sel.a, sel.b]) {
        ctx.beginPath();
        ctx.moveTo(Math.round(edge) + 0.5, pad.t);
        ctx.lineTo(Math.round(edge) + 0.5, pad.t + ph);
        ctx.stroke();
      }
    }

    // Réticule + infobulle.
    if (this.hover != null && this.hover >= pad.l && this.hover <= pad.l + pw && !this.drag) {
      const ts = this.tsAt(this.hover);
      const rows = [];
      let anchorTs = null;
      sets.forEach((points, i) => {
        const p = nearest(points, ts);
        if (!p) return;
        anchorTs ??= p[0];
        rows.push({ color: colors[i], label: this.series[i].label, value: p[1], max: p[2] });
      });

      if (anchorTs != null) {
        const cx = Math.round(x(anchorTs)) + 0.5;
        ctx.strokeStyle = axis;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(cx, pad.t);
        ctx.lineTo(cx, pad.t + ph);
        ctx.stroke();
        sets.forEach((points, i) => {
          const p = nearest(points, ts);
          if (p) this.dot(ctx, x(p[0]), y(p[1]), colors[i], surface);
        });

        const nearbyMarkers = this.markers.filter((m) => Math.abs(m.ts - ts) <= span * 0.015);
        const showMax = rows.some((r) => r.max != null && r.max > r.value * 1.15 && span > 15 * 60_000);
        this.tip.innerHTML =
          `<div class="t">${esc(span > 36 * 3600_000 ? dateTime(anchorTs) : fmtTime(anchorTs))}</div>` +
          rows
            .map((r) => `<div class="r"><i style="background:${r.color}"></i>${esc(r.label)}<b>${esc(fmtValue(r.value, unit))}${showMax && r.max != null ? ` <span class="muted">(pic ${esc(fmtValue(r.max, unit))})</span>` : ''}</b></div>`)
            .join('') +
          nearbyMarkers.map((m) => `<div class="r" style="margin-top:4px"><i style="background:${m.color}"></i>${esc(m.label)}</div>`).join('');
        this.tip.hidden = false;
        const tipWidth = this.tip.offsetWidth;
        const left = cx + 14 + tipWidth > w ? cx - 14 - tipWidth : cx + 14;
        this.tip.style.left = `${Math.max(0, left)}px`;
        this.tip.style.top = `${pad.t}px`;
      } else {
        this.tip.hidden = true;
      }
    } else {
      this.tip.hidden = true;
    }
  }

  dot(ctx, cx, cy, color, surface) {
    ctx.fillStyle = surface;
    ctx.beginPath();
    ctx.arc(cx, cy, 6, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.arc(cx, cy, 4, 0, Math.PI * 2);
    ctx.fill();
  }
}

/**
 * Aires empilées : chaque série est une couche posée sur les précédentes (toutes partagent les mêmes instants).
 * En mode « percent », chaque instant est ramené à 100 % : on lit la part de chacun. La part (ou la valeur)
 * du dernier instant est écrite à droite de chaque couche.
 */
export class StackedChart extends LineChart {
  constructor(el, options) {
    super(el, { area: false, showLastDot: false, padRight: 64, swatch: 'area', percent: false, ...options });
  }

  /** Bas et haut de chaque couche, instant par instant (en % du total en mode « percent »). */
  bands() {
    const ts = (this.data[this.series[0]?.key] || []).map((p) => p[0]);
    const layers = this.series.map((s) => {
      const points = this.data[s.key] || [];
      return ts.map((_, k) => Math.max(0, points[k]?.[1] ?? 0));
    });
    const totals = ts.map((_, k) => layers.reduce((sum, layer) => sum + layer[k], 0));
    const level = ts.map(() => 0);
    const bottoms = [];
    const tops = [];
    for (const layer of layers) {
      bottoms.push(level.slice());
      for (let k = 0; k < ts.length; k++) {
        level[k] += this.options.percent ? (totals[k] > 0 ? (layer[k] / totals[k]) * 100 : 0) : layer[k];
      }
      tops.push(level.slice());
    }
    return { ts, layers, totals, bottoms, tops };
  }

  draw() {
    const geom = this.prepare();
    if (!geom) return;
    const { ctx, w, pad, pw, ph } = geom;
    const { ts, layers, totals, bottoms, tops } = this.bands();
    const colors = this.series.map((s, i) => cssVar(seriesColor(s, i)));
    const percent = this.options.percent;
    const unit = this.options.unit;
    const peak = Math.max(0, ...(tops[tops.length - 1] ?? [0]));
    const yMax = percent ? 100 : niceMax(Math.max(peak * 1.05, MIN_SCALE[unit] ?? 1));
    const x = (t) => pad.l + ((t - this.from) / (this.to - this.from)) * pw;
    const y = (v) => pad.t + ph - (Math.min(v, yMax) / yMax) * ph;
    drawAxes(ctx, geom, this.from, this.to, yMax, percent ? '%' : unit);
    if (ts.length === 0) {
      this.tip.hidden = true;
      return;
    }

    // Tronçons continus : pas d'aire tirée à travers un trou (agent arrêté).
    const gap = gapThreshold(ts.map((t) => [t]));
    const segments = [];
    let start = 0;
    for (let k = 1; k <= ts.length; k++) {
      if (k === ts.length || ts[k] - ts[k - 1] > gap) {
        segments.push([start, k]);
        start = k;
      }
    }

    layers.forEach((_, i) => {
      ctx.fillStyle = colors[i];
      for (const [a, b] of segments) {
        if (b - a === 1) {
          ctx.fillRect(x(ts[a]) - 1, y(tops[i][a]), 2, y(bottoms[i][a]) - y(tops[i][a]));
          continue;
        }
        ctx.beginPath();
        ctx.moveTo(x(ts[a]), y(tops[i][a]));
        for (let k = a + 1; k < b; k++) ctx.lineTo(x(ts[k]), y(tops[i][k]));
        for (let k = b - 1; k >= a; k--) ctx.lineTo(x(ts[k]), y(bottoms[i][k]));
        ctx.closePath();
        ctx.fill();
      }
    });

    // Fin liseré entre les couches, pour les distinguer même quand deux couleurs se ressemblent.
    ctx.strokeStyle = cssVar('--surface');
    ctx.lineWidth = 1;
    ctx.globalAlpha = 0.7;
    for (let i = 0; i < layers.length - 1; i++) {
      for (const [a, b] of segments) {
        if (b - a < 2) continue;
        ctx.beginPath();
        ctx.moveTo(x(ts[a]), y(tops[i][a]));
        for (let k = a + 1; k < b; k++) ctx.lineTo(x(ts[k]), y(tops[i][k]));
        ctx.stroke();
      }
    }
    ctx.globalAlpha = 1;

    // À droite : la part (ou la valeur) de chaque couche au dernier instant, sans chevauchement.
    const last = ts.length - 1;
    const share = (k, v) => (totals[k] > 0 ? (v / totals[k]) * 100 : 0);
    const labels = layers
      .map((layer, i) => ({ i, mid: (y(tops[i][last]) + y(bottoms[i][last])) / 2, value: layer[last], share: share(last, layer[last]) }))
      .filter((l) => l.share >= 0.5)
      .sort((a, b) => a.mid - b.mid);
    ctx.font = `600 11px ${cssVar('--font') || 'system-ui'}`;
    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';
    const muted = cssVar('--muted');
    let previous = -Infinity;
    for (const l of labels) {
      const ly = Math.min(pad.t + ph, Math.max(previous + 12, l.mid));
      previous = ly;
      ctx.fillStyle = this.series[l.i].color ? muted : colors[l.i]; // couches grises (autres, reste) : texte lisible
      ctx.fillText(percent ? fmtValue(l.share, '%') : fmtValue(l.value, unit), pad.l + pw + 6, ly);
    }

    // Réticule + infobulle : de la couche du haut à celle du bas, avec la valeur et la part de chacune.
    if (this.hover == null || this.hover < pad.l || this.hover > pad.l + pw) {
      this.tip.hidden = true;
      return;
    }
    const k = nearestIndex(ts, this.tsAt(this.hover));
    const cx = Math.round(x(ts[k])) + 0.5;
    ctx.strokeStyle = cssVar('--axis');
    ctx.beginPath();
    ctx.moveTo(cx, pad.t);
    ctx.lineTo(cx, pad.t + ph);
    ctx.stroke();
    const span = this.to - this.from;
    // Une valeur en % du processeur ne doit pas se confondre avec la part dans le graphique : on la nomme.
    const absolute = (v) => fmtValue(v, unit) + (this.options.valueSuffix ? ` ${this.options.valueSuffix}` : '');
    const rows = layers.map((layer, i) => ({ i, v: layer[k] })).filter((r) => r.v > 0).reverse();
    this.tip.innerHTML =
      `<div class="t">${esc(span > 36 * 3600_000 ? dateTime(ts[k]) : fmtTime(ts[k]))}</div>` +
      rows
        .map((r) => {
          const main = percent ? fmtValue(share(k, r.v), '%') : fmtValue(r.v, unit);
          const second = percent ? absolute(r.v) : `${fmtValue(share(k, r.v), '%')} du total`;
          return `<div class="r"><i style="background:${colors[r.i]}"></i>${esc(this.series[r.i].label)}<b>${esc(main)} <span class="muted">· ${esc(second)}</span></b></div>`;
        })
        .join('') +
      `<div class="r" style="margin-top:4px"><i></i>Total<b>${esc(absolute(totals[k]))}</b></div>`;
    this.tip.hidden = false;
    const tipWidth = this.tip.offsetWidth;
    const left = cx + 14 + tipWidth > w ? cx - 14 - tipWidth : cx + 14;
    this.tip.style.left = `${Math.max(0, left)}px`;
    this.tip.style.top = `${pad.t}px`;
  }
}

function nearestIndex(values, target) {
  let lo = 0;
  let hi = values.length - 1;
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1;
    if (values[mid] < target) lo = mid;
    else hi = mid;
  }
  return Math.abs(values[lo] - target) <= Math.abs(values[hi] - target) ? lo : hi;
}

function nearest(points, ts) {
  if (points.length === 0) return null;
  let lo = 0;
  let hi = points.length - 1;
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1;
    if (points[mid][0] < ts) lo = mid;
    else hi = mid;
  }
  return Math.abs(points[lo][0] - ts) <= Math.abs(points[hi][0] - ts) ? points[lo] : points[hi];
}

/** Petite courbe sans axes pour les tuiles de chiffres. */
export function sparkline(canvas, points, colorVar = '--s1', max = null) {
  const w = canvas.clientWidth;
  const h = canvas.clientHeight;
  if (!w || !h) return;
  const dpr = window.devicePixelRatio || 1;
  canvas.width = Math.round(w * dpr);
  canvas.height = Math.round(h * dpr);
  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);
  if (points.length < 2) return;
  const from = points[0][0];
  const to = points[points.length - 1][0];
  const top = max ?? (Math.max(...points.map((p) => p[1])) * 1.1 || 1);
  const color = cssVar(colorVar);
  const x = (ts) => ((ts - from) / Math.max(1, to - from)) * (w - 4) + 2;
  const y = (v) => h - 2 - (Math.min(v, top) / top) * (h - 4);
  ctx.globalAlpha = 0.1;
  ctx.fillStyle = color;
  ctx.beginPath();
  ctx.moveTo(x(points[0][0]), h);
  for (const p of points) ctx.lineTo(x(p[0]), y(p[1]));
  ctx.lineTo(x(to), h);
  ctx.closePath();
  ctx.fill();
  ctx.globalAlpha = 1;
  ctx.strokeStyle = color;
  ctx.lineWidth = 2;
  ctx.lineJoin = 'round';
  ctx.beginPath();
  points.forEach((p, i) => (i === 0 ? ctx.moveTo(x(p[0]), y(p[1])) : ctx.lineTo(x(p[0]), y(p[1]))));
  ctx.stroke();
}
