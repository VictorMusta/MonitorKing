// Formats en français (espace insécable avant les unités, virgule décimale).
import { realExe, realDescription } from './names.js';
const nf0 = new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 0 });
const nf1 = new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 1 });
const nf1f = new Intl.NumberFormat('fr-FR', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
const timeFmt = new Intl.DateTimeFormat('fr-FR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const shortTimeFmt = new Intl.DateTimeFormat('fr-FR', { hour: '2-digit', minute: '2-digit' });
const dayFmt = new Intl.DateTimeFormat('fr-FR', { weekday: 'long', day: 'numeric', month: 'long' });
const dateTimeFmt = new Intl.DateTimeFormat('fr-FR', { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

export const NBSP = ' ';

export function bytes(b) {
  if (b == null || isNaN(b)) return '–';
  const units = ['o', 'Ko', 'Mo', 'Go', 'To'];
  let i = 0;
  let v = Math.abs(b);
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return (i === 0 ? nf0 : v >= 100 ? nf0 : nf1).format(v) + NBSP + units[i];
}

export const rate = (bps) => bytes(bps) + '/s';

export function mb(megabytes) {
  if (megabytes == null) return '–';
  return megabytes >= 1024 ? nf1f.format(megabytes / 1024) + NBSP + 'Go' : nf0.format(megabytes) + NBSP + 'Mo';
}

export function value(v, unit) {
  if (v == null || isNaN(v)) return '–';
  switch (unit) {
    case '%': return nf0.format(v) + NBSP + '%';
    case 'o/s': return rate(v);
    case 'Go': return nf1f.format(v) + NBSP + 'Go';
    case 'Mo': return mb(v);
    case 'ms': return (v < 10 ? nf1 : nf0).format(v) + NBSP + 'ms';
    case '°C': return nf0.format(v) + NBSP + '°C';
    case '/s': return nf0.format(v) + '/s';
    case '': return nf0.format(v);
    case 'V': return new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 3 }).format(v) + NBSP + 'V';
    default: return (Math.abs(v) >= 100 ? nf0 : nf1).format(v) + NBSP + unit;
  }
}

export const pct = (v) => value(v, '%');
export const time = (ts) => timeFmt.format(new Date(ts));
export const shortTime = (ts) => shortTimeFmt.format(new Date(ts));
export const day = (ts) => dayFmt.format(new Date(ts));
export const dateTime = (ts) => dateTimeFmt.format(new Date(ts));

export function duration(seconds) {
  seconds = Math.max(0, Math.round(seconds));
  if (seconds < 60) return `${seconds}${NBSP}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}${NBSP}min ${String(seconds % 60).padStart(2, '0')}${NBSP}s`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}${NBSP}h ${String(Math.floor(seconds % 3600 / 60)).padStart(2, '0')}`;
  return `${Math.floor(seconds / 86400)}${NBSP}j ${Math.floor(seconds % 86400 / 3600)}${NBSP}h`;
}

export function ago(ts) {
  const s = (Date.now() - ts) / 1000;
  if (s < 10) return "à l'instant";
  if (s < 60) return `il y a ${Math.round(s)}${NBSP}s`;
  if (s < 3600) return `il y a ${Math.round(s / 60)}${NBSP}min`;
  if (s < 86400) return `il y a ${Math.round(s / 3600)}${NBSP}h`;
  return `il y a ${Math.round(s / 86400)}${NBSP}j`;
}

export function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/** Nom lisible d'une application : description si disponible, sinon l'exécutable (déchiffrés si la clé du PC est connue). */
export function appName(p) {
  const exe = realExe(p.name);
  const description = exe ? realDescription(p.name) : p.description;
  const d = description && description.length <= 48 ? description : null;
  return d || exe || p.name;
}

/** Exécutable réel (si les noms ont été déchiffrés), sinon le nom reçu (éventuellement un pseudonyme). */
export const exeName = (p) => realExe(p.name) ?? p.name;
