// Client de l'API (même origine). Le même dashboard parle à l'agent local (/api)
// ou au serveur central, où chaque PC a son préfixe (/api/m/{id}).
let base = '/api';

export function useMachine(id) {
  base = id ? `/api/m/${encodeURIComponent(id)}` : '/api';
}

function query(params) {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(params || {})) if (v !== undefined && v !== null && v !== '') q.set(k, v);
  const s = q.toString();
  return s ? `?${s}` : '';
}

async function get(path, root = base) {
  const response = await fetch(`${root}${path}`, { headers: { accept: 'application/json' } });
  if (!response.ok) throw new Error(`${response.status} sur ${path}`);
  return response.json();
}

async function send(method, path, body, root = '/api') {
  const response = await fetch(`${root}${path}`, {
    method,
    headers: { 'content-type': 'application/json', accept: 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error((await response.text().catch(() => '')) || `Erreur ${response.status}`);
  return response.status === 204 ? null : response.json().catch(() => null);
}

export const api = {
  // Communs
  mode: () => get('/mode', '/api'),
  // Serveur
  machines: () => get('/machines', '/api'),
  createEnrollment: (label) => send('POST', '/admin/enrollments', { label }),
  // Agent : confidentialité (décidée sur le PC lui-même)
  privacy: () => get('/privacy', '/api'),
  shareFull: (hours) => send('POST', `/privacy/full${query({ hours })}`),
  backToDiscreet: () => send('POST', '/privacy/discreet'),
  rotateReadKey: () => send('POST', '/privacy/rotate-key'),
  pseudonyms: () => get('/pseudonyms', '/api'),
  // Agent : mise à jour automatique (réglée sur le PC lui-même)
  update: () => get('/update', '/api'),
  setAutoUpdate: (enabled) => send('POST', '/update/auto', { enabled }),
  // Machine courante
  info: () => get('/info'),
  status: () => get('/status'),
  metrics: () => get('/metrics'),
  live: () => get('/live'),
  series: (keys, params) => get(`/series${query({ keys: keys.join(','), ...params })}`),
  processes: (from, to) => get(`/processes${query({ from, to })}`),
  breakdown: (resource, params) => get(`/breakdown${query({ resource, ...params })}`),
  events: (params) => get(`/events${query(params)}`),
  diagnosis: (params) => get(`/diagnosis${query(params)}`),
  reportUrl: (params) => `${base}/report${query(params)}`,
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
