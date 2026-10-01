// État partagé du dashboard : dernier échantillon, historique temps réel (30 min) et définitions des métriques.
import { api } from './api.js';

const LIVE_WINDOW = 30 * 60_000;

export const store = {
  info: null,
  status: [],
  defs: new Map(),
  latest: null,
  lastOk: 0,
  history: new Map(),
  seeded: new Set(),

  async init() {
    const [info, defs, status] = await Promise.all([api.info(), api.metrics(), api.status()]);
    this.info = info;
    this.status = status;
    this.setDefs(defs);
  },

  /** Oublie tout ce qui concerne la machine affichée (changement de PC côté serveur). */
  reset() {
    this.info = null;
    this.status = [];
    this.defs = new Map();
    this.latest = null;
    this.lastOk = 0;
    this.history = new Map();
    this.seeded = new Set();
  },

  setDefs(defs) {
    for (const d of defs) this.defs.set(d.key, d);
  },

  async refreshMeta() {
    const [defs, status] = await Promise.all([api.metrics(), api.status()]);
    this.setDefs(defs);
    this.status = status;
  },

  def(key) {
    return this.defs.get(key) ?? { key, label: key, unit: '', group: 'other' };
  },

  hint(collectorId) {
    return this.status.find((s) => s.id === collectorId)?.hint ?? null;
  },

  /** Charge l'historique temps réel (30 min) des métriques demandées, une seule fois par métrique. */
  async seed(keys) {
    const missing = keys.filter((k) => !this.seeded.has(k));
    if (missing.length === 0) return;
    missing.forEach((k) => this.seeded.add(k));
    try {
      const result = await api.series(missing, { minutes: 30 });
      for (const [key, points] of Object.entries(result.series)) {
        const current = this.history.get(key) || [];
        const merged = new Map();
        for (const p of points) merged.set(p[0], p);
        for (const p of current) merged.set(p[0], p);
        this.history.set(key, [...merged.values()].sort((a, b) => a[0] - b[0]));
      }
    } catch {
      missing.forEach((k) => this.seeded.delete(k));
    }
  },

  push(snapshot) {
    this.latest = snapshot;
    this.lastOk = Date.now();
    const cutoff = snapshot.ts - LIVE_WINDOW;
    for (const [key, v] of Object.entries(snapshot.metrics)) {
      let points = this.history.get(key);
      if (!points) this.history.set(key, (points = []));
      if (points.length === 0 || points[points.length - 1][0] < snapshot.ts) points.push([snapshot.ts, v, v]);
      while (points.length && points[0][0] < cutoff) points.shift();
    }
  },

  series(key, minutes) {
    const from = (this.latest?.ts ?? Date.now()) - minutes * 60_000;
    return (this.history.get(key) || []).filter((p) => p[0] >= from);
  },

  metric(key) {
    return this.latest?.metrics?.[key];
  },
};
