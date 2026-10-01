// Shell du dashboard : navigation par onglets (#hash), interrogation régulière, thème.
// Deux modes : « agent » (ce PC, en direct toutes les 2 s) et « serveur » (plusieurs PC, sélecteur, données à ~10-30 s).
import { api, useMachine } from './api.js';
import { store } from './store.js';
import * as f from './format.js';
import { icon } from './icons.js';
import * as views from './views.js';

const MACHINE_ROUTES = [
  { id: 'accueil', label: 'Accueil', view: views.home },
  { id: 'diagnostic', label: 'Pourquoi ça rame ?', view: views.diagnostic },
  { id: 'applications', label: 'Applications', view: views.applications },
  { id: 'systeme', label: 'Processeur & mémoire', view: views.system },
  { id: 'gpu', label: 'Carte graphique', view: views.gpu },
  { id: 'capteurs', label: 'Capteurs', view: views.sensors },
  { id: 'reseau', label: 'Réseau', view: views.network },
  { id: 'journal', label: 'Journal', view: views.journal },
  { id: 'historique', label: 'Historique', view: views.history },
];
const FLEET_ROUTE = { id: 'parc', label: 'Mes PC', view: views.fleet };

const main = document.querySelector('main');
const tabs = document.getElementById('tabs');
const live = document.getElementById('live');
let current = null;
let mode = 'agent';
let machines = [];
let pollTimer = null;
let routes = MACHINE_ROUTES;

let toastTimer = null;
function toast(message) {
  let el = document.getElementById('toast');
  if (!el) {
    el = document.createElement('div');
    el.id = 'toast';
    el.className = 'toast';
    el.setAttribute('role', 'status');
    document.body.appendChild(el);
  }
  el.textContent = message;
  el.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove('show'), 4500);
}

const ctx = {
  navigate: (route) => { location.hash = route; },
  toast,
  pickWidget: views.widgetPicker(document.getElementById('widget-dialog')),
  get mode() { return mode; },
  selectMachine: (id) => selectMachine(id, 'accueil'),
  refreshMachines: async () => { machines = await api.machines(); renderMachinePicker(); return machines; },
};

function route() {
  const fallback = mode === 'server' && !store.info ? 'parc' : 'accueil';
  const id = location.hash.slice(1) || fallback;
  let entry = routes.find((r) => r.id === id) ?? routes.find((r) => r.id === fallback) ?? routes[0];
  if (entry.id !== 'parc' && mode === 'server' && !store.info) entry = FLEET_ROUTE; // aucun PC choisi
  try {
    current?.destroy?.();
  } catch (e) {
    console.error(e);
  }
  main.innerHTML = '';
  tabs.querySelectorAll('.tab').forEach((t) => (t.dataset.route === entry.id ? t.setAttribute('aria-current', 'page') : t.removeAttribute('aria-current')));
  current = entry.view(main, ctx);
  if (store.latest && entry.id !== 'parc') current?.update?.(store.latest);
  document.title = `${entry.label} · MonitorKing`;
  window.scrollTo(0, 0);
}

function setLive(state, text) {
  const dot = state === 'ok' ? 'live-dot' : 'live-dot stale';
  live.innerHTML = `<span class="${dot}"></span><span>${f.esc(text)}</span>`;
}

function liveLabel(snap) {
  if (mode === 'agent') return ['ok', 'En direct'];
  const age = (Date.now() - snap.ts) / 1000;
  return age < 90 ? ['ok', `Reçu ${f.ago(snap.ts)}`] : ['stale', `Hors ligne · ${f.ago(snap.ts)}`];
}

function updateBadges(snap) {
  const tab = tabs.querySelector('[data-route="diagnostic"]');
  if (!tab) return;
  const count = snap.hung.length;
  let badge = tab.querySelector('.badge');
  if (count && !badge) {
    badge = document.createElement('span');
    badge.className = 'badge';
    tab.appendChild(badge);
  }
  if (badge) {
    if (count) {
      badge.textContent = count;
      badge.title = `${count} fenêtre(s) ne répond(ent) pas`;
    } else badge.remove();
  }
}

async function poll() {
  clearTimeout(pollTimer);
  if (mode === 'server' && !store.info) {
    setLive('ok', `Serveur · ${machines.length} PC`);
    pollTimer = setTimeout(poll, 10_000);
    return;
  }
  try {
    const snap = await api.live();
    store.push(snap);
    setLive(...liveLabel(snap));
    updateBadges(snap);
    if (!location.hash.startsWith('#parc')) current?.update?.(snap);
  } catch {
    setLive('stale', mode === 'agent' ? 'Agent injoignable' : 'Pas encore de données');
  } finally {
    pollTimer = setTimeout(poll, mode === 'agent' ? 2000 : 10_000);
  }
}

// ---- Thème (clair / sombre), mémorisé par navigateur.
function applyTheme(theme) {
  if (theme) document.documentElement.dataset.theme = theme;
  else delete document.documentElement.dataset.theme;
  const dark = theme ? theme === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches;
  const button = document.getElementById('theme');
  button.innerHTML = icon(dark ? 'sun' : 'moon', 16);
  button.title = dark ? 'Passer en thème clair' : 'Passer en thème sombre';
  window.dispatchEvent(new Event('themechange'));
}

document.getElementById('theme').addEventListener('click', () => {
  const dark = document.documentElement.dataset.theme
    ? document.documentElement.dataset.theme === 'dark'
    : matchMedia('(prefers-color-scheme: dark)').matches;
  const next = dark ? 'light' : 'dark';
  try {
    localStorage.setItem('mk-theme', next);
  } catch {
    // stockage indisponible : le choix vaut pour cette page seulement
  }
  applyTheme(next);
});
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => applyTheme(document.documentElement.dataset.theme));

// ---- En-tête : nom du PC (agent) ou sélecteur de PC (serveur).
function renderMachinePicker() {
  const box = document.querySelector('.machine');
  if (mode === 'agent') {
    box.innerHTML = `<strong>${f.esc(store.info.machineName)}</strong><span>${f.esc(`${store.info.os} · ${store.info.cpu}`)}</span>`;
    return;
  }
  const selected = store.info?.machineId ?? '';
  box.innerHTML = machines.length
    ? `<select class="machine-select" aria-label="PC affiché">
         ${selected ? '' : '<option value="" selected>Choisir un PC…</option>'}
         ${machines.map((m) => `<option value="${f.esc(m.id)}" ${m.id === selected ? 'selected' : ''}>${m.online ? '● ' : '○ '}${f.esc(m.label)}</option>`).join('')}
       </select>
       <span>${store.info ? f.esc(`${store.info.os} · ${store.info.cpu}`) : 'Serveur central'}</span>`
    : '<strong>Aucun PC inscrit</strong><span>Serveur central</span>';
  box.querySelector('select')?.addEventListener('change', (e) => e.target.value && selectMachine(e.target.value));
}

async function selectMachine(id, nextRoute) {
  useMachine(id);
  store.reset();
  try {
    localStorage.setItem('mk-machine', id);
  } catch {
    // pas grave : on redemandera le PC la prochaine fois
  }
  try {
    await store.init();
    store.push(await api.live());
  } catch {
    // PC inscrit mais encore sans données : les vues afficheront leurs états vides.
  }
  renderMachinePicker();
  renderFooter();
  if (nextRoute && location.hash !== `#${nextRoute}`) location.hash = nextRoute; // hashchange affichera la vue
  else route();
  poll();
}

// ---- Pied de page.
function renderFooter() {
  const footer = document.getElementById('footer');
  if (mode === 'server') {
    footer.innerHTML = store.info
      ? `<span>Serveur MonitorKing · ${f.esc(store.info.machineName)} · agent v${f.esc(store.info.agentVersion)}</span>
         <span>Confidentialité : ${store.info.privacyMode === 'complet' ? 'partage complet (temporaire)' : 'mode discret, applications pseudonymisées'}</span>
         <span>Données conservées ${store.info.retentionDays} jours</span>`
      : '<span>Serveur MonitorKing</span>';
    return;
  }
  const i = store.info;
  footer.innerHTML = `
    <span>MonitorKing v${f.esc(i.agentVersion)} · lecture seule</span>
    <span title="${f.esc(i.dataPath)}">Données locales, conservées ${i.retentionDays} jours</span>
    <button id="show-privacy">Confidentialité et envoi</button>
    <button id="show-status">État des collecteurs</button>`;
  document.getElementById('show-status').addEventListener('click', showStatus);
  document.getElementById('show-privacy').addEventListener('click', () => views.showPrivacy(document.getElementById('status-dialog'), ctx));
}

async function showStatus() {
  await store.refreshMeta().catch(() => {});
  const dialog = document.getElementById('status-dialog');
  dialog.innerHTML = `
    <form method="dialog">
      <div class="dlg-head"><h2>État des collecteurs</h2><button class="icon-btn" value="close" aria-label="Fermer">${icon('close', 16)}</button></div>
      <div class="dlg-body">
        <table class="data"><thead><tr><th>Collecteur</th><th class="r">Durée</th><th>État</th></tr></thead><tbody>
        ${store.status.map((s) => `<tr><td><b>${f.esc(s.label)}</b>${s.hint ? `<div class="sev-warning" style="font-size:12px">${f.esc(s.hint)}</div>` : ''}${s.detail ? `<div class="muted" style="font-size:12px">${f.esc(s.detail)}</div>` : ''}</td>
          <td class="r">${s.lastMs > 0 ? `${f.esc(String(s.lastMs).replace('.', ','))} ms` : '—'}</td>
          <td>${s.ok ? '<span class="sev-ok">OK</span>' : `<span class="sev-critical">${f.esc(s.error ?? 'Erreur')}</span>`}</td></tr>`).join('')}
        </tbody></table>
        <p class="muted" style="font-size:12px;margin-top:12px">Base de données : ${f.esc(store.info.dataPath)}<br>Échantillonnage : toutes les ${store.info.sampleIntervalMs / 1000} s, agrégé par 10 s.
        ${store.info.administrator ? '' : '<br>L’agent tourne sans droits administrateur : certaines sondes (températures CPU, journal de démarrage) sont limitées.'}</p>
      </div>
    </form>`;
  dialog.showModal();
}

async function detectMode() {
  for (;;) {
    try {
      return (await api.mode()).mode;
    } catch {
      setLive('stale', 'Connexion…');
      await new Promise((r) => setTimeout(r, 1500));
    }
  }
}

async function waitForAgent() {
  for (;;) {
    try {
      store.push(await api.live());
      return;
    } catch {
      setLive('stale', 'Agent injoignable');
      await new Promise((r) => setTimeout(r, 1500));
    }
  }
}

async function start() {
  applyTheme(document.documentElement.dataset.theme);
  mode = await detectMode();
  routes = mode === 'server' ? [FLEET_ROUTE, ...MACHINE_ROUTES] : MACHINE_ROUTES;
  tabs.innerHTML = routes.map((r) => `<a class="tab" href="#${r.id}" data-route="${r.id}">${f.esc(r.label)}</a>`).join('');
  window.addEventListener('hashchange', route);

  if (mode === 'server') {
    machines = await api.machines().catch(() => []);
    let saved = null;
    try {
      saved = localStorage.getItem('mk-machine');
    } catch {
      saved = null;
    }
    const initial = machines.find((m) => m.id === saved) ?? machines[0];
    if (initial) {
      await selectMachine(initial.id);
    } else {
      renderMachinePicker();
      renderFooter();
      route();
      poll();
    }
    setInterval(() => ctx.refreshMachines().catch(() => {}), 60_000);
    return;
  }

  await waitForAgent();
  await store.init();
  renderMachinePicker();
  renderFooter();
  setLive('ok', 'En direct');
  route();
  pollTimer = setTimeout(poll, 2000);
  setInterval(() => store.refreshMeta().catch(() => {}), 30_000);
}

start();
