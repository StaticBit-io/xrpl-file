#!/usr/bin/env bash
# Prepares the site server. Runs as root on the server, sent there by deploy/server/provision.sh:
#
#   setup.sh <bundle dir> <domain>
#
# The bundle holds this script, the files it installs, and deploy.pub (the deploy key). Every step
# checks before it changes anything and prints "ok <step>" or "changed <step>"; the last line is
# "changed: <n>", so a second run on a prepared server reports 0. A line "armed sshd-revert" means the
# sshd policy changed and a timer will undo it in five minutes unless provision.sh disarms it after a
# fresh key login. Spec: docs/superpowers/specs/2026-11-12-site-server-design.md.
set -euo pipefail

BUNDLE=${1:?usage: setup.sh <bundle dir> <domain>}
DOMAIN=${2:?usage: setup.sh <bundle dir> <domain>}
NODE_MAJOR=24
export DEBIAN_FRONTEND=noninteractive

changed=0
report() { # report ok|changed <step>
  [[ $1 == changed ]] && changed=$((changed + 1))
  echo "$1 $2"
}

# put <mode> <owner:group> <destination> <source file>: installs only when the content differs.
put() {
  local mode=$1 owner=$2 destination=$3 source=$4
  if [[ -f $destination ]] && cmp -s "$source" "$destination" &&
    [[ $(stat -c '%a %U:%G' "$destination") == "${mode#0} $owner" ]]; then
    return 1
  fi
  install -D -m "$mode" -o "${owner%%:*}" -g "${owner##*:}" "$source" "$destination"
}

# put_text <mode> <owner:group> <destination> <<< text
put_text() {
  local temp status=0
  temp=$(mktemp)
  cat >"$temp"
  put "$1" "$2" "$3" "$temp" || status=$?
  rm -f "$temp"
  return "$status"
}

step_packages() {
  local repos=0 prerequisites=()
  for package in ca-certificates curl gnupg; do
    dpkg -s "$package" >/dev/null 2>&1 || prerequisites+=("$package")
  done
  if ((${#prerequisites[@]})); then
    # A fresh server may have no package lists yet.
    apt-get update -qq
    apt-get install -y -qq "${prerequisites[@]}" >/dev/null
    repos=1
  fi
  install -d -m 0755 /etc/apt/keyrings
  if [[ ! -s /etc/apt/keyrings/nodesource.gpg ]]; then
    curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg
    repos=1
  fi
  if put_text 0644 root:root /etc/apt/sources.list.d/nodesource.sources <<EOF; then repos=1; fi
Types: deb
URIs: https://deb.nodesource.com/node_${NODE_MAJOR}.x/
Suites: nodistro
Components: main
Signed-By: /etc/apt/keyrings/nodesource.gpg
EOF
  if [[ ! -s /usr/share/keyrings/caddy-stable-archive-keyring.gpg ]]; then
    curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/gpg.key | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
    chmod o+r /usr/share/keyrings/caddy-stable-archive-keyring.gpg
    repos=1
  fi
  if [[ ! -s /etc/apt/sources.list.d/caddy-stable.list ]]; then
    curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt >/etc/apt/sources.list.d/caddy-stable.list
    chmod o+r /etc/apt/sources.list.d/caddy-stable.list
    repos=1
  fi
  local missing=()
  for package in nodejs caddy ufw unattended-upgrades; do
    dpkg -s "$package" >/dev/null 2>&1 || missing+=("$package")
  done
  if ((repos)) || ((${#missing[@]})); then
    apt-get update -qq
    ((${#missing[@]})) && apt-get install -y -qq "${missing[@]}" >/dev/null
    report changed packages
  else
    report ok packages
  fi
  [[ $(node -p 'process.versions.node.split(".")[0]') == "$NODE_MAJOR" ]] || {
    echo "node $(node --version) is not ${NODE_MAJOR}.x" >&2
    exit 1
  }
}

step_updates() {
  if put_text 0644 root:root /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'; then report changed security-updates; else report ok security-updates; fi
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
}

step_swap() {
  if swapon --show=NAME --noheadings | grep -qx /swapfile; then
    report ok swap
    return
  fi
  [[ -f /swapfile ]] || fallocate -l 1G /swapfile
  chmod 600 /swapfile
  mkswap /swapfile >/dev/null
  swapon /swapfile
  grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >>/etc/fstab
  report changed swap
}

step_users() {
  local did=0
  if ! id xrpl-file >/dev/null 2>&1; then
    useradd --system --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin xrpl-file
    did=1
  fi
  if ! id deploy >/dev/null 2>&1; then
    # A home deploy cannot write: no ~/.ssh of its own to add a key to.
    useradd --home-dir /var/lib/xrpl-file-deploy --no-create-home --shell /bin/bash deploy
    did=1
  fi
  install -d -m 0755 -o root -g root /var/lib/xrpl-file-deploy
  if [[ $(passwd -S deploy | awk '{print $2}') != L ]]; then
    passwd -l deploy >/dev/null
    did=1
  fi
  if ((did)); then report changed users; else report ok users; fi
}

step_deploy_key() {
  local key
  key=$(head -1 "$BUNDLE/deploy.pub")
  [[ $key == ssh-ed25519\ * ]] || {
    echo "deploy.pub is not an ed25519 public key" >&2
    exit 1
  }
  if put_text 0644 root:root /etc/ssh/authorized_keys/deploy <<<"command=\"/usr/local/bin/xrpl-file-release\",restrict $key"; then
    report changed deploy-key
  else
    report ok deploy-key
  fi
}

step_sudoers() {
  if [[ -f /etc/sudoers.d/xrpl-file-deploy ]] && cmp -s "$BUNDLE/sudoers-xrpl-file-deploy" /etc/sudoers.d/xrpl-file-deploy; then
    report ok sudoers
    return
  fi
  visudo -cf "$BUNDLE/sudoers-xrpl-file-deploy" >/dev/null
  install -m 0440 -o root -g root "$BUNDLE/sudoers-xrpl-file-deploy" /etc/sudoers.d/xrpl-file-deploy
  report changed sudoers
}

step_directories() {
  local before after
  before=$(stat -c '%a %U' /srv/xrpl-file /srv/xrpl-file/releases /var/cache/xrpl-file /etc/xrpl-file 2>/dev/null || true)
  install -d -m 0755 -o deploy -g deploy /srv/xrpl-file /srv/xrpl-file/releases
  install -d -m 0750 -o xrpl-file -g xrpl-file /var/cache/xrpl-file
  install -d -m 0755 -o root -g root /etc/xrpl-file
  after=$(stat -c '%a %U' /srv/xrpl-file /srv/xrpl-file/releases /var/cache/xrpl-file /etc/xrpl-file)
  if [[ $before != "$after" ]]; then report changed directories; else report ok directories; fi
}

step_scripts() {
  local did=0
  put 0755 root:root /usr/local/bin/xrpl-file-release "$BUNDLE/xrpl-file-release" && did=1
  put 0755 root:root /usr/local/bin/xrpl-file-smoke "$BUNDLE/xrpl-file-smoke" && did=1
  if ((did)); then report changed release-scripts; else report ok release-scripts; fi
}

step_unit() {
  local did=0
  put 0644 root:root /etc/systemd/system/xrpl-file-site.service "$BUNDLE/xrpl-file-site.service" && did=1
  put_text 0644 root:root /etc/xrpl-file/site.env <<<"SITE_ORIGIN=https://$DOMAIN" && did=1
  if ((did)); then
    systemctl daemon-reload
    # A unit already running keeps the old environment until restarted; before the first release
    # there is nothing to restart.
    [[ -e /srv/xrpl-file/current ]] && systemctl restart xrpl-file-site
  fi
  if ! systemctl is-enabled --quiet xrpl-file-site; then
    systemctl enable --quiet xrpl-file-site
    did=1
  fi
  if ((did)); then report changed site-unit; else report ok site-unit; fi
}

step_caddy() {
  local did=0
  install -d -m 0755 -o root -g root /etc/caddy/sites
  put 0644 root:root /etc/caddy/Caddyfile "$BUNDLE/Caddyfile" && did=1
  put 0644 root:root /etc/systemd/system/caddy.service.d/10-xrpl-file.conf "$BUNDLE/caddy-10-xrpl-file.conf" && did=1
  if ((did)); then
    caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null 2>&1 || {
      echo "the Caddyfile does not validate" >&2
      exit 1
    }
    systemctl daemon-reload
    # The admin address moved, which a reload through the old address cannot do: restart.
    systemctl restart caddy
    report changed caddy
  else
    report ok caddy
  fi
}

step_firewall() {
  local status
  status=$(ufw status verbose 2>/dev/null || true)
  if grep -q '^Status: active' <<<"$status" && grep -q 'Default: deny (incoming)' <<<"$status" &&
    grep -qE '^22/tcp +ALLOW IN' <<<"$status" && grep -qE '^80/tcp +ALLOW IN' <<<"$status" &&
    grep -qE '^443/tcp +ALLOW IN' <<<"$status"; then
    report ok firewall
    return
  fi
  # 22 first: enabling ufw without it would cut this very session.
  ufw allow 22/tcp >/dev/null
  ufw allow 80/tcp >/dev/null
  ufw allow 443/tcp >/dev/null
  ufw default deny incoming >/dev/null
  ufw default allow outgoing >/dev/null
  ufw --force enable >/dev/null
  report changed firewall
}

# policy_holds <user>: what sshd will apply to that user, not just that the file parses.
policy_holds() {
  local effective
  effective=$(sshd -T -C "user=$1,host=localhost,addr=127.0.0.1")
  grep -qx 'passwordauthentication no' <<<"$effective" &&
    grep -qx 'kbdinteractiveauthentication no' <<<"$effective" &&
    grep -qx 'pubkeyauthentication yes' <<<"$effective" &&
    grep -qE '^permitrootlogin (prohibit-password|without-password)$' <<<"$effective"
}

step_sshd() {
  local target=/etc/ssh/sshd_config.d/01-xrpl-file.conf
  if [[ -f $target ]] && cmp -s "$BUNDLE/sshd-01-xrpl-file.conf" "$target"; then
    report ok sshd
    return
  fi
  install -m 0644 -o root -g root "$BUNDLE/sshd-01-xrpl-file.conf" "$target"
  if ! sshd -t || ! policy_holds root || ! policy_holds deploy; then
    rm -f "$target"
    echo "the sshd policy did not check out; the drop-in was removed and sshd left as it was" >&2
    exit 1
  fi
  # Undone in five minutes unless provision.sh proves a fresh key login and stops this timer.
  systemctl stop xrpl-file-sshd-revert.timer >/dev/null 2>&1 || true
  systemd-run --unit=xrpl-file-sshd-revert --on-active=5min \
    /bin/sh -c "rm -f $target && systemctl try-reload-or-restart ssh.service" >/dev/null
  echo "armed sshd-revert"
  systemctl try-reload-or-restart ssh.service
  report changed sshd
}

step_packages
step_updates
step_swap
step_users
step_deploy_key
step_sudoers
step_directories
step_scripts
step_unit
step_caddy
step_firewall
step_sshd
echo "changed: $changed"
