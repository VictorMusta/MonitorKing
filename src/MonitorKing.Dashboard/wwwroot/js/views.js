// Onglets du dashboard. Chaque vue renvoie { update(snapshot), destroy() }.
import { api } from './api.js';
import { store } from './store.js';
import { LineChart, cssVar } from './chart.js';
import * as f from './format.js';
import { icon, severityIcon } from './icons.js';
import { catalog, mountWidget, renderDiagnosis, eventRow, processCell, RESOURCES, KIND_LABELS, SENSOR_TYPES } from './widgets.js';
import { copyReport, downloadReport } from './report.js';
import * as names from './names.js';

const GROUP_LABELS = { cpu: 'Processeur', memory: 'Mémoire', disk: 'Disques', gpu: 'Carte graphique', network: 'Réseau', sensors: 'Capteurs' };

const HARDWARE_LABELS = {
  Cpu: 'Processeur', GpuAmd: 'Carte graphique', GpuNvidia: 'Carte graphique', GpuIntel: 'Carte graphique',
  Storage: 'Stockage', Motherboard: 'Carte mère', Memory: 'Mémoire', Cooler: 'Refroidissement',
  Battery: 'Batterie', Network: 'Réseau', Psu: 'Alimentation',
};

function head(root, title, subtitle, actions = '') {
  const el = document.createElement('div');
  el.className = 'view-head';
  el.innerHTML = `<div><h1>${title}</h1>${subtitle ? `<p>${subtitle}</p>` : ''}</div><div class="actions">${actions}</div>`;
  root.appendChild(el);
  return el;
}

function banner(root, text) {
  if (!text) return null;
  const el = document.createElement('div');
  el.className = 'banner';
  el.innerHTML = `${icon('info', 16)}<div>${f.esc(text)}</div>`;
  root.appendChild(el);
  return el;
}

function grid(root, items, ctx) {
  const el = document.createElement('div');
  el.className = 'grid';
  root.appendChild(el);
  const mounted = items.map((item) => {
    const m = mountWidget(item, ctx);
    el.appendChild(m.card);
    return m;
  });
  return {
    el,
    mounted,
    update(snap) {
      for (const m of mounted) {
        try {
          m.instance.update?.(snap);
        } catch (e) {
          console.error(e);
        }
      }
    },
    destroy() {
      for (const m of mounted) m.instance.destroy?.();
    },
  };
}

function combine(...parts) {
  return {
    update: (snap) => parts.forEach((p) => p?.update?.(snap)),
    destroy: () => parts.forEach((p) => p?.destroy?.()),
  };
}

const keysMatching = (re) => [...store.defs.keys()].filter((k) => re.test(k)).sort();

// ---------------------------------------------------------------- Accueil (personnalisable)

export const DEFAULT_HOME = {
  version: 1,
  items: [
    { type: 'diagnosis', size: 'l' },
    { type: 'stat', size: 's', params: { metric: 'cpu.total' } },
    { type: 'stat', size: 's', params: { metric: 'mem.load' } },
    { type: 'stat', size: 's', params: { metric: 'disk.active' } },
    { type: 'stat', size: 's', params: { metric: 'gpu.total' } },
    { type: 'chart', size: 'm', params: { metrics: ['cpu.total', 'mem.load', 'disk.active', 'gpu.total'], minutes: 10, title: 'Charge du PC · 10 dernières minutes' } },
    { type: 'top', size: 's', params: { by: 'ram' } },
    { type: 'top', size: 's', params: { by: 'cpu' } },
    { type: 'hangs', size: 's' },
    { type: 'wifi', size: 's' },
    { type: 'sensors', size: 'm', params: { type: 'Temperature' } },
    { type: 'events', size: 'l', params: { count: 5 } },
  ],
};

const newId = () => (crypto.randomUUID ? crypto.randomUUID() : String(Math.random()).slice(2));

export function home(root, ctx) {
  let layout = null;
  let editing = false;
  let current = null;
  const header = head(root, 'Accueil', 'Ton écran de surveillance. Personnalise-le avec les widgets de ton choix.',
    `<button class="btn" data-act="edit">${icon('edit', 14)} Personnaliser</button>`);
  const container = document.createElement('div');
  root.appendChild(container);

  const save = async () => {
    try {
      await api.saveLayout('home', { version: 1, items: layout.items.map(({ id, ...rest }) => rest) });
    } catch (e) {
      ctx.toast(e.message);
    }
  };

  const renderActions = () => {
    header.querySelector('.actions').innerHTML = editing
      ? `<button class="btn" data-act="add">${icon('plus', 14)} Ajouter un widget</button>
         <button class="btn ghost" data-act="reset">Réinitialiser</button>
         <button class="btn primary" data-act="done">Terminé</button>`
      : `<button class="btn" data-act="edit">${icon('edit', 14)} Personnaliser</button>`;
  };

  const render = () => {
    current?.destroy();
    container.innerHTML = '';
    container.classList.toggle('editing', editing);
    current = grid(container, layout.items, ctx);
    current.mounted.forEach((m, index) => {
      const bar = document.createElement('div');
      bar.className = 'edit-bar';
      bar.innerHTML = `
        <button title="Déplacer avant" data-edit="left">◀</button>
        <button title="Déplacer après" data-edit="right">▶</button>
        <button title="Changer la taille" data-edit="size">${(layout.items[index].size || 'm').toUpperCase()}</button>
        <button title="Retirer" data-edit="remove">✕</button>`;
      bar.addEventListener('click', (e) => {
        const action = e.target.closest('[data-edit]')?.dataset.edit;
        if (!action) return;
        const items = layout.items;
        if (action === 'left' && index > 0) [items[index - 1], items[index]] = [items[index], items[index - 1]];
        if (action === 'right' && index < items.length - 1) [items[index + 1], items[index]] = [items[index], items[index + 1]];
        if (action === 'size') items[index].size = { s: 'm', m: 'l', l: 's' }[items[index].size || 'm'];
        if (action === 'remove') items.splice(index, 1);
        save();
        render();
      });
      m.card.appendChild(bar);
    });
    if (store.latest) current.update(store.latest);
  };

  header.addEventListener('click', async (e) => {
    const action = e.target.closest('[data-act]')?.dataset.act;
    if (action === 'edit' || action === 'done') {
      editing = action === 'edit';
      renderActions();
      render();
    } else if (action === 'add') {
      const item = await ctx.pickWidget();
      if (item) {
        layout.items.push({ id: newId(), ...item });
        save();
        render();
      }
    } else if (action === 'reset') {
      layout = structuredClone(DEFAULT_HOME);
      layout.items.forEach((i) => (i.id = newId()));
      save();
      render();
    }
  });

  (async () => {
    const saved = await api.layout('home').catch(() => null);
    layout = saved?.items?.length ? saved : structuredClone(DEFAULT_HOME);
    layout.items.forEach((i) => (i.id = newId()));
    render();
  })();

  return {
    update: (snap) => current?.update(snap),
    destroy: () => current?.destroy(),
  };
}

// ---------------------------------------------------------------- Diagnostic

export function diagnostic(root, ctx) {
  let minutes = 2;
  let alive = true;
  const header = head(root, 'Pourquoi ça rame ?',
    'Ce qui sature en ce moment, qui en est responsable, et ce que Windows a signalé à côté.',
    `<div class="segmented" role="group" aria-label="Fenêtre d'analyse">
       ${[1, 2, 5].map((m) => `<button data-min="${m}" aria-pressed="${m === minutes}">${m} min</button>`).join('')}
     </div>
     <button class="btn" data-act="run">${icon('refresh', 14)} Relancer</button>
     <span class="report-tools">
       <select data-report-range aria-label="Période du rapport">
         <option value="15">15 dernières min</option>
         <option value="60" selected>Dernière heure</option>
         <option value="360">6 dernières heures</option>
         <option value="1440">24 dernières heures</option>
       </select>
       <button class="btn primary" data-act="copy-report">Copier le rapport</button>
       <button class="btn" data-act="download-report" title="Télécharger le rapport (.md)">.md</button>
     </span>`);
  const hero = document.createElement('section');
  hero.className = 'card diag-hero';
  hero.innerHTML = '<div class="empty">Analyse en cours…</div>';
  root.appendChild(hero);

  const title = document.createElement('div');
  title.className = 'view-head';
  title.style.marginTop = '24px';
  title.innerHTML = `<div><h1 style="font-size:16px">Qui consomme quoi</h1><p>Moyenne par application sur la fenêtre analysée.</p></div>`;
  root.appendChild(title);
  const cols = document.createElement('div');
  cols.className = 'culprit-cols';
  root.appendChild(cols);
  const tip = document.createElement('p');
  tip.className = 'muted';
  tip.style.marginTop = '20px';
  tip.innerHTML = `Le ralentissement est déjà passé ? Ouvre l'<a href="#historique">Historique</a> et sélectionne la période à la souris.`;
  root.appendChild(tip);

  const columns = [
    { id: 'cpu', label: 'Processeur', value: (p) => p.cpu, format: (v) => f.pct(v) },
    { id: 'memory', label: 'Mémoire vive', value: (p) => p.ramMb, format: (v) => f.mb(v) },
    { id: 'disk', label: 'Disque et E/S', value: (p) => p.ioBps, format: (v) => f.rate(v) },
    { id: 'gpu', label: 'Carte graphique', value: (p) => p.gpu, format: (v) => f.pct(v) },
  ];

  const run = async () => {
    try {
      const d = await api.diagnosis({ minutes });
      if (!alive) return;
      hero.innerHTML = renderDiagnosis(d);
      cols.innerHTML = columns.map((c) => {
        const list = d.culprits[c.id] || [];
        const max = Math.max(1e-9, ...list.map(c.value));
        return `<section class="card"><div class="card-head"><h2>${c.label}</h2></div>${list.length
          ? `<ol class="toplist">${list.map((p) => `<li><span class="name" title="${f.esc(p.name)}">${f.esc(f.appName(p))}</span><span class="val">${c.format(c.value(p))}</span><div class="bar"><div style="width:${(c.value(p) / max * 100).toFixed(1)}%"></div></div></li>`).join('')}</ol>`
          : '<div class="empty">Rien de notable.</div>'}</section>`;
      }).join('');
    } catch (e) {
      hero.innerHTML = `<div class="empty">Diagnostic indisponible : ${f.esc(e.message)}</div>`;
    }
  };

  header.addEventListener('click', (e) => {
    const m = e.target.closest('[data-min]');
    if (m) {
      minutes = Number(m.dataset.min);
      header.querySelectorAll('[data-min]').forEach((b) => b.setAttribute('aria-pressed', String(b === m)));
      run();
    }
    if (e.target.closest('[data-act="run"]')) run();
    const report = e.target.closest('[data-act$="-report"]')?.dataset.act;
    if (report) {
      const params = { minutes: Number(header.querySelector('[data-report-range]').value) };
      (report === 'copy-report' ? copyReport : downloadReport)(params, ctx.toast).catch((err) => ctx.toast(err.message));
    }
  });
  run();
  const timer = setInterval(run, 10_000);
  return { destroy: () => { alive = false; clearInterval(timer); } };
}

// ---------------------------------------------------------------- Applications

export function applications(root, ctx) {
  head(root, 'Applications', 'Qui utilise le processeur, la mémoire, le disque et la carte graphique, en direct. Clique sur une colonne pour trier.');
  return grid(root, [{ type: 'processes', size: 'l' }], ctx);
}

// ---------------------------------------------------------------- Système

export function system(root, ctx) {
  head(root, 'Processeur, mémoire et disques', 'En direct et sur les 30 dernières minutes.');
  const disks = keysMatching(/^disk\.\d+\.active$/);
  const latencies = keysMatching(/^disk\.\d+\.latency$/);
  return grid(root, [
    { type: 'stat', size: 's', params: { metric: 'cpu.total' } },
    { type: 'stat', size: 's', params: { metric: 'mem.load' } },
    { type: 'stat', size: 's', params: { metric: 'mem.commitpct' } },
    { type: 'stat', size: 's', params: { metric: 'disk.active' } },
    { type: 'chart', size: 'm', params: { metrics: ['cpu.total', 'cpu.maxcore'], minutes: 30, title: 'Processeur' } },
    { type: 'chart', size: 'm', params: { metrics: ['mem.load', 'mem.commitpct'], minutes: 30, title: 'Mémoire' } },
    { type: 'chart', size: 'm', params: { metrics: disks.length ? disks : ['disk.active'], minutes: 30, title: 'Activité de chaque disque' } },
    { type: 'chart', size: 'm', params: { metrics: ['disk.read', 'disk.write'], minutes: 30, title: 'Débit disque' } },
    { type: 'chart', size: 'm', params: { metrics: latencies.length ? latencies : ['disk.latency'], minutes: 30, title: 'Temps de réponse de chaque disque (SSD : < 5 ms, disque dur : < 20 ms)' } },
    { type: 'chart', size: 'm', params: { metrics: ['mem.hardfaults'], minutes: 30, title: 'Pages relues sur le disque (signe de mémoire saturée)' } },
    { type: 'chart', size: 'm', params: { metrics: ['cpu.dpc'], minutes: 30, title: 'Temps passé dans les pilotes (DPC + interruptions)' } },
    { type: 'top', size: 's', params: { by: 'cpu' } },
    { type: 'top', size: 's', params: { by: 'ram' } },
    { type: 'top', size: 'm', params: { by: 'io', count: 5 } },
  ], ctx);
}

// ---------------------------------------------------------------- GPU

export function gpu(root, ctx) {
  head(root, 'Carte graphique', 'Charge par moteur (3D, vidéo, calcul), mémoire vidéo, températures et applications.');
  const temps = keysMatching(/^hw\/gpu[^/]*\/\d+\/temperature\//);
  const items = [
    { type: 'stat', size: 's', params: { metric: 'gpu.total' } },
    { type: 'stat', size: 's', params: { metric: 'gpu.vram' } },
    ...temps.slice(0, 2).map((k) => ({ type: 'stat', size: 's', params: { metric: k } })),
    { type: 'chart', size: 'm', params: { metrics: ['gpu.3d', 'gpu.video', 'gpu.compute'], minutes: 30, title: 'Charge par moteur' } },
    temps.length
      ? { type: 'chart', size: 'm', params: { metrics: temps.slice(0, 4), minutes: 30, title: 'Températures GPU' } }
      : { type: 'chart', size: 'm', params: { metrics: ['gpu.vram'], minutes: 30, title: 'Mémoire vidéo utilisée' } },
    { type: 'top', size: 's', params: { by: 'gpu' } },
    { type: 'top', size: 's', params: { by: 'vram' } },
    { type: 'sensors', size: 'm', params: { type: 'Power', hardware: 'Gpu' } },
    { type: 'sensors', size: 'm', params: { type: 'Fan', hardware: 'Gpu' } },
    { type: 'sensors', size: 'm', params: { type: 'Clock', hardware: 'Gpu' } },
  ];
  return grid(root, items, ctx);
}

// ---------------------------------------------------------------- Capteurs

export function sensors(root, ctx) {
  head(root, 'Capteurs', 'Tout ce que LibreHardwareMonitor arrive à lire sur ce PC, par composant.');
  const hint = store.hint('sensors');
  banner(root, hint);
  const temps = keysMatching(/\/temperature\//);
  const charts = temps.length
    ? grid(root, [{ type: 'chart', size: 'l', params: { metrics: temps.slice(0, 4), minutes: 30, title: 'Températures · 30 dernières minutes', height: 190 } }], ctx)
    : null;
  const list = document.createElement('div');
  list.className = 'grid';
  list.style.marginTop = '16px';
  root.appendChild(list);
  const order = ['Temperature', 'Fan', 'Control', 'Power', 'Load', 'Clock', 'SmallData', 'Data', 'Voltage', 'Level'];
  const render = (snap) => {
    const byHardware = new Map();
    for (const s of snap.sensors) {
      if (!byHardware.has(s.hardware)) byHardware.set(s.hardware, []);
      byHardware.get(s.hardware).push(s);
    }
    if (byHardware.size === 0) {
      list.innerHTML = '<section class="card span-l"><div class="empty">Aucun capteur lisible.</div></section>';
      return;
    }
    list.innerHTML = [...byHardware.entries()].map(([hardware, items]) => {
      const types = order.filter((t) => items.some((s) => s.type === t));
      const kind = HARDWARE_LABELS[items[0].hardwareType] ?? items[0].hardwareType;
      return `<section class="card span-l">
        <div class="hw-title">${f.esc(hardware)} <small>${f.esc(kind)}${items[0].chip ? ` · mesuré par la puce ${f.esc(items[0].chip)}` : ''}</small></div>
        ${types.map((t) => `
          <div class="type-title">${SENSOR_TYPES[t] ?? t}</div>
          <div class="sensor-grid">${items.filter((s) => s.type === t).map((s) => `
            <div class="sensor ${t === 'Temperature' && s.value >= 85 ? 'hot' : ''}"><small>${f.esc(s.name)}</small><b>${f.esc(f.value(s.value, s.unit))}</b></div>`).join('')}
          </div>`).join('')}
      </section>`;
    }).join('');
  };
  return combine(charts, { update: render });
}

// ---------------------------------------------------------------- Réseau

export function network(root, ctx) {
  head(root, 'Réseau', 'Débit de la connexion et qualité du Wi-Fi.');
  banner(root, store.hint('wifi'));
  return grid(root, [
    { type: 'wifi', size: 's' },
    { type: 'stat', size: 's', params: { metric: 'wifi.signal' } },
    { type: 'chart', size: 'm', params: { metrics: ['net.down', 'net.up'], minutes: 30, title: 'Débit réseau' } },
    { type: 'chart', size: 'm', params: { metrics: ['wifi.signal'], minutes: 30, title: 'Qualité du signal Wi-Fi' } },
    { type: 'chart', size: 'm', params: { metrics: ['wifi.rx', 'wifi.tx'], minutes: 30, title: 'Débit de liaison Wi-Fi (négocié avec la box)' } },
  ], ctx);
}

// ---------------------------------------------------------------- Journal

export function journal(root, ctx) {
  let days = 7;
  let kind = 'all';
  let items = [];
  let alive = true;
  const header = head(root, 'Journal', "Ce que Windows a signalé (plantages, gels, arrêts brutaux, erreurs disque, bridage…) et les gels détectés par l'agent.",
    `<div class="segmented" role="group" aria-label="Période">${[1, 7, 30].map((d) => `<button data-days="${d}" aria-pressed="${d === days}">${d === 1 ? '24 h' : `${d} jours`}</button>`).join('')}</div>`);
  banner(root, store.hint('events'));
  const chips = document.createElement('div');
  chips.className = 'chips';
  chips.style.marginBottom = '8px';
  root.appendChild(chips);
  const card = document.createElement('section');
  card.className = 'card';
  card.innerHTML = '<div class="empty">Chargement…</div>';
  root.appendChild(card);

  const render = () => {
    const kinds = [...new Set(items.map((e) => e.kind))];
    chips.innerHTML = [`<button class="chip" data-kind="all" aria-pressed="${kind === 'all'}">Tout (${items.length})</button>`,
      ...kinds.map((k) => `<button class="chip" data-kind="${k}" aria-pressed="${kind === k}">${f.esc(KIND_LABELS[k] ?? k)} (${items.filter((e) => e.kind === k).length})</button>`)].join('');
    const list = kind === 'all' ? items : items.filter((e) => e.kind === kind);
    if (list.length === 0) {
      card.innerHTML = '<div class="empty">Rien à signaler sur cette période.</div>';
      return;
    }
    let html = '';
    let lastDay = '';
    for (const e of list) {
      const d = f.day(e.ts);
      if (d !== lastDay) {
        html += `<div class="day">${f.esc(d)}</div>`;
        lastDay = d;
      }
      html += eventRow(e, { details: true });
    }
    card.innerHTML = html;
  };

  const load = async () => {
    try {
      const r = await api.events({ from: Date.now() - days * 86400_000, limit: 2000 });
      if (!alive) return;
      const hangs = r.hangs.map((h) => ({
        ts: h.start,
        kind: 'hang',
        level: 3,
        provider: 'MonitorKing',
        eventId: '—',
        title: `Ne répondait plus : ${h.process}${h.end ? ` pendant ${f.duration((h.end - h.start) / 1000)}` : ' (durée inconnue)'}`,
        message: h.title ? `Fenêtre : ${h.title}` : null,
      }));
      items = [...r.events, ...hangs].sort((a, b) => b.ts - a.ts);
      render();
    } catch (e) {
      card.innerHTML = `<div class="empty">${f.esc(e.message)}</div>`;
    }
  };

  header.addEventListener('click', (e) => {
    const b = e.target.closest('[data-days]');
    if (!b) return;
    days = Number(b.dataset.days);
    header.querySelectorAll('[data-days]').forEach((x) => x.setAttribute('aria-pressed', String(x === b)));
    load();
  });
  chips.addEventListener('click', (e) => {
    const b = e.target.closest('[data-kind]');
    if (!b) return;
    kind = b.dataset.kind;
    render();
  });
  load();
  const timer = setInterval(load, 60_000);
  return { destroy: () => { alive = false; clearInterval(timer); } };
}

// ---------------------------------------------------------------- Historique

export function history(root, ctx) {
  let hours = 1;
  let selection = null;
  let alive = true;
  const header = head(root, 'Historique', 'Que s’est-il passé ? Sélectionne une période sur le graphique (cliquer-glisser) pour l’analyser.',
    `<div class="segmented" role="group" aria-label="Période">${[[1, '1 h'], [6, '6 h'], [24, '24 h'], [168, '7 jours']].map(([h, l]) => `<button data-hours="${h}" aria-pressed="${h === hours}">${l}</button>`).join('')}</div>`);

  const card = document.createElement('section');
  card.className = 'card';
  card.innerHTML = '<div class="card-head"><h2>Charge du PC</h2><span class="meta"></span></div><div class="plot"></div><div class="selection-bar"></div>';
  root.appendChild(card);
  const keys = ['cpu.total', 'mem.load', 'disk.active', 'gpu.total'];
  const chart = new LineChart(card.querySelector('.plot'), {
    series: keys.map((k) => ({ key: k, label: store.def(k).label })),
    unit: '%',
    max: 100,
    height: 240,
    selectable: true,
    showLastDot: false,
    onSelect: (s) => {
      selection = s;
      renderSelection();
    },
  });

  const tempKeys = keysMatching(/\/temperature\//).slice(0, 4);
  let tempChart = null;
  if (tempKeys.length) {
    const tempCard = document.createElement('section');
    tempCard.className = 'card';
    tempCard.style.marginTop = '16px';
    tempCard.innerHTML = '<div class="card-head"><h2>Températures</h2></div><div class="plot"></div>';
    root.appendChild(tempCard);
    tempChart = new LineChart(tempCard.querySelector('.plot'), {
      series: tempKeys.map((k) => ({ key: k, label: store.def(k).label.split(' · ').slice(1).join(' · ') || store.def(k).label })),
      unit: '°C',
      height: 160,
      showLastDot: false,
    });
  }

  const result = document.createElement('div');
  result.style.marginTop = '16px';
  root.appendChild(result);

  const bar = card.querySelector('.selection-bar');
  const renderSelection = () => {
    if (!selection) {
      bar.innerHTML = `<span class="muted">Astuce : clique-glisse sur le graphique pour choisir la période d’un ralentissement.</span>
        <button class="btn small" data-act="copy-range">Copier le rapport de la période affichée</button>`;
      return;
    }
    bar.innerHTML = `
      <span><b>${f.esc(f.dateTime(selection.from))}</b> → <b>${f.esc(f.time(selection.to))}</b> <span class="muted">(${f.duration((selection.to - selection.from) / 1000)})</span></span>
      <button class="btn primary small" data-act="analyze">Analyser cette période</button>
      <button class="btn small" data-act="copy-selection">Copier le rapport</button>
      <button class="btn small" data-act="download-selection" title="Télécharger le rapport (.md)">.md</button>
      <button class="btn ghost small" data-act="clear">Effacer</button>`;
  };
  bar.addEventListener('click', async (e) => {
    const action = e.target.closest('[data-act]')?.dataset.act;
    const failed = (err) => ctx.toast(err.message);
    if (action === 'copy-range') copyReport({ from: Math.round(chart.from), to: Math.round(chart.to) }, ctx.toast).catch(failed);
    if (action === 'copy-selection' && selection) copyReport({ from: selection.from, to: selection.to }, ctx.toast).catch(failed);
    if (action === 'download-selection' && selection) downloadReport({ from: selection.from, to: selection.to }, ctx.toast).catch(failed);
    if (action === 'clear') {
      selection = null;
      chart.setSelection(null);
      result.innerHTML = '';
      renderSelection();
    }
    if (action === 'analyze' && selection) analyze(selection);
  });

  // Le coupable d'un pic n'est pas forcément l'application qui consomme le plus : c'est celle dont l'activité
  // augmente pendant le pic. On compare donc la sélection à la moyenne de toute la période affichée.
  const COLUMNS = [
    { id: 'cpu', label: 'Processeur', value: (p) => p.cpu, format: (v) => f.pct(v) },
    { id: 'ram', label: 'Mémoire (pic)', value: (p) => p.ramMb, format: (v) => f.mb(v) },
    { id: 'io', label: 'Disque / E/S', value: (p) => p.ioReadBps + p.ioWriteBps, format: (v) => f.rate(v) },
    { id: 'rise', label: 'Hausse E/S', value: (p) => p.rise, format: (v) => (v >= 2 ? `×${v >= 10 ? Math.round(v) : v.toFixed(1).replace('.', ',')}` : '–') },
    { id: 'gpu', label: 'GPU', value: (p) => p.gpu, format: (v) => (v > 0 ? f.pct(v) : '–') },
  ];
  const MIN_IO = 20 * 1024; // en dessous, une « hausse » n'a pas de sens

  const renderPeriod = (d, rows, sort, s) => {
    const col = COLUMNS.find((c) => c.id === sort);
    const top = [...rows].sort((a, b) => col.value(b) - col.value(a)).slice(0, 15);
    result.innerHTML = `
      <section class="card diag-hero">${renderDiagnosis(d, { scope: `Période analysée : ${f.dateTime(s.from)} → ${f.time(s.to)}` })}</section>
      <section class="card" style="margin-top:16px">
        <div class="card-head"><h2>Applications sur cette période</h2><span class="meta">clique sur une colonne pour trier</span></div>
        ${top.length ? `<div class="table-wrap"><table class="data"><thead><tr><th>Application</th>${COLUMNS.map((c) => `<th class="r sortable" data-sort="${c.id}" ${c.id === sort ? 'aria-sort="descending"' : ''}>${c.label}${c.id === sort ? ' ↓' : ''}</th>`).join('')}</tr></thead>
        <tbody>${top.map((p) => `<tr><td>${processCell(p)}</td>${COLUMNS.map((c) => `<td class="r">${c.id === 'rise' && p.rise >= 3 ? `<b class="sev-warning">${c.format(c.value(p))}</b>` : c.format(c.value(p))}</td>`).join('')}</tr>`).join('')}</tbody></table></div>
        <p class="muted" style="margin:10px 0 0;font-size:12px">« Hausse E/S » compare les lectures/écritures de chaque application pendant la sélection à sa moyenne sur toute la période affichée. Pour un pic qui revient régulièrement, le coupable est celle qui grimpe pendant le pic, pas forcément celle qui en fait le plus en continu.</p>`
        : '<div class="empty">Pas de données applications sur cette période (agent arrêté ?).</div>'}
      </section>`;
    result.querySelector('thead')?.addEventListener('click', (e) => {
      const th = e.target.closest('[data-sort]');
      if (th) renderPeriod(d, rows, th.dataset.sort, s);
    });
  };

  const analyze = async (s) => {
    result.innerHTML = '<section class="card"><div class="empty">Analyse de la période…</div></section>';
    try {
      const [d, procs, base] = await Promise.all([
        api.diagnosis({ from: s.from, to: s.to }),
        api.processes(s.from, s.to),
        api.processes(chart.from, chart.to),
      ]);
      const baseline = new Map(base.map((p) => [p.name, p.ioReadBps + p.ioWriteBps]));
      const rows = procs.map((p) => {
        const io = p.ioReadBps + p.ioWriteBps;
        return { ...p, rise: io >= MIN_IO ? io / Math.max(baseline.get(p.name) ?? 0, 1024) : 0 };
      });
      const diskIssue = d.findings.some((x) => x.resource === 'disk');
      const rising = rows.some((r) => r.rise >= 2);
      renderPeriod(d, rows, rising ? 'rise' : diskIssue ? 'io' : 'cpu', s);
    } catch (e) {
      result.innerHTML = `<section class="card"><div class="empty">${f.esc(e.message)}</div></section>`;
    }
  };

  const load = async () => {
    const to = Date.now();
    const from = to - hours * 3600_000;
    try {
      const [series, events, temps] = await Promise.all([
        api.series(keys, { from, to, points: 500 }),
        api.events({ from, to, limit: 500 }),
        tempKeys.length ? api.series(tempKeys, { from, to, points: 400 }) : null,
      ]);
      if (!alive) return;
      chart.setData(series.series, from, to);
      const colors = { crash: cssVar('--critical'), bsod: cssVar('--critical'), power: cssVar('--critical'), hang: cssVar('--serious'), disk: cssVar('--critical') };
      chart.setMarkers([
        ...events.events.filter((e) => e.kind !== 'boot').map((e) => ({ ts: e.ts, color: colors[e.kind] ?? cssVar('--warning'), label: e.title })),
        ...events.hangs.map((h) => ({ ts: h.start, color: cssVar('--serious'), label: `Gel : ${h.process}` })),
      ]);
      card.querySelector('.meta').textContent = `${events.events.length + events.hangs.length} signalement(s) sur la période`;
      if (tempChart && temps) tempChart.setData(temps.series, from, to);
    } catch (e) {
      card.querySelector('.meta').textContent = e.message;
    }
  };

  header.addEventListener('click', (e) => {
    const b = e.target.closest('[data-hours]');
    if (!b) return;
    hours = Number(b.dataset.hours);
    header.querySelectorAll('[data-hours]').forEach((x) => x.setAttribute('aria-pressed', String(x === b)));
    selection = null;
    chart.setSelection(null);
    renderSelection();
    load();
  });

  renderSelection();
  load();
  const timer = setInterval(load, 30_000);
  return {
    destroy: () => {
      alive = false;
      clearInterval(timer);
      chart.destroy();
      tempChart?.destroy();
    },
  };
}

// ---------------------------------------------------------------- Mes PC (serveur)

export function fleet(root, ctx) {
  const header = head(root, 'Mes PC', 'Les PC inscrits sur ce serveur : s’ils répondent, et ce qui ne va pas.',
    `<button class="btn primary" data-act="add">${icon('plus', 14)} Ajouter un PC</button>`);
  const panel = document.createElement('div');
  root.appendChild(panel);
  const list = document.createElement('div');
  list.className = 'grid';
  root.appendChild(list);
  let alive = true;

  const render = (machines) => {
    if (!alive) return;
    if (machines.length === 0) {
      list.innerHTML = '<section class="card span-l"><div class="empty">Aucun PC inscrit pour l’instant. Clique sur « Ajouter un PC » pour obtenir un code d’inscription.</div></section>';
      return;
    }
    list.innerHTML = machines.map((m) => `
      <section class="card span-m">
        <div class="card-head">
          <h2>${f.esc(m.label)}</h2>
          <span class="meta"><span class="${m.online ? 'sev-ok' : 'muted'}">${m.online ? '● en ligne' : '○ hors ligne'}</span> · ${m.lastSeen ? f.esc(f.ago(m.lastSeen)) : 'jamais connecté'}</span>
        </div>
        <div class="verdict">
          <span class="sev-icon">${severityIcon(m.severity ?? 'info', 20)}</span>
          <div>
            <h3 style="font-size:16px">${f.esc(names.reveal(m.verdict, m.id) ?? 'En attente des premières données')}</h3>
            <p>${f.esc([m.os, m.cpu, m.ramGb ? f.value(m.ramGb, 'Go') : null].filter(Boolean).join(' · ') || 'Fiche de la machine pas encore reçue')}</p>
          </div>
        </div>
        <div style="margin-top:14px;display:flex;gap:8px;align-items:center">
          <button class="btn" data-open="${f.esc(m.id)}">Ouvrir</button>
          <span class="muted" style="font-size:12px">${m.mode === 'complet' ? 'Partage complet activé par l’utilisateur' : 'Mode discret : applications pseudonymisées'}</span>
        </div>
      </section>`).join('');
  };

  const load = async () => {
    try {
      const machines = await ctx.refreshMachines();
      // Clés de lecture mémorisées : les verdicts affichent les vrais noms des PC concernés.
      await Promise.all(machines
        .filter((m) => names.savedKey(m.id) && !names.isUnlocked(m.id))
        .map((m) => names.unlock(m.id, names.savedKey(m.id), false).catch(() => {})));
      render(machines);
    } catch (e) {
      ctx.toast(e.message);
    }
  };

  list.addEventListener('click', (e) => {
    const id = e.target.closest('[data-open]')?.dataset.open;
    if (id) ctx.selectMachine(id);
  });

  header.addEventListener('click', (e) => {
    if (!e.target.closest('[data-act="add"]')) return;
    panel.innerHTML = `
      <section class="card" style="margin-bottom:16px">
        <div class="card-head"><h2>Ajouter un PC</h2></div>
        <form class="toolbar" data-form="enroll">
          <input class="search" name="label" maxlength="60" placeholder="Nom du PC, ex. « PC de Thomas »" required>
          <button class="btn primary">Créer un code d’inscription</button>
        </form>
        <div data-result></div>
      </section>`;
    panel.querySelector('input').focus();
  });

  panel.addEventListener('submit', async (e) => {
    e.preventDefault();
    const label = e.target.label.value.trim();
    if (!label) return;
    try {
      const { code, expires } = await api.createEnrollment(label);
      const server = location.origin;
      panel.querySelector('[data-result]').innerHTML = `
        <p>Code pour <b>${f.esc(label)}</b> : <code class="code">${f.esc(code)}</code> <span class="muted">(usage unique, valable jusqu’à ${f.esc(f.dateTime(expires))})</span></p>
        <p class="secondary">Sur le PC à surveiller, lance l’agent une première fois avec :</p>
        <pre class="snippet">MonitorKing.Agent.exe --MonitorKing:Server:Url=${f.esc(server)} --MonitorKing:Server:EnrollmentCode=${f.esc(code)}</pre>
        <p class="secondary">ou ajoute dans son <code>appsettings.json</code>, section <code>MonitorKing</code> :</p>
        <pre class="snippet">"Server": { "Url": "${f.esc(server)}", "EnrollmentCode": "${f.esc(code)}" }</pre>
        <p class="muted" style="font-size:12px">Une fois inscrit, le PC garde son propre jeton : le code ne sert plus. Par défaut, il envoie ses données en mode discret (applications pseudonymisées).</p>`;
    } catch (err) {
      ctx.toast(err.message);
    }
  });

  load();
  const timer = setInterval(load, 30_000);
  return { destroy: () => { alive = false; clearInterval(timer); } };
}

// ---------------------------------------------------------------- Confidentialité (agent)

export async function showPrivacy(dialog, ctx) {
  let state;
  let pseudonyms;
  try {
    [state, pseudonyms] = await Promise.all([api.privacy(), api.pseudonyms()]);
  } catch (e) {
    ctx.toast(e.message);
    return;
  }

  const server = state.server;
  const render = (filter = '') => {
    const rows = pseudonyms
      .filter((p) => !filter || `${p.name} ${p.description ?? ''} ${p.pseudonym}`.toLowerCase().includes(filter))
      .slice(0, 200);
    dialog.innerHTML = `
      <form method="dialog">
        <div class="dlg-head"><h2>Confidentialité et envoi</h2><button class="icon-btn" value="close" aria-label="Fermer">${icon('close', 16)}</button></div>
        <div class="dlg-body">
          <p><b>Mode actuel : ${state.mode === 'complet' ? `partage complet jusqu’à ${f.esc(f.time(state.fullUntil))}` : 'discret'}</b></p>
          <ul class="secondary" style="margin:6px 0 14px;padding-left:18px">
            <li>Tout le détail reste sur ce PC : tu vois tout ici.</li>
            <li>En mode discret, ce qui part vers le serveur ne contient ni le nom de tes applications (remplacé par « Appli 7F3A9C »), ni les titres de fenêtres, ni le nom du Wi-Fi, ni les messages de Windows. Les composants de Windows restent lisibles.</li>
            <li>Seul ce PC peut activer le partage complet, et il s’arrête tout seul.</li>
          </ul>
          <p class="secondary">${server
            ? `Serveur : <b>${f.esc(server.url)}</b> · ${server.enrolled ? `inscrit sous le nom « ${f.esc(server.label ?? '?')} »` : 'pas encore inscrit'}${server.lastUpload ? ` · dernier envoi ${f.esc(f.ago(server.lastUpload))}` : ''}${server.lastError ? `<br><span class="sev-warning">${f.esc(server.lastError)}</span>` : ''}`
            : 'Aucun serveur configuré : rien ne quitte ce PC.'}</p>
          <div class="toolbar">
            ${state.mode === 'complet'
              ? '<button type="button" class="btn primary" data-privacy="discreet">Revenir au mode discret</button>'
              : '<button type="button" class="btn" data-privacy="1">Tout partager pendant 1 h</button><button type="button" class="btn" data-privacy="24">pendant 24 h</button>'}
          </div>
          <h3 style="font-size:14px;margin:18px 0 8px">Clé de lecture des noms</h3>
          <p class="secondary">Les vrais noms de tes applications partent aussi vers le serveur, mais <b>chiffrés</b> : il ne peut pas les lire. Avec cette clé, une personne de confiance peut les voir dans son navigateur. Ne la donne que si tu le souhaites ; la renouveler lui retire l'accès.</p>
          <div class="toolbar">
            <code class="code" style="word-break:break-all">${f.esc(state.readKey)}</code>
            <button type="button" class="btn small" data-key="copy">Copier</button>
            <button type="button" class="btn small" data-key="rotate">Renouveler la clé</button>
          </div>
          <h3 style="font-size:14px;margin:18px 0 8px">Retrouver une application à partir de son pseudonyme</h3>
          <input class="search" data-filter type="search" placeholder="Pseudonyme ou nom (ex. 7F3A, steam)" value="${f.esc(filter)}" style="width:100%">
          <div class="table-wrap" style="margin-top:8px"><table class="data"><thead><tr><th>Application</th><th>Côté serveur</th></tr></thead><tbody>
            ${rows.map((p) => `<tr><td><b>${f.esc(p.description || p.name)}</b><div class="muted" style="font-size:12px">${f.esc(p.name)}</div></td>
              <td>${p.system ? '<span class="muted">lisible (Windows)</span>' : `<code class="code">${f.esc(p.pseudonym)}</code>`}</td></tr>`).join('')}
          </tbody></table></div>
        </div>
      </form>`;
    const input = dialog.querySelector('[data-filter]');
    input.addEventListener('input', () => {
      const value = input.value.trim().toLowerCase();
      render(value);
      const again = dialog.querySelector('[data-filter]');
      again.focus();
      again.setSelectionRange(again.value.length, again.value.length);
    });
    dialog.querySelectorAll('[data-privacy]').forEach((button) => button.addEventListener('click', async () => {
      try {
        const choice = button.dataset.privacy;
        if (choice === 'discreet') await api.backToDiscreet();
        else await api.shareFull(Number(choice));
        state = await api.privacy();
        render(filter);
        ctx.toast(state.mode === 'complet' ? 'Partage complet activé : il s’arrêtera tout seul.' : 'Retour au mode discret.');
      } catch (e) {
        ctx.toast(e.message);
      }
    }));
    dialog.querySelector('[data-key="copy"]').addEventListener('click', async () => {
      try {
        await navigator.clipboard.writeText(state.readKey);
        ctx.toast('Clé copiée. Ne la transmets qu’à une personne de confiance.');
      } catch {
        ctx.toast('Copie impossible : sélectionne la clé à la main.');
      }
    });
    dialog.querySelector('[data-key="rotate"]').addEventListener('click', async () => {
      if (!confirm('Renouveler la clé ? Les personnes qui ont l’ancienne ne verront plus les vrais noms de tes applications.')) return;
      try {
        await api.rotateReadKey();
        state = await api.privacy();
        render(filter);
        ctx.toast('Nouvelle clé générée : les noms sont rechiffrés au prochain envoi.');
      } catch (e) {
        ctx.toast(e.message);
      }
    });
  };

  render();
  if (!dialog.open) dialog.showModal();
}

// ---------------------------------------------------------------- Sélecteur de widget (accueil)

export function widgetPicker(dialog) {
  return () => new Promise((resolve) => {
    const types = Object.entries(catalog);
    let type = null;
    const numeric = [...store.defs.values()].filter((d) => !d.key.startsWith('sys.') && d.key !== 'hang.count');
    const groups = [...new Set(numeric.map((d) => d.group))];
    const metricOptions = groups.map((g) => `<optgroup label="${f.esc(GROUP_LABELS[g] ?? g)}">${numeric.filter((d) => d.group === g)
      .map((d) => `<option value="${f.esc(d.key)}">${f.esc(d.label)}${d.unit ? ` (${f.esc(d.unit)})` : ''}</option>`).join('')}</optgroup>`).join('');

    const fields = (spec) => {
      const parts = [];
      if (spec.needs === 'metric') parts.push(`<div class="field"><label for="w-metric">Métrique</label><select id="w-metric">${metricOptions}</select></div>`);
      if (spec.needs === 'metrics') {
        parts.push(`<div class="field"><label for="w-metrics">Métriques (1 à 4, même unité — Ctrl+clic pour en choisir plusieurs)</label><select id="w-metrics" multiple>${metricOptions}</select></div>`);
        parts.push('<div class="field"><label for="w-minutes">Durée affichée</label><select id="w-minutes"><option value="5">5 minutes</option><option value="10" selected>10 minutes</option><option value="15">15 minutes</option><option value="30">30 minutes</option></select></div>');
      }
      if (spec.needs === 'resource') parts.push(`<div class="field"><label for="w-by">Ressource</label><select id="w-by">${Object.entries(RESOURCES).map(([k, r]) => `<option value="${k}">${r.label}</option>`).join('')}</select></div>`);
      if (spec.needs === 'sensorType') parts.push(`<div class="field"><label for="w-type">Type de capteur</label><select id="w-type">${Object.entries(SENSOR_TYPES).map(([k, l]) => `<option value="${k}">${l}</option>`).join('')}</select></div>`);
      parts.push(`<div class="field"><label for="w-size">Taille</label><select id="w-size">${[['s', 'Petite (1 colonne)'], ['m', 'Moyenne (2 colonnes)'], ['l', 'Pleine largeur']].map(([v, l]) => `<option value="${v}" ${v === spec.defaultSize ? 'selected' : ''}>${l}</option>`).join('')}</select></div>`);
      parts.push('<div class="field"><label for="w-title">Titre (facultatif)</label><input id="w-title" type="text" maxlength="80"></div>');
      return parts.join('');
    };

    dialog.innerHTML = `
      <form method="dialog">
        <div class="dlg-head"><h2>Ajouter un widget</h2><button class="icon-btn" value="cancel" aria-label="Fermer">${icon('close', 16)}</button></div>
        <div class="dlg-body">
          <div class="type-list">${types.map(([k, s]) => `<button type="button" class="type-card" data-type="${k}" aria-pressed="false"><b>${f.esc(s.name)}</b><small>${f.esc(s.description)}</small></button>`).join('')}</div>
          <div class="params"></div>
          <p class="error sev-critical" hidden></p>
        </div>
        <div class="dlg-foot"><button class="btn ghost" value="cancel">Annuler</button><button class="btn primary" value="add" disabled>Ajouter</button></div>
      </form>`;
    const paramsEl = dialog.querySelector('.params');
    const addBtn = dialog.querySelector('[value="add"]');
    const errorEl = dialog.querySelector('.error');

    dialog.querySelector('.type-list').addEventListener('click', (e) => {
      const b = e.target.closest('[data-type]');
      if (!b) return;
      type = b.dataset.type;
      dialog.querySelectorAll('[data-type]').forEach((x) => x.setAttribute('aria-pressed', String(x === b)));
      paramsEl.innerHTML = fields(catalog[type]);
      addBtn.disabled = false;
      errorEl.hidden = true;
    });

    const build = () => {
      const spec = catalog[type];
      const params = {};
      const q = (id) => dialog.querySelector(id);
      if (spec.needs === 'metric') params.metric = q('#w-metric').value;
      if (spec.needs === 'metrics') {
        params.metrics = [...q('#w-metrics').selectedOptions].map((o) => o.value);
        params.minutes = Number(q('#w-minutes').value);
        if (params.metrics.length < 1 || params.metrics.length > 4) return { error: 'Choisis entre 1 et 4 métriques.' };
        const units = new Set(params.metrics.map((k) => store.def(k).unit));
        if (units.size > 1) return { error: 'Les métriques d’une même courbe doivent avoir la même unité (un seul axe).' };
      }
      if (spec.needs === 'resource') params.by = q('#w-by').value;
      if (spec.needs === 'sensorType') params.type = q('#w-type').value;
      const title = q('#w-title').value.trim();
      if (title) params.title = title;
      return { item: { type, size: q('#w-size').value, params } };
    };

    addBtn.addEventListener('click', (e) => {
      if (!type) return;
      const r = build();
      if (r.error) {
        e.preventDefault();
        errorEl.textContent = r.error;
        errorEl.hidden = false;
      } else {
        dialog.returnValue = 'add';
        dialog.dataset.item = JSON.stringify(r.item);
      }
    });

    dialog.addEventListener('close', () => {
      resolve(dialog.returnValue === 'add' && dialog.dataset.item ? JSON.parse(dialog.dataset.item) : null);
      delete dialog.dataset.item;
    }, { once: true });
    dialog.returnValue = '';
    dialog.showModal();
  });
}
