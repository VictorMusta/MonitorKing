// Client de l'API locale de l'agent (même origine).
function query(params) {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(params || {})) if (v !== undefined && v !== null && v !== '') q.set(k, v);
  const s = q.toString();
  return s ? `?${s}` : '';
}

async function get(path) {
  const response = await fetch(`/api${path}`, { headers: { accept: 'application/json' } });
  if (!response.ok) throw new Error(`${response.status} sur ${path}`);
  return response.json();
}

export const api = {
  info: () => get('/info'),
  status: () => get('/status'),
  metrics: () => get('/metrics'),
  live: () => get('/live'),
  series: (keys, params) => get(`/series${query({ keys: keys.join(','), ...params })}`),
  processes: (from, to) => get(`/processes${query({ from, to })}`),
  events: (params) => get(`/events${query(params)}`),
  diagnosis: (params) => get(`/diagnosis${query(params)}`),
  async layout(id) {
    const response = await fetch(`/api/layouts/${encodeURIComponent(id)}`);
    return response.ok ? response.json() : null;
  },
  async saveLayout(id, layout) {
    const response = await fetch(`/api/layouts/${encodeURIComponent(id)}`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(layout),
    });
    if (!response.ok) throw new Error(`Enregistrement impossible (${response.status})`);
  },
};
