// Catalogue de widgets. Les onglets et l'écran d'accueil personnalisable sont construits avec les mêmes briques :
// ajouter une métrique côté agent la rend aussitôt disponible ici (via /api/metrics).
import { api } from './api.js';
import { store } from './store.js';
import { LineChart, sparkline } from './chart.js';
import * as f from './format.js';
import { icon, severityIcon, SEVERITY_LABEL } from './icons.js';
import { reveal } from './names.js';

export const KIND_LABELS = {
  crash: 'Plantage', hang: 'Gel', power: 'Alimentation', bsod: 'Écran bleu', memory: 'Mémoire',
  disk: 'Disque', hardware: 'Matériel', thermal: 'Thermique', gpu: 'Pilote graphique', service: 'Service', boot: 'Démarrage',
};

const KIND_ICONS = {
  crash: 'crash', hang: 'hang', power: 'power', bsod: 'bsod', memory: 'memory', disk: 'disk',
  hardware: 'hardware', thermal: 'thermal', gpu: 'gpu', service: 'service', boot: 'boot',
};

export const SENSOR_TYPES = {
  Temperature: 'Températures', Fan: 'Ventilateurs', Control: 'Pilotage des ventilateurs', Power: 'Puissance',
  Clock: 'Fréquences', Load: 'Charges', SmallData: 'Mémoire', Data: 'Données', Voltage: 'Tensions', Level: 'Niveaux',
};

const vramTotalMb = () => store.latest?.sensors?.find((s) => s.name === 'GPU Memory Total')?.value ?? null;

export const RESOURCES = {
  cpu: { label: 'Processeur', value: (p) => p.cpu, format: (v) => f.pct(v), max: () => 100 },
  ram: { label: 'Mémoire vive', value: (p) => p.ramMb, format: (v) => f.mb(v), max: () => (store.info?.ramGb ?? 16) * 1024 },
  io: { label: 'Disque et E/S', value: (p) => p.ioReadBps + p.ioWriteBps, format: (v) => f.rate(v), max: (list) => Math.max(1, ...list.map((p) => p.ioReadBps + p.ioWriteBps)) },
  gpu: { label: 'Carte graphique', value: (p) => p.gpu, format: (v) => f.pct(v), max: () => 100 },
  vram: { label: 'Mémoire vidéo', value: (p) => p.vramMb, format: (v) => f.mb(v), max: (list) => vramTotalMb() ?? Math.max(1, ...list.map((p) => p.vramMb)) },
};

const clamp01 = (v) => Math.max(0, Math.min(1, v));

function shortLabel(def) {
  return def.key.startsWith('hw/') ? def.label.split(' · ').slice(1).join(' · ') || def.label : def.label;
}

function gauge(def) {
  if (def.unit === '%') return { max: def.max ?? 100 };
  if (def.unit === '°C') return { max: 100 };
  return null;
}

function level(key, def, v) {
  if (v == null) return '';
  if (key === 'wifi.signal') return v < 40 ? 'critical' : v < 60 ? 'warning' : '';
  if (def.unit === '°C') return v >= 85 ? 'critical' : v >= 75 ? 'warning' : '';
  return v >= 90 ? 'critical' : v >= 75 ? 'warning' : '';
}

function statSub(key, snap) {
  const m = snap.metrics;
  switch (key) {
    case 'cpu.total': return `Cœur le plus chargé : ${f.pct(m['cpu.maxcore'])}`;
    case 'mem.load': return `${f.value(m['mem.used'], 'Go')} sur ${f.value(store.info?.ramGb, 'Go')}`;
    case 'mem.commitpct': return `${f.value(m['mem.commit'], 'Go')} engagés`;
    case 'disk.active': return `Lecture ${f.rate(m['disk.read'])} · écriture ${f.rate(m['disk.write'])}`;
    case 'gpu.total': {
      const total = vramTotalMb();
      return `VRAM ${f.value(m['gpu.vram'], 'Go')}${total ? ` sur ${f.mb(total)}` : ''}`;
    }
    case 'wifi.signal': return snap.wifi ? `${snap.wifi.rssi ?? '–'} dBm · ${snap.wifi.ssid ?? snap.wifi.state}` : '';
    default:
      if (/^disk\.\d+\.active$/.test(key)) return `Temps de réponse ${f.value(m[key.replace('.active', '.latency')], 'ms')}`;
      if (key.startsWith('hw/')) return store.def(key).label.split(' · ')[0];
      return '';
  }
}

/** Verdict + constats, partagé par le widget d'accueil, l'onglet Diagnostic et l'Historique. */
export function renderDiagnosis(d, { limit = 99, scope = '' } = {}) {
  const findings = d.findings.filter((x) => x.severity !== 'ok' || d.findings.length === 1).slice(0, limit);
  const hidden = d.findings.filter((x) => x.severity !== 'ok').length - findings.filter((x) => x.severity !== 'ok').length;
  return `
    <div class="verdict">
      <span class="sev-icon">${severityIcon(d.severity, 22)}</span>
      <div>
        <h3>${f.esc(reveal(d.verdict))}</h3>
        <p>${scope || (d.live ? `Analyse des ${Math.round((d.to - d.from) / 60000)} dernières minutes · ${f.time(d.to)}` : `${f.dateTime(d.from)} → ${f.time(d.to)}`)}</p>
      </div>
    </div>
    <ul class="findings">
      ${findings.map((x) => `
        <li class="finding">
          ${severityIcon(x.severity, 18)}
          <div>
            <span class="sev-label sev-${x.severity}">${SEVERITY_LABEL[x.severity]}</span>
            <b>${f.esc(reveal(x.title))}</b>
            <span>${f.esc(reveal(x.detail))}</span>
          </div>
        </li>`).join('')}
    </ul>
    ${hidden > 0 ? `<div class="muted" style="margin-top:8px">+ ${hidden} autre${hidden > 1 ? 's' : ''} constat${hidden > 1 ? 's' : ''}</div>` : ''}`;
}

export function eventRow(e, { details = false } = {}) {
  const tone = e.level <= 2 ? 'sev-critical' : e.level === 3 ? 'sev-warning' : 'secondary';
  return `
    <div class="event">
      <span class="kind-dot ${tone}">${icon(KIND_ICONS[e.kind] ?? 'info', 14)}</span>
      <div>
        <b>${f.esc(reveal(e.title))}</b>
        <div class="muted">${f.esc(KIND_LABELS[e.kind] ?? e.kind)} · ${f.esc(e.provider)} · événement ${e.eventId}</div>
        ${details && e.message ? `<details><summary>Message de Windows</summary><pre>${f.esc(e.message)}</pre></details>` : ''}
      </div>
      <span class="when" title="${f.esc(f.dateTime(e.ts))}">${details ? f.time(e.ts) : f.ago(e.ts)}</span>
    </div>`;
}

export function processCell(p) {
  const exe = f.exeName(p);
  const sub = [exe !== f.appName(p) ? exe : null, p.via ? `via ${p.via}` : null, p.count > 1 ? `${p.count} processus` : null].filter(Boolean).join(' · ');
  return `<div class="app-name"><b title="${f.esc(exe)}">${f.esc(f.appName(p))}</b>${sub ? `<small>${f.esc(sub)}</small>` : ''}</div>`;
}

export const catalog = {
  diagnosis: {
    name: 'Pourquoi ça rame ?',
    description: 'Le verdict en direct : ce qui sature et quelle application en est responsable.',
    defaultSize: 'l',
    title: () => 'Pourquoi ça rame ?',
    link: { label: 'Diagnostic complet →', route: 'diagnostic' },
    create(body, params) {
      body.innerHTML = '<div class="empty">Analyse en cours…</div>';
      let alive = true;
      const run = async () => {
        try {
          const d = await api.diagnosis({ minutes: 2 });
          if (alive) body.innerHTML = renderDiagnosis(d, { limit: params.limit ?? 4 });
        } catch (e) {
          if (alive) body.innerHTML = `<div class="empty">Diagnostic indisponible : ${f.esc(e.message)}</div>`;
        }
      };
      run();
      const timer = setInterval(run, 10_000);
      return { destroy: () => { alive = false; clearInterval(timer); } };
    },
  },

  stat: {
    name: 'Chiffre clé',
    description: 'Une métrique en grand, avec jauge et tendance sur 5 minutes.',
    defaultSize: 's',
    needs: 'metric',
    title: (p) => store.def(p.metric).label,
    create(body, p) {
      const def = store.def(p.metric);
      const g = gauge(def);
      body.innerHTML = `
        <div class="stat-value num">–</div>
        <div class="stat-sub"></div>
        ${g ? '<div class="meter"><div style="width:0"></div></div>' : ''}
        <canvas class="spark"></canvas>`;
      const valueEl = body.querySelector('.stat-value');
      const subEl = body.querySelector('.stat-sub');
      const meter = body.querySelector('.meter');
      const spark = body.querySelector('.spark');
      store.seed([p.metric]);
      return {
        update(snap) {
          const v = snap.metrics[p.metric];
          valueEl.textContent = f.value(v, def.unit);
          subEl.textContent = statSub(p.metric, snap);
          if (meter && v != null) {
            meter.className = `meter ${level(p.metric, def, v)}`;
            meter.firstElementChild.style.width = `${clamp01(v / g.max) * 100}%`;
          }
          sparkline(spark, store.series(p.metric, 5), '--s1', g ? g.max : null);
        },
      };
    },
  },

  chart: {
    name: 'Courbe',
    description: "Jusqu'à 4 métriques de même unité, sur 5 à 30 minutes.",
    defaultSize: 'm',
    needs: 'metrics',
    title: (p) => p.title || p.metrics.map((k) => store.def(k).label).join(' · '),
    create(body, p) {
      const defs = p.metrics.map((k) => store.def(k));
      const unit = defs[0]?.unit ?? '';
      const max = defs.every((d) => d.max != null && d.max === defs[0].max) ? defs[0].max : null;
      const chart = new LineChart(body, {
        series: defs.map((d) => ({ key: d.key, label: shortLabel(d) })),
        unit,
        max,
        height: p.height ?? 170,
      });
      const minutes = p.minutes ?? 10;
      const refresh = () => {
        const now = store.latest?.ts ?? Date.now();
        const data = {};
        for (const k of p.metrics) data[k] = store.series(k, minutes);
        chart.setData(data, now - minutes * 60_000, now);
      };
      store.seed(p.metrics).then(refresh);
      return { update: refresh, destroy: () => chart.destroy() };
    },
  },

  top: {
    name: 'Plus gros consommateurs',
    description: 'Les applications qui utilisent le plus une ressource, en direct.',
    defaultSize: 's',
    needs: 'resource',
    title: (p) => `${RESOURCES[p.by]?.label ?? '?'} : qui consomme ?`,
    create(body, p) {
      const r = RESOURCES[p.by] ?? RESOURCES.cpu;
      const count = p.count ?? 5;
      return {
        update(snap) {
          const list = snap.processes.filter((x) => r.value(x) > 0).sort((a, b) => r.value(b) - r.value(a)).slice(0, count);
          if (list.length === 0) {
            body.innerHTML = '<div class="empty">Rien de notable en ce moment.</div>';
            return;
          }
          const max = Math.max(r.max(snap.processes), r.value(list[0]));
          body.innerHTML = `<ol class="toplist">${list.map((x) => `
            <li>
              <span class="name" title="${f.esc(f.exeName(x))}">${f.esc(f.appName(x))}${x.count > 1 ? ` <span class="muted">×${x.count}</span>` : ''}</span>
              <span class="val">${r.format(r.value(x))}</span>
              <div class="bar"><div style="width:${(clamp01(r.value(x) / max) * 100).toFixed(1)}%"></div></div>
            </li>`).join('')}</ol>`;
        },
      };
    },
  },

  hangs: {
    name: 'Fenêtres gelées',
    description: '« Ne répond pas » en direct et gels des dernières 24 heures.',
    defaultSize: 's',
    title: () => 'Fenêtres gelées',
    create(body) {
      let recent = [];
      const load = async () => {
        try {
          recent = (await api.events({ from: Date.now() - 24 * 3600_000, limit: 1 })).hangs;
        } catch {
          recent = [];
        }
      };
      load();
      const timer = setInterval(load, 30_000);
      return {
        update(snap) {
          let html = snap.hung.length
            ? `<ol class="toplist">${snap.hung.map((h) => `
                <li><span class="name sev-critical">${f.esc(reveal(h.process))}</span><span class="val">${f.duration((snap.ts - h.since) / 1000)}</span>
                <div class="muted" style="grid-column:1/-1">${f.esc(h.title)}</div></li>`).join('')}</ol>`
            : `<div class="secondary"><span class="sev-ok">${icon('ok', 16, 'style="vertical-align:-3px"')}</span> Aucune fenêtre gelée en ce moment.</div>`;
          const past = recent.filter((h) => h.end);
          html += `<div class="muted" style="margin-top:10px">${past.length
            ? `${past.length} gel${past.length > 1 ? 's' : ''} en 24 h. Dernier : ${f.esc(reveal(past[0].process))} (${f.duration((past[0].end - past[0].start) / 1000)}).`
            : 'Aucun gel enregistré depuis 24 h.'}</div>`;
          body.innerHTML = html;
        },
        destroy: () => clearInterval(timer),
      };
    },
  },

  events: {
    name: 'Signalements Windows',
    description: 'Plantages, arrêts brutaux, erreurs disque… des 7 derniers jours.',
    defaultSize: 'l',
    title: () => 'Signalements Windows (7 jours)',
    link: { label: 'Journal →', route: 'journal' },
    create(body, p) {
      body.innerHTML = '<div class="empty">Chargement…</div>';
      const load = async () => {
        try {
          const r = await api.events({ from: Date.now() - 7 * 86400_000, limit: p.count ?? 6 });
          body.innerHTML = r.events.length
            ? r.events.map((e) => eventRow(e)).join('')
            : "<div class=\"empty\">Aucun signalement : Windows n'a rien remonté cette semaine.</div>";
        } catch (e) {
          body.innerHTML = `<div class="empty">${f.esc(e.message)}</div>`;
        }
      };
      load();
      const timer = setInterval(load, 60_000);
      return { destroy: () => clearInterval(timer) };
    },
  },

  sensors: {
    name: 'Capteurs',
    description: 'Températures, ventilateurs, puissance, fréquences (LibreHardwareMonitor).',
    defaultSize: 'm',
    needs: 'sensorType',
    title: (p) => SENSOR_TYPES[p.type ?? 'Temperature'] ?? p.type,
    create(body, p) {
      const type = p.type ?? 'Temperature';
      return {
        update(snap) {
          const list = snap.sensors.filter((s) => s.type === type && (!p.hardware || s.hardwareType.startsWith(p.hardware)));
          if (list.length === 0) {
            body.innerHTML = `<div class="empty">${f.esc(store.hint('sensors') ?? 'Aucun capteur de ce type sur ce PC.')}</div>`;
            return;
          }
          body.innerHTML = `<div class="sensor-grid">${list.map((s) => `
            <div class="sensor ${type === 'Temperature' && s.value >= 85 ? 'hot' : ''}" title="${f.esc(s.hardware)}">
              <small>${f.esc(s.name)}</small><b>${f.esc(f.value(s.value, s.unit))}</b>
              <small>${f.esc(s.hardware)}</small>
            </div>`).join('')}</div>
            ${type === 'Temperature' && store.hint('sensors') ? `<div class="muted" style="margin-top:10px">${f.esc(store.hint('sensors'))}</div>` : ''}`;
        },
      };
    },
  },

  wifi: {
    name: 'Wi-Fi',
    description: 'Réseau, qualité du signal, canal et débit de liaison.',
    defaultSize: 's',
    title: () => 'Wi-Fi',
    create(body) {
      return {
        update(snap) {
          const w = snap.wifi;
          if (!w) {
            body.innerHTML = `<div class="empty">${f.esc(store.hint('wifi') ?? 'Pas de Wi-Fi sur ce PC.')}</div>`;
            return;
          }
          const q = w.signalQuality ?? 0;
          const bars = [10, 35, 60, 80].map((t, i) => `<i class="${w.signalQuality != null && q >= t ? 'on' : ''}" style="height:${7 + i * 5}px"></i>`).join('');
          body.innerHTML = `
            <div style="display:flex;align-items:center;gap:12px;margin-bottom:12px">
              <span class="bars" aria-hidden="true">${bars}</span>
              <div style="min-width:0">
                <div class="stat-value num" style="font-size:24px">${w.signalQuality != null ? f.pct(q) : '–'}</div>
                <div class="muted" style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${f.esc(w.ssid ?? w.state)}</div>
              </div>
            </div>
            <dl class="kv">
              <dt>Puissance</dt><dd>${w.rssi != null ? `${w.rssi} dBm` : '–'}</dd>
              <dt>Canal</dt><dd>${w.channel ?? '–'}${w.band ? ` · ${w.band}` : ''}</dd>
              <dt>Liaison</dt><dd>↓ ${w.rxMbps != null ? Math.round(w.rxMbps) : '–'} · ↑ ${w.txMbps != null ? Math.round(w.txMbps) : '–'} Mb/s</dd>
            </dl>
            ${store.hint('wifi') ? `<div class="muted" style="margin-top:10px">${f.esc(store.hint('wifi'))}</div>` : ''}`;
        },
      };
    },
  },

  machine: {
    name: 'Cette machine',
    description: 'Processeur, mémoire, carte graphique, système et durée depuis le démarrage.',
    defaultSize: 's',
    title: () => 'Cette machine',
    create(body) {
      return {
        update() {
          const i = store.info;
          body.innerHTML = `
            <dl class="kv" style="grid-template-columns:auto minmax(0,1fr)">
              <dt>Processeur</dt><dd>${f.esc(i.cpu)}</dd>
              <dt>Mémoire</dt><dd>${f.esc(f.value(i.ramGb, 'Go'))}</dd>
              <dt>Graphique</dt><dd>${f.esc(i.gpus.join(', ') || '–')}</dd>
              <dt>Système</dt><dd>${f.esc(i.os)}</dd>
              <dt>Allumé depuis</dt><dd>${f.duration((Date.now() - i.bootTime) / 1000)}</dd>
              <dt>Agent</dt><dd>v${f.esc(i.agentVersion)} · ${i.administrator ? 'administrateur' : 'sans droits admin'}</dd>
            </dl>`;
        },
      };
    },
  },

  processes: {
    name: 'Tableau des applications',
    description: 'Toutes les applications en cours, triables par ressource.',
    defaultSize: 'l',
    title: () => 'Applications en cours',
    create(body, p) {
      const columns = [
        { id: 'name', label: 'Application' },
        { id: 'cpu', label: 'Processeur', r: true },
        { id: 'ram', label: 'Mémoire', r: true },
        { id: 'io', label: 'Disque / E/S', r: true },
        { id: 'gpu', label: 'GPU', r: true },
        { id: 'vram', label: 'VRAM', r: true },
      ];
      let sort = p.sort ?? 'cpu';
      let filter = '';
      let last = null;
      body.innerHTML = `
        <div class="toolbar">
          <input class="search" type="search" placeholder="Filtrer : chrome, steam, svchost…" aria-label="Filtrer les applications">
          <span class="muted count"></span>
        </div>
        <div class="table-wrap"><table class="data"><thead><tr></tr></thead><tbody></tbody></table></div>
        <p class="muted" style="margin:10px 0 0;font-size:12px">« Disque / E/S » compte toutes les lectures et écritures du processus, réseau compris. Les processus WebView2 sont rattachés à l'application qui les a lancés.</p>`;
      const head = body.querySelector('thead tr');
      const tbody = body.querySelector('tbody');
      const countEl = body.querySelector('.count');
      const renderHead = () => {
        head.innerHTML = columns.map((c) => `<th class="sortable ${c.r ? 'r' : ''}" data-col="${c.id}" ${c.id === sort ? `aria-sort="${c.id === 'name' ? 'ascending' : 'descending'}"` : ''}>${c.label}${c.id === sort ? ' ↓' : ''}</th>`).join('');
      };
      head.addEventListener('click', (e) => {
        const th = e.target.closest('th[data-col]');
        if (!th) return;
        sort = th.dataset.col;
        renderHead();
        if (last) render(last);
      });
      body.querySelector('.search').addEventListener('input', (e) => {
        filter = e.target.value.trim().toLowerCase();
        if (last) render(last);
      });
      renderHead();

      const render = (snap) => {
        last = snap;
        let list = snap.processes;
        if (filter) list = list.filter((x) => x.name.toLowerCase().includes(filter) || (x.description ?? '').toLowerCase().includes(filter));
        if (sort === 'name') list = [...list].sort((a, b) => f.appName(a).localeCompare(f.appName(b), 'fr'));
        else list = [...list].sort((a, b) => RESOURCES[sort].value(b) - RESOURCES[sort].value(a));
        const total = list.length;
        list = list.slice(0, filter ? 200 : 60);
        countEl.textContent = `${total} application${total > 1 ? 's' : ''}${total > list.length ? ` · ${list.length} affichées` : ''}`;
        const max = sort !== 'name' ? Math.max(RESOURCES[sort].max(snap.processes), ...list.map((x) => RESOURCES[sort].value(x))) : 1;
        const cell = (x, id) => {
          const r = RESOURCES[id];
          const v = r.value(x);
          const text = v > 0 || id === 'ram' ? r.format(v) : '<span class="muted">–</span>';
          if (id !== sort) return `<td class="r">${text}</td>`;
          return `<td class="r"><span class="bar-cell"><span class="mini-bar"><div style="width:${(clamp01(v / max) * 100).toFixed(1)}%"></div></span>${text}</span></td>`;
        };
        tbody.innerHTML = list.map((x) => `<tr><td>${processCell(x)}</td>${['cpu', 'ram', 'io', 'gpu', 'vram'].map((id) => cell(x, id)).join('')}</tr>`).join('');
      };
      return { update: render };
    },
  },
};

/** Crée une carte (en-tête + corps) et y monte un widget. */
export function mountWidget(item, ctx) {
  const spec = catalog[item.type];
  const card = document.createElement('section');
  card.className = `card span-${item.size || spec?.defaultSize || 'm'}`;
  if (!spec) {
    card.innerHTML = `<div class="empty">Widget inconnu : ${f.esc(item.type)}</div>`;
    return { card, instance: {} };
  }
  const params = item.params || {};
  const link = spec.link;
  card.innerHTML = `
    <div class="card-head">
      <h2>${f.esc(params.title || spec.title(params))}</h2>
      ${link ? `<button class="link" data-route="${link.route}">${link.label}</button>` : ''}
    </div>
    <div class="card-body"></div>`;
  card.querySelector('[data-route]')?.addEventListener('click', (e) => ctx.navigate(e.currentTarget.dataset.route));
  const body = card.querySelector('.card-body');
  let instance = {};
  try {
    instance = spec.create(body, params, ctx) || {};
  } catch (e) {
    body.innerHTML = `<div class="empty">Erreur : ${f.esc(e.message)}</div>`;
  }
  return { card, instance };
}
