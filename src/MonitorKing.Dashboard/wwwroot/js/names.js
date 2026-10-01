// Vrais noms des applications pseudonymisées, déchiffrés DANS LE NAVIGATEUR avec la clé de lecture du PC.
// Le serveur ne stocke que des noms chiffrés (AES-256-GCM) et ne voit jamais la clé : c'est la personne
// du PC qui la donne, et elle peut la renouveler pour retirer l'accès.
const maps = new Map(); // id de machine -> Map(pseudonyme -> { n: exécutable, d: description })
const PATTERN = /Appli [0-9A-F]{6}/g;
let active = null;

const storageKey = (id) => `mk-readkey-${id}`;

function bytesFromBase64(value) {
  let s = value.trim().replace(/-/g, '+').replace(/_/g, '/');
  while (s.length % 4) s += '=';
  return Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
}

export function setActive(id) {
  active = id;
}

export function isUnlocked(id = active) {
  return maps.has(id);
}

export function savedKey(id) {
  try {
    return localStorage.getItem(storageKey(id));
  } catch {
    return null;
  }
}

export function forget(id = active) {
  maps.delete(id);
  try {
    localStorage.removeItem(storageKey(id));
  } catch {
    // rien de mémorisé
  }
}

/** Déchiffre les noms d'un PC avec sa clé ; mémorise la clé dans ce navigateur si demandé. */
export async function unlock(id, key, remember) {
  let raw;
  try {
    raw = bytesFromBase64(key);
  } catch {
    throw new Error('Clé illisible : copie-la telle quelle depuis le PC.');
  }
  if (raw.length !== 32) throw new Error('Clé invalide : copie-la telle quelle depuis le PC.');
  const cryptoKey = await crypto.subtle.importKey('raw', raw, 'AES-GCM', false, ['decrypt']);
  const response = await fetch(`/api/m/${encodeURIComponent(id)}/sealed-names`);
  if (!response.ok) throw new Error(`Noms chiffrés indisponibles (${response.status}).`);
  const list = await response.json();
  const map = new Map();
  for (const { pseudonym, sealed } of list) {
    try {
      const bytes = bytesFromBase64(sealed);
      const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: bytes.slice(0, 12) }, cryptoKey, bytes.slice(12));
      map.set(pseudonym, JSON.parse(new TextDecoder().decode(plain)));
    } catch {
      // nom chiffré avec une autre clé (clé renouvelée depuis) : il reste pseudonymisé
    }
  }
  if (list.length > 0 && map.size === 0) throw new Error('Cette clé ne correspond pas à ce PC, ou elle a été renouvelée.');
  maps.set(id, map);
  if (remember) {
    try {
      localStorage.setItem(storageKey(id), key.trim());
    } catch {
      // stockage indisponible : la clé vaut pour cette page seulement
    }
  }
  return map.size;
}

/** Remplace les pseudonymes d'un texte par les vrais noms, si la clé du PC a été fournie. */
export function reveal(text, id = active) {
  const map = maps.get(id);
  if (!map || text == null) return text;
  return String(text).replace(PATTERN, (match) => {
    const entry = map.get(match);
    if (!entry) return match;
    return entry.d && entry.d.length <= 48 ? entry.d : entry.n;
  });
}

/** Comme reveal, mais avec l'exécutable en plus (« Steam (steam.exe) ») : pour les rapports. */
export function revealLong(text, id = active) {
  const map = maps.get(id);
  if (!map || text == null) return text;
  return String(text).replace(PATTERN, (match) => {
    const entry = map.get(match);
    if (!entry) return match;
    return entry.d && entry.d !== entry.n ? `${entry.d} (${entry.n})` : entry.n;
  });
}

export function realExe(name, id = active) {
  return maps.get(id)?.get(name)?.n ?? null;
}

export function realDescription(name, id = active) {
  return maps.get(id)?.get(name)?.d ?? null;
}
