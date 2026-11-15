// The files setup.sh installs on the server say what the spec says
// (docs/superpowers/specs/2026-11-12-site-server-design.md, "Preparing the server").
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const read = (name) => readFile(new URL(`../${name}`, import.meta.url), 'utf8');
const lines = (text) => text.split(/\r?\n/).map((l) => l.trim()).filter((l) => l && !l.startsWith('#'));

test('the unit is sandboxed as the spec says', async () => {
  const unit = lines(await read('xrpl-file-site.service'));
  for (const expected of [
    'User=xrpl-file',
    'ExecStart=/usr/bin/node /srv/xrpl-file/current/site/server/main.mjs',
    'ProtectSystem=strict',
    'ReadWritePaths=/var/cache/xrpl-file',
    'NoNewPrivileges=yes',
    'PrivateTmp=yes',
    'ProtectHome=yes',
    'CapabilityBoundingSet=',
    'MemoryMax=512M',
    'Restart=on-failure',
    'TimeoutStopSec=10',
  ]) {
    assert.ok(unit.includes(expected), expected);
  }
});

test('sshd turns password login off before cloud-init turns it on', async () => {
  const name = 'sshd-01-xrpl-file.conf';
  // Installed as /etc/ssh/sshd_config.d/01-xrpl-file.conf; the first value of a keyword wins.
  assert.ok('01-xrpl-file.conf' < '50-cloud-init.conf');
  const conf = lines(await read(name));
  for (const expected of ['PasswordAuthentication no', 'KbdInteractiveAuthentication no', 'PermitRootLogin prohibit-password']) {
    assert.ok(conf.includes(expected), expected);
  }
  // The deploy key lives where the deploy user cannot write, so code run as deploy cannot lift the
  // forced command.
  assert.ok(conf.includes('AuthorizedKeysFile .ssh/authorized_keys /etc/ssh/authorized_keys/%u'));
});

test('deploy may restart the site and nothing else', async () => {
  assert.deepEqual(lines(await read('sudoers-xrpl-file-deploy')), ['deploy ALL=(root) NOPASSWD: /usr/bin/systemctl restart xrpl-file-site']);
});

test('Caddy keeps its admin API off TCP', async () => {
  const caddyfile = await read('Caddyfile');
  assert.match(caddyfile, /admin unix\/\/run\/caddy\/admin\.sock/);
  assert.doesNotMatch(caddyfile, /localhost:2019|:2019/);
  assert.match(caddyfile, /import \/etc\/caddy\/sites\/\*\.caddy/);
  const site = await read('site.caddy');
  assert.match(site, /reverse_proxy 127\.0\.0\.1:8080/);
  assert.match(site, /lb_try_duration 15s/);
  const dropIn = lines(await read('caddy-10-xrpl-file.conf'));
  assert.ok(dropIn.includes('RuntimeDirectory=caddy'));
  assert.ok(dropIn.includes('RuntimeDirectoryMode=0700'));
});

test('setup arms the sshd fallback before it reloads sshd', async () => {
  const setup = await read('setup.sh');
  const arm = setup.indexOf('systemd-run --unit=xrpl-file-sshd-revert --on-active=5min');
  const check = setup.indexOf('sshd -T -C');
  const reload = setup.indexOf('systemctl try-reload-or-restart ssh');
  assert.ok(arm > 0 && check > 0 && reload > 0, 'all three present');
  assert.ok(check < arm && arm < reload, 'policy checked, then fallback armed, then sshd reloaded');
});

test('provision proves a real key login before anything else', async () => {
  const provision = await read('provision.sh');
  for (const option of ['ControlMaster=no', 'ControlPath=none', 'BatchMode=yes', 'PasswordAuthentication=no', 'StrictHostKeyChecking=yes', 'HostKeyAlgorithms=ssh-ed25519']) {
    assert.ok(provision.includes(option), option);
  }
  // It prints the gh commands (inside a heredoc); it never runs them.
  assert.match(provision, /gh secret set SITE_SSH_KEY/);
  const outsideHeredocs = provision.replace(/<<'?EOF'?\n[\s\S]*?\nEOF\n/g, '');
  assert.doesNotMatch(outsideHeredocs, /^\s*gh /m);
});
