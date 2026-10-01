// Shell du dashboard : navigation par onglets (#hash), interrogation de l'agent toutes les 2 s, thème.
import { api } from './api.js';
import { store } from './store.js';
import * as f from './format.js';
import { icon } from './icons.js';
import * as views from './views.js';

const ROUTES = [
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

const main = document.querySelector('main');
const tabs = document.getElementById('tabs');
const live = document.getElementById('live');
let current = null;

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
};

function route() {
  const id = location.hash.slice(1) || 'accueil';
  const entry = ROUTES.find((r) => r.id === id) ?? ROUTES[0];
  try {
    current?.destroy?.();
  } catch (e) {
    console.error(e);
  }
  main.innerHTML = '';
  tabs.querySelectorAll('.tab').forEach((t) => (t.dataset.route === entry.id ? t.setAttribute('aria-current', 'page') : t.removeAttribute('aria-current')));
  current = entry.view(main, ctx);
  if (store.latest) current?.update?.(store.latest);
  document.title = `${entry.label} · MonitorKing`;
  window.scrollTo(0, 0);
}

function setLive(ok) {
  live.innerHTML = ok
    ? '<span class="live-dot"></span><span>En direct</span>'
    : '<span class="live-dot stale"></span><span>Agent injoignable</span>';
}

function updateBadges(snap) {
  const tab = tabs.querySelector('[data-route="diagnostic"]');
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
  try {
    const snap = await api.live();
    store.push(snap);
    setLive(true);
    updateBadges(snap);
    current?.update?.(snap);
  } catch {
    setLive(false);
  } finally {
    setTimeout(poll, 2000);
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

// ---- État des collecteurs (pied de page).
function renderFooter() {
  const i = store.info;
  document.getElementById('footer').innerHTML = `
    <span>MonitorKing v${f.esc(i.agentVersion)} · lecture seule</span>
    <span title="${f.esc(i.dataPath)}">Données locales, conservées ${i.retentionDays} jours</span>
    <button id="show-status">État des collecteurs</button>`;
  document.getElementById('show-status').addEventListener('click', showStatus);
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

async function waitForAgent() {
  for (;;) {
    try {
      const snap = await api.live();
      store.push(snap);
      return;
    } catch {
      setLive(false);
      await new Promise((r) => setTimeout(r, 1500));
    }
  }
}

async function start() {
  applyTheme(document.documentElement.dataset.theme);
  tabs.innerHTML = ROUTES.map((r) => `<a class="tab" href="#${r.id}" data-route="${r.id}">${f.esc(r.label)}</a>`).join('');
  await waitForAgent();
  await store.init();
  document.getElementById('machine-name').textContent = store.info.machineName;
  document.getElementById('machine-os').textContent = `${store.info.os} · ${store.info.cpu}`;
  renderFooter();
  setLive(true);
  window.addEventListener('hashchange', route);
  route();
  setTimeout(poll, 2000);
  setInterval(() => store.refreshMeta().catch(() => {}), 30_000);
}

start();
