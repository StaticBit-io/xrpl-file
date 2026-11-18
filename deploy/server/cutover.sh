#!/usr/bin/env bash
# Puts the site on the server once DNS points at it (owner's PC, Git Bash):
#
#   deploy/server/cutover.sh root@94.183.189.150 [xrpl-file.staticbit.io]
#
# Waits until public resolvers answer the server's IP for the domain, installs the Caddy site and
# reloads Caddy, then waits for HTTPS with a valid certificate and the site's release header. Caddy
# asks for the certificate only now, when the name already resolves here, so failed validations do
# not pile up against the issuer's rate limits.
# Spec: docs/design/2026-11-12-site-server-design.md, "Cut-over".
set -euo pipefail

TARGET=${1:?usage: cutover.sh <root@ip> [domain]}
DOMAIN=${2:-xrpl-file.staticbit.io}
HOST=${TARGET#*@}
HERE=$(cd "$(dirname "$0")" && pwd)
DNS_WAIT_SECONDS=1800
TLS_WAIT_SECONDS=900

SSH=(ssh -o ControlMaster=no -o ControlPath=none -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=15)

resolved() { # resolved <DoH JSON endpoint>: the A records the resolver gives for DOMAIN
  curl -fsS --max-time 10 -H 'accept: application/dns-json' "$1?name=$DOMAIN&type=A" |
    grep -o '"data":"[0-9.]*"' | cut -d'"' -f4 | sort -u | tr '\n' ' '
}

echo "== waiting for $DOMAIN to resolve to $HOST"
deadline=$((SECONDS + DNS_WAIT_SECONDS))
while true; do
  cloudflare=$(resolved https://cloudflare-dns.com/dns-query || true)
  google=$(resolved https://dns.google/resolve || true)
  echo "1.1.1.1: ${cloudflare:-none}  8.8.8.8: ${google:-none}"
  [[ $cloudflare == "$HOST " && $google == "$HOST " ]] && break
  if ((SECONDS > deadline)); then
    echo "DNS still does not point at $HOST; nothing on the server changed" >&2
    exit 1
  fi
  sleep 15
done

echo "== installing the Caddy site"
expected=$("${SSH[@]}" "$TARGET" "node -p \"require('/srv/xrpl-file/current/release.json').commit\"")
echo "the server runs $expected"
site=/etc/caddy/sites/$DOMAIN.caddy
# A site file that does not validate would stop Caddy from loading at its next restart: it goes again.
sed "s/^DOMAIN {/$DOMAIN {/" "$HERE/site.caddy" | "${SSH[@]}" "$TARGET" \
  "install -D -m 0644 /dev/stdin $site && if caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null; then systemctl reload caddy; else rm -f $site; echo 'the Caddy site does not validate; removed it' >&2; exit 1; fi"

echo "== waiting for https://$DOMAIN/"
deadline=$((SECONDS + TLS_WAIT_SECONDS))
while true; do
  # --resolve: this PC's resolver may still hold the old answer; the server is what is being checked.
  if answer=$(curl -sS --max-time 10 --resolve "$DOMAIN:443:$HOST" -o /dev/null -D - "https://$DOMAIN/" 2>&1) &&
    grep -qi "^x-xrpl-file-release: $expected" <<<"$answer"; then
    echo "$answer" | tr -d '\r' | grep -i -E '^(HTTP|x-xrpl-file-release)'
    echo "the site is on the server"
    exit 0
  fi
  if ((SECONDS > deadline)); then
    echo "no valid certificate after $((TLS_WAIT_SECONDS / 60)) minutes; Caddy's last lines:" >&2
    "${SSH[@]}" "$TARGET" 'journalctl -u caddy -n 40 --no-pager' >&2 || true
    exit 1
  fi
  sleep 10
done
