// Rapports Markdown : à copier dans une conversation avec Claude, ou à télécharger.
import { store } from './store.js';
import { api } from './api.js';
import { revealLong } from './names.js';

async function fetchReport(params) {
  const response = await fetch(api.reportUrl(params));
  if (!response.ok) throw new Error(`Rapport indisponible (${response.status})`);
  // Si la clé de lecture de ce PC a été fournie, les pseudonymes sont remplacés ici, dans le navigateur.
  return revealLong(await response.text());
}

async function writeClipboard(text) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    // Repli : sélection d'une zone de texte invisible.
    const area = document.createElement('textarea');
    area.value = text;
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();
    document.execCommand('copy');
    area.remove();
  }
}

export async function copyReport(params, toast) {
  const text = await fetchReport(params);
  await writeClipboard(text);
  toast(`Rapport copié (${Math.round(text.length / 1024)} Ko) : colle-le dans ta conversation avec Claude.`);
}

export async function downloadReport(params, toast) {
  const text = await fetchReport(params);
  const stamp = new Date().toISOString().slice(0, 16).replace(/[-:T]/g, '');
  const name = `MonitorKing_${(store.info?.machineName ?? 'PC').replace(/[^\w-]+/g, '')}_${stamp}.md`;
  const url = URL.createObjectURL(new Blob([text], { type: 'text/markdown;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 5000);
  toast(`Rapport téléchargé : ${name}`);
}
