# The site on its own server

Status: design, waiting for the owner's approval. Moves the hosting of
`https://xrpl-file.staticbit.io` from Cloudflare Pages to a server the owner rents, and keeps the
Pages setup able to take it back. Builds on the link cards (`2026-11-03-link-cards-design.md`, PR #5).

## Why

The link cards need code on the server: a robot gets HTML written for one file, and `/og/` answers a
picture read from the ledger. On Pages that code is a Pages Function on Workers Paid. The owner chose a
rented server instead:

- a fixed monthly price, with no bill for usage;
- a fixed IP of its own, which public XRPL nodes see as one client, instead of Cloudflare's shared
  addresses that a node may throttle;
- no limit on how long the work after a response may run, where Workers stop at 30 s;
- outbound WebSocket that is known to work, where on Workers it was never checked in production.

## The server

- Address `94.183.189.150`, IPv4 only (no global IPv6 address).
- Ubuntu 24.04 LTS, 1 vCPU, 2 GiB RAM, 19 GiB disk, no swap. Clock synchronised, time zone UTC.
- Bought fresh. `root` logs in by key from the owner's PC; password login is also on, there is no
  firewall, and nothing else is installed.

## Goals

1. The site at `https://xrpl-file.staticbit.io` behaves as it does on Pages: the same pages at the
   same URLs, the same redirects and rewrites (`site/src/_redirects`), the same response headers
   (`site/src/_headers`), the same 404 page.
2. The link cards work as specified in `2026-11-03-link-cards-design.md`, running
   `site/cards/handler.mjs` with its behaviour on Pages unchanged. A new optional `deps` member is
   allowed where the server needs a seam (see "One CPU"); the Pages adapter does not pass it.
3. A push to `main` deploys the site to the server from GitHub Actions, with no step on the owner's PC.
4. The server is prepared from the owner's PC by a script kept in the repository. Running it again on
   a prepared server changes nothing it does not need to.
5. Moving the site back to Pages is one repository variable and one DNS change. The Pages setup from
   PR #5 (`site/functions/`, `site/wrangler.toml`, `site/src/_routes.json`, the Pages deploy job)
   stays in the repository and keeps passing CI.

## Non-goals

- Containers. The server runs Node and Caddy from their packages.
- More than one server, load balancing, or a standby.
- Monitoring and alerting. A later stage may add an outside uptime check.
- Backups. The server holds nothing that is not in git or rebuilt on demand (the card cache).
- IPv6. The server has no IPv6 address, so DNS gets an A record only.
- Access logs. Only errors reach the journal.

## Anti-goals (a failure even if it works)

- **The owner loses SSH access while preparing the server.** Password login is turned off only once
  key login is proven, and a broken `sshd` configuration is never reloaded.
- **The deploy key can do more than deploy.** A stolen copy of the GitHub secret may upload and roll
  back a release and nothing else: no shell, no forwarding, no other command.
- **A secret passes through the repository, the chat or a log.** The deploy key is created on the
  owner's PC, and the owner stores it in GitHub with a command the script prints.
- **A deploy shows visitors an error.** A deploy that starts a release which fails its health check
  puts the previous release back. A restart while visitors are on the site does not answer them with
  a 502.
- **A person waits for the ledger**, as in the link-cards design. The server's single CPU adds a way
  to break this, covered under "One CPU".
- **The Pages path rots unnoticed.** CI keeps bundling the Pages Function even while the server is
  the target.

## How it is built

### Processes

```
internet ──443/80──> Caddy ──127.0.0.1:8080──> node site/server/main.mjs
                     TLS (Let's Encrypt)          static files + link cards
                     compression, retries         card cache in /var/cache/xrpl-file
```

- **Caddy** terminates TLS with a Let's Encrypt certificate it obtains and renews itself, redirects
  HTTP to HTTPS, compresses responses, and proxies everything to Node. While Node restarts during a
  deploy it retries the request for up to 15 s (`lb_try_duration`) instead of answering 502.
  The restart gap is the drain (at most 5 s, below) plus Node's start-up, well inside that window.
- **Node** is the whole site. Nothing in the Caddy configuration knows about pages, rules or robots,
  so the site's behaviour lives in the repository files Pages reads too.

DNS is a plain A record without the Cloudflare proxy (the owner's choice): no CDN in front, the
server's address is public, and Let's Encrypt's HTTP challenge reaches Caddy directly.

### The Node server (`site/server/`)

One process, `node site/server/main.mjs`, listening on `127.0.0.1:8080`. It is built from parts that
already exist and gets the parts Pages used to provide:

1. **Static files with Pages semantics.** `resolveRequest` in `site/serve.mjs` already answers like
   Pages (first matching `_redirects` rule, a 200 rule rewrites, pretty URLs, `index.html` for a
   folder, the 404 page). It moves into a module both the dev server and the production server use;
   the dev server keeps its behaviour.
2. **`_headers`.** A new evaluator for the subset of Pages' `_headers` syntax the site uses: a block
   is a path pattern (`*` as a splat, `:name` as a segment) followed by indented `Name: value` lines;
   every matching block's headers are added to the response, which is the Pages behaviour the comment
   at the top of `site/src/_headers` relies on. The headers apply to every static response, including
   the 404 page.
3. **Conditional requests.** Static responses carry an `ETag` (from size and modification time) and
   answer `If-None-Match` with 304. Pages did this, and the pages are `no-cache`, so without it every
   visit downloads every page again.
4. **Routes for the card handler.** Requests whose path matches an `include` pattern of
   `site/src/_routes.json` go to `handleCardRequest`; everything else is static. The server reads the
   same file Pages reads, so the two hosts route the same paths.
5. **The card handler's `deps`**, as `site/functions/[[path]].js` provides them on Pages:
   - `assets.fetch` and `passThrough`: the static server above, as a `Response`.
   - `cache`: a disk cache under `/var/cache/xrpl-file` (next section).
   - `waitUntil`: the promise is kept in a set. On `SIGTERM` the server stops accepting connections
     and waits for the set, at most 5 s, before it exits. Work still running then is a cache fill (a
     picture read the robot no longer waits for); losing it costs one read on the next request, which
     is cheaper than holding a deploy for the 25 s read ceiling. The unit's `TimeoutStopSec` is 10 s.
   - `openSource`: `new LedgerClient(ENDPOINTS[network])` with `close()`, as on Pages. Node 24 has a
     global `WebSocket`, so `web/src/ledger.mjs` runs unchanged.
   - `origin`: the configured site origin (`SITE_ORIGIN`, default `https://xrpl-file.staticbit.io`),
     never the request's `Host` header. The origin goes into `og:image` URLs and cache keys, and a
     forged `Host` must not put another domain into a cached card.
   - An exception in the handler answers the static response, as `passThroughOnException` does on
     Pages.
6. **Node and the web `Request`/`Response`.** An incoming `node:http` request becomes a `Request`; a
   `Response` is written back with its status, headers and body. `HEAD` gets no body.

### The card cache

`caches.default` does not exist outside Workers. The cache the handler uses on the server:

- An entry is a body and its headers, stored as files named by the SHA-256 of the cache key.
- An entry expires after the `max-age` of its `Cache-Control`, the same lifetimes the handler already
  sets per outcome (a year, an hour, five minutes).
- The cache is capped at 256 MiB. Writing past the cap removes the least recently used entries.
- A file that cannot be read or parsed is a miss and is deleted. A cache that fails is never an error
  for the visitor: the handler already treats a failing `match` or `put` as a miss.
- The cache survives restarts and deploys: it lives outside the release directories.

### One CPU

On Pages every request had its own isolate. Here one Node process serves everyone, so CPU spent
verifying a robot's picture delays a person's page by the same time.

- At most 4 card reads (manifest or picture) run at once. The cap lives in the server's
  `openSource`: past it, `openSource` throws, and the handler already turns that into the generic card
  (`readCardBefore`) or the banner (`readPictureUnderCeiling`) with the five-minute lifetime. The
  slot is released in `close()`, which the handler calls in a `finally` on every path.
- The plan measures how long a 1 MB picture read blocks the event loop on the server (the
  link-cards spec measured about 0.3 s of CPU on Workers). Above 50 ms in one block, picture reads
  move to a worker thread through a new optional dep (for example `deps.readPicture`, defaulting to
  `card.mjs`'s own); the Pages adapter does not pass it. Below 50 ms they stay on the main thread and
  no seam is added.

### Preparing the server

`deploy/server/provision.sh` runs on the owner's PC in Git Bash:

```
deploy/server/provision.sh root@94.183.189.150 [xrpl-file.staticbit.io]
```

The second argument is the site's domain, defaulting to `xrpl-file.staticbit.io`; it fills in the
Caddyfile and the unit's `SITE_ORIGIN`. The server is always addressed by its IP, here and in
deploys: the IP does not change when DNS moves, so one pinned host key serves both.

It runs these steps in this order, and each one checks before it changes anything:

1. **Proves key login and the host key.** The server's host key must already be in the owner's
   `known_hosts` (`StrictHostKeyChecking=yes`), and
   `ssh -o BatchMode=yes -o PasswordAuthentication=no` must succeed. Otherwise the script stops before
   touching the server, and says how to connect once by hand and check the fingerprint. A changed
   host key stops it too.
2. **Creates the deploy key** on the owner's PC (`~/.ssh/xrpl-file-deploy`, ed25519, no passphrase)
   if it does not exist.
3. **Sends the server part** (`deploy/server/setup.sh` and the files it installs) over the proven
   connection and runs it as root:
   - packages: Node 24 from NodeSource's signed apt repository, Caddy from its signed apt
     repository, `ufw`, `unattended-upgrades`;
   - a 1 GiB swap file;
   - users: `xrpl-file` (runs the site, no login shell) and `deploy` (receives releases);
   - the deploy key in `deploy`'s `authorized_keys` as
     `command="/usr/local/bin/xrpl-file-release",restrict ssh-ed25519 …`: whatever the client asks
     to run, the server runs only the release script, which reads the request from
     `SSH_ORIGINAL_COMMAND`;
   - a sudoers rule letting `deploy` run exactly `systemctl restart xrpl-file-site` and nothing else;
   - directories `/srv/xrpl-file/releases` (owned by `deploy`) and `/var/cache/xrpl-file` (owned by
     `xrpl-file`);
   - the systemd unit `xrpl-file-site.service` with `User=xrpl-file`, `ProtectSystem=strict`,
     `ReadWritePaths=/var/cache/xrpl-file`, `NoNewPrivileges`, `PrivateTmp`, `ProtectHome`,
     `MemoryMax=512M`, `Restart=on-failure`;
   - the Caddyfile from the repository, with the domain filled in;
   - `ufw`: deny incoming by default, allow 22, 80 and 443 — 22 is allowed before `ufw` is enabled;
   - `sshd`: a drop-in `/etc/ssh/sshd_config.d/01-xrpl-file.conf` with `PasswordAuthentication no`,
     `KbdInteractiveAuthentication no`, `PermitRootLogin prohibit-password`. The name sorts before
     Ubuntu's `50-cloud-init.conf`, which turns password login on, and in `sshd` the first value of
     a keyword wins. `sshd -t` must pass before `sshd` is reloaded; if it fails, the drop-in is
     removed again.
4. **Pins the host key.** It takes the IP's ed25519 line from the owner's `known_hosts`, already
   proven in step 1, for the deploy to use.
5. **Prints the GitHub commands** for the owner to run: `gh secret set SITE_SSH_KEY < ~/.ssh/xrpl-file-deploy`,
   `gh secret set SITE_SSH_KNOWN_HOSTS` with the pinned line, `gh variable set SITE_HOST` with the
   IP. The script does not run them.

**How the scripts are proven.** CI runs `shellcheck` over them, and runs the release script against a
temporary directory with `sudo`, `systemctl` and the health check replaced by stubs on `PATH` (a
failed check must restore the previous release; a bad request must be refused). The setup itself is
proven on the real server: it is fresh and holds nothing yet, so the first run is the test and a
second run must report no changes. After the run, a new SSH session must log in by key, a password
login must be refused, and the deploy key must be refused any command but the two release requests.
If the hosting panel offers a console, the runbook names it as the way back in.

### Releases on the server

`/usr/local/bin/xrpl-file-release` accepts two requests through `SSH_ORIGINAL_COMMAND` and refuses
anything else:

- **`release <commit>`** (a 40-character hex commit) reads a `.tar.gz` from standard input, unpacks it
  into `/srv/xrpl-file/releases/<UTC time>-<commit7>`, checks that it holds `site/server/main.mjs`
  and `site/dist/index.html`, points `/srv/xrpl-file/current` at it with an atomic rename, restarts
  `xrpl-file-site`, and checks health: `GET http://127.0.0.1:8080/` must answer 200 within 30 s (polled every second). On a
  failed check it points `current` back at the previous release, restarts again and exits non-zero.
  It keeps the five newest releases.
- **`rollback`** points `current` at the release before it and restarts.

The release is the repository's built site and the source files the server imports, laid out as in
the repository so that relative imports work: `site/dist/`, `site/server/`, `site/cards/`, the
`site/*.mjs` modules they import, `site/src/js/config.mjs`, `site/src/_routes.json` and `web/src/`.
The plan names the exact list; a test unpacks a release into an empty directory and starts the server
from it.

### GitHub Actions

- The repository variable **`SITE_TARGET`** chooses where a push to `main` deploys: `cloudflare` (the
  Pages job from PR #5, unchanged) or `server`. Unset means `cloudflare`, which is where the site is
  today, so merging this work changes nothing until the owner sets the variable. Any other value
  fails the deploy with a message naming the variable, so a misspelt target never skips a deploy
  silently.
- The server deploy is a separate workflow, `deploy-site-server.yml`, run by the `main` push when
  `SITE_TARGET` is `server` (`workflow_call`) and by hand (`workflow_dispatch`). Running it by hand is
  how the first release reaches the server while DNS still points at Pages.
- It builds the site (`web/build.mjs`, `site/build.mjs`), packs the release, and runs
  `ssh deploy@$SITE_HOST "release $GITHUB_SHA" < release.tar.gz` with `SITE_SSH_KEY` and
  `SITE_SSH_KNOWN_HOSTS`. Missing secrets fail the job with their names.
- The `unit` job keeps building the Pages Function bundle whatever the target, and gains
  `shellcheck` over `deploy/server/*.sh`.

### Moving the site (owner steps)

1. Run `deploy/server/provision.sh root@94.183.189.150` and the `gh` commands it prints.
2. Run `deploy-site-server` by hand on `main`. Check the site through an SSH tunnel to
   `127.0.0.1:8080`: the pages, a file page, a robot card, `/og/`.
3. In Cloudflare DNS, delete the Pages project's `xrpl-file` CNAME record and add an A record
   `xrpl-file → 94.183.189.150` with the proxy off (grey cloud). Then remove the custom domain from
   the Pages project. This is the order Cloudflare documents for detaching a custom domain
   (developers.cloudflare.com/pages/configuration/custom-domains, "Delete a custom domain"). The zone
   has no CAA records, so Let's Encrypt may issue for the name (checked 2026-11-12).
4. Caddy obtains the certificate once the name resolves to the server; `systemctl reload caddy`
   makes it try at once rather than at its next retry.
5. Set `SITE_TARGET=server`.

Until step 3, visitors keep getting Pages. Between steps 3 and 4 a visitor whose resolver already has
the new address gets a TLS error for at most a few minutes.

**Back to Pages:** set `SITE_TARGET=cloudflare` and deploy, so the Pages project serves the current
site; delete the A record; add `xrpl-file.staticbit.io` as a custom domain of the Pages project again,
which creates the proxied CNAME. Until Pages shows the domain as active, visitors get errors, so this
is done at a quiet hour. Workers Paid from the link-cards design applies again then.

## What it costs

- The server: €2.99 a month (Burstable, 1 CPU, 2 GiB, 1 TB of traffic, then $1 per TB).
- No Workers Paid. The Pages project stays and costs nothing while no traffic reaches it.

## Left to the builder

- Module boundaries inside `site/server/`, the cache's file layout, and names not given above.
- Whether the release script also cleans up a release directory left by a failed unpack.
- How the Node adapter streams large bodies (pictures are at most 1 MB).
- The wording of the runbook in `site/README.md` and the deploy note, in English.
