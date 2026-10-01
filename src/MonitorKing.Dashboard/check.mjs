// Vérifie que le dashboard peut se charger : syntaxe de chaque module, puis imports et exports cohérents.
// Node charge les modules comme le ferait le navigateur. Une erreur de syntaxe ou un import introuvable
// fait échouer le chargement (SyntaxError) avant toute exécution ; ensuite, l'absence de `document` est normale.
// Usage : node src/MonitorKing.Dashboard/check.mjs (lancé aussi par la release de l'agent).
import { spawnSync } from 'node:child_process';
import { cpSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

process.on('unhandledRejection', () => {}); // code du dashboard qui démarre sans navigateur : sans intérêt ici

const dir = mkdtempSync(join(tmpdir(), 'mk-dashboard-'));
cpSync(fileURLToPath(new URL('./wwwroot/js/', import.meta.url)), dir, { recursive: true });
writeFileSync(join(dir, 'package.json'), '{ "type": "module" }');
const files = readdirSync(dir).filter((name) => name.endsWith('.js'));
const errors = new Set();
const shorten = (text) => String(text).replaceAll(pathToFileURL(dir).href, 'wwwroot/js').replaceAll(dir, 'wwwroot/js').trim();

// 1. Syntaxe, fichier par fichier : node --check donne la ligne fautive.
for (const file of files) {
  const check = spawnSync(process.execPath, ['--check', join(dir, file)], { encoding: 'utf8' });
  if (check.status !== 0) errors.add(shorten(check.stderr).split('\n').filter((line) => !line.startsWith('    at ')).join('\n'));
}

// 2. Imports et exports : chaque module est chargé avec ses dépendances.
if (!errors.size) {
  for (const file of files) {
    try {
      await import(pathToFileURL(join(dir, file)).href);
    } catch (e) {
      if (e instanceof SyntaxError) errors.add(shorten(e.stack).split('\n').filter((line) => !line.startsWith('    at ')).join('\n'));
    }
  }
}
rmSync(dir, { recursive: true, force: true });

for (const error of errors) console.error(`${error}\n`);
console.log(errors.size ? `Dashboard : ${errors.size} erreur(s).` : `Dashboard : ${files.length} modules chargés, imports cohérents.`);
process.exit(errors.size ? 1 : 0);
