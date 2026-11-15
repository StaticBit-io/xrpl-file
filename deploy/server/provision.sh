#!/usr/bin/env bash
# Prepares the site server from the owner's PC (Git Bash):
#
#   deploy/server/provision.sh root@94.183.189.150 [xrpl-file.staticbit.io]
#
# Proves key login, creates the deploy key, runs setup.sh on the server, proves the new sshd policy
# with a fresh login, and prints the GitHub commands that store the deploy key. It never runs them:
# the key leaves this PC only through the owner's own `gh secret set`.
# Spec: docs/superpowers/specs/2026-11-12-site-server-design.md, "Preparing the server".
set -euo pipefail

TARGET=${1:?usage: provision.sh <root@ip> [domain]}
DOMAIN=${2:-xrpl-file.staticbit.io}
HOST=${TARGET#*@}
HERE=$(cd "$(dirname "$0")" && pwd)
KEY=$HOME/.ssh/xrpl-file-deploy
KNOWN_HOSTS=$HOME/.ssh/known_hosts

[[ $HOST =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || {
  echo "address the server by its IP (the deploy pins the host key by IP): got '$HOST'" >&2
  exit 2
}

# Every connection is a real login: a multiplexed master could stand in for one.
SSH=(ssh -o ControlMaster=no -o ControlPath=none -o BatchMode=yes -o PasswordAuthentication=no
  -o StrictHostKeyChecking=yes -o HostKeyAlgorithms=ssh-ed25519 -o ConnectTimeout=15)

step() { printf '\n== %s\n' "$*"; }

step "key login to $TARGET"
if ! "${SSH[@]}" "$TARGET" true; then
  cat >&2 <<EOF
Key login failed, or the server's ed25519 host key is not in $KNOWN_HOSTS (or has changed).
Nothing on the server was touched. Connect once by hand, compare the fingerprint with the one
the hosting panel shows, and run this script again:

  ssh -o HostKeyAlgorithms=ssh-ed25519 $TARGET
EOF
  exit 1
fi
echo ok

step "deploy key $KEY"
if [[ ! -e $KEY && ! -e $KEY.pub ]]; then
  ssh-keygen -q -t ed25519 -N '' -C xrpl-file-deploy -f "$KEY"
  echo created
else
  derived=$(ssh-keygen -y -P '' -f "$KEY" 2>/dev/null) || {
    echo "$KEY has a passphrase or cannot be read; Actions cannot use it. Delete $KEY and $KEY.pub to make a new one." >&2
    exit 1
  }
  [[ $(cut -d' ' -f1,2 <<<"$derived") == $(cut -d' ' -f1,2 <"$KEY.pub") ]] || {
    echo "$KEY.pub does not match $KEY. Delete both to make a new key." >&2
    exit 1
  }
  echo reused
fi

step "setup on the server"
bundle=$(mktemp -d)
trap 'rm -rf "$bundle"' EXIT
cp "$HERE/setup.sh" "$HERE/xrpl-file-release" "$HERE/xrpl-file-smoke" "$HERE/xrpl-file-site.service" \
  "$HERE/Caddyfile" "$HERE/caddy-10-xrpl-file.conf" "$HERE/sshd-01-xrpl-file.conf" \
  "$HERE/sudoers-xrpl-file-deploy" "$bundle/"
cp "$KEY.pub" "$bundle/deploy.pub"
output=$(tar -czf - -C "$bundle" . | "${SSH[@]}" "$TARGET" \
  "d=\$(mktemp -d) && tar -xzf - -C \"\$d\" && bash \"\$d/setup.sh\" \"\$d\" '$DOMAIN'; rc=\$?; rm -rf \"\$d\"; exit \$rc" 2>&1) || {
  echo "$output" >&2
  echo "setup failed on the server" >&2
  exit 1
}
echo "$output"

if grep -qx 'armed sshd-revert' <<<"$output"; then
  step "fresh key login under the new sshd policy"
  if "${SSH[@]}" "$TARGET" 'systemctl stop xrpl-file-sshd-revert.timer'; then
    echo "ok, the revert timer is stopped"
  else
    echo "a fresh key login failed: the old sshd policy comes back within five minutes" >&2
    exit 1
  fi
fi

step "password login is refused"
refusal=$(ssh -o ControlMaster=no -o ControlPath=none -o BatchMode=yes -o PubkeyAuthentication=no \
  -o PreferredAuthentications=password,keyboard-interactive -o StrictHostKeyChecking=yes "$TARGET" true 2>&1 || true)
if grep -q 'Permission denied (publickey)' <<<"$refusal"; then
  echo ok
else
  echo "unexpected answer to a password login: $refusal" >&2
  exit 1
fi

step "the deploy key runs nothing but the release script"
answer=$(ssh -o ControlMaster=no -o ControlPath=none -o BatchMode=yes -o StrictHostKeyChecking=yes \
  -o IdentitiesOnly=yes -i "$KEY" "deploy@$HOST" id 2>&1 || true)
if grep -q 'refused' <<<"$answer"; then
  echo ok
else
  echo "the deploy key answered something other than a refusal: $answer" >&2
  exit 1
fi

step "pinned host key"
pinned=$(ssh-keygen -F "$HOST" -f "$KNOWN_HOSTS" | awk -v host="$HOST" '$2 == "ssh-ed25519" { print host " " $2 " " $3; exit }')
[[ -n $pinned ]] || {
  echo "no ed25519 line for $HOST in $KNOWN_HOSTS" >&2
  exit 1
}
echo "$pinned"

step "store the deploy access in GitHub (run these yourself, from the repository)"
cat <<EOF
gh secret set SITE_SSH_KEY < "$KEY"
gh secret set SITE_SSH_KNOWN_HOSTS --body '$pinned'
gh variable set SITE_HOST --body '$HOST'
EOF
