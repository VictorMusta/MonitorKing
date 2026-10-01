#!/bin/bash
# Change le mot de passe du dashboard MonitorKing (authentification Caddy du bloc du domaine).
# À lancer sur le serveur, en root, dans un vrai terminal :
#   ssh -t root@serveur monitorking-password            (change le mot de passe de « victor »)
#   ssh -t root@serveur monitorking-password --check    (vérifie seulement que la ligne à remplacer est trouvée)
# Le mot de passe n'est ni affiché, ni écrit en clair : seul son hash bcrypt va dans le Caddyfile.
set -euo pipefail

CADDYFILE=/etc/caddy/Caddyfile
DOMAIN=${MONITORKING_DOMAIN:-monitorking-victor.duckdns.org}
LOGIN=${MONITORKING_LOGIN:-victor}

# Remplace le hash de $LOGIN, uniquement à l'intérieur du bloc du domaine.
replace_hash() {
  awk -v domain="$DOMAIN" -v login="$LOGIN" -v hash="$1" '
    {
      line = $0
      if (!inblock && index($0, domain) > 0 && index($0, "{") > 0) { inblock = 1; depth = 0 }
      if (inblock) {
        if ($1 == login && $2 ~ /^\$2/) { sub(/\$2[^ \t]*/, hash, line); replaced++ }
        opens = gsub(/\{/, "{"); closes = gsub(/\}/, "}")
        depth += opens - closes
        if (depth <= 0) inblock = 0
      }
      print line
    }
    END { if (replaced != 1) exit 3 }
  ' "$CADDYFILE"
}

if [ "${1:-}" = "--check" ]; then
  if replace_hash '$2a$14$verification' > /dev/null; then
    echo "OK : ligne « $LOGIN » trouvée dans le bloc $DOMAIN."
  else
    echo "Ligne « $LOGIN \$2… » introuvable (ou en double) dans le bloc $DOMAIN." >&2
    exit 1
  fi
  exit 0
fi

[ -t 0 ] || { echo "Lance cette commande dans un terminal interactif (ssh -t)." >&2; exit 1; }

read -r -s -p "Nouveau mot de passe pour « $LOGIN » (12 caractères minimum) : " PASSWORD; echo
read -r -s -p "Confirmation : " CONFIRM; echo
[ "$PASSWORD" = "$CONFIRM" ] || { echo "Les deux saisies ne correspondent pas." >&2; exit 1; }
[ ${#PASSWORD} -ge 12 ] || { echo "Trop court : 12 caractères minimum." >&2; exit 1; }

HASH=$(printf '%s\n' "$PASSWORD" | caddy hash-password 2>/dev/null | tail -n 1 || true)
unset PASSWORD CONFIRM
[[ "$HASH" == \$2* ]] || { echo "Impossible de calculer le hash avec caddy hash-password." >&2; exit 1; }

cp "$CADDYFILE" "$CADDYFILE.bak-password"
NEW=$(mktemp)
if ! replace_hash "$HASH" > "$NEW"; then
  rm -f "$NEW"
  echo "Ligne « $LOGIN » introuvable dans le bloc $DOMAIN : rien n'a été modifié." >&2
  exit 1
fi
cat "$NEW" > "$CADDYFILE"
rm -f "$NEW"

if caddy validate --config "$CADDYFILE" --adapter caddyfile > /tmp/caddy-validate.log 2>&1; then
  systemctl reload caddy
  echo "Mot de passe changé. Sauvegarde de l'ancienne configuration : $CADDYFILE.bak-password"
else
  cp "$CADDYFILE.bak-password" "$CADDYFILE"
  echo "Configuration invalide : ancienne version restaurée." >&2
  tail -n 20 /tmp/caddy-validate.log >&2
  exit 1
fi
