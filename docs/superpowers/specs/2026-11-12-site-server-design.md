# The site on its own server

Status: design, waiting for the owner's approval. Moves the hosting of
`https://xrpl-file.staticbit.io` from Cloudflare Pages to a server the owner rents, and keeps the
Pages setup able to take it back. Builds on the link cards (`2026-11-03-link-cards-design.md`, PR #5).

## Why

The link cards need code on the server: a robot gets HTML written for one file, and `/og/` answers a
picture read from the ledger. On Pages that code is a Pages Function on Workers Paid. The owner chose a
rented server instead:

- a fixed monthly price, with no charge per request (traffic beyond the included 1 TB is billed);
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
   (`site/src/_headers` plus Pages' defaults, listed below), the same 404 page.
2. The link cards work as specified in `2026-11-03-link-cards-design.md`, running
   `site/cards/handler.mjs` with its behaviour on Pages unchanged. A new optional `deps` member is
   allowed where the server needs a seam (see "One CPU"); the Pages adapter does not pass it.
3. A push to `main` deploys the site to the server from GitHub Actions, with no step on the owner's PC.
4. The server is prepared from the owner's PC by a script kept in the repository. Running it again on
   a prepared server changes nothing it does not need to.
5. The Pages setup from PR #5 (`site/functions/`, `site/wrangler.toml`, `site/src/_routes.json`, the
   Pages deploy job) stays in the repository and keeps passing CI, so the site can move back.

## Non-goals

- Containers. The server runs Node and Caddy from their packages.
- More than one server, load balancing, or a standby.
- Monitoring and alerting. A later stage may add an outside uptime check.
- Backups. The server holds nothing that is not in git or rebuilt on demand (the card cache).
- IPv6. The server has no IPv6 address, so DNS gets an A record only.
- Access logs. Only errors reach the journal.
- Keeping the assets of an older release reachable after a deploy. Pages does not do it either: a page
  loaded just before a deploy fetches its scripts from the new release.

## Anti-goals (a failure even if it works)

- **The owner loses SSH access while preparing the server.** Key login is proven before anything
  changes, the new `sshd` policy is checked as it will apply, and the change undoes itself unless a
  fresh key login succeeds after it.
- **A stolen deploy key reaches beyond the site.** The deploy key can upload a release, and a release
  is code, so whoever holds the key can run code as the site's service user. That is accepted, and
  bounded: no shell for `deploy`, no forwarding, no command but the release script; the service user
  cannot write the code it runs, the release script, the unit, Caddy's configuration or its admin
  API, and cannot gain privileges.
- **A secret passes through the repository, the chat or a log.** The deploy key is created on the
  owner's PC, and the owner stores it in GitHub with a command the script prints.
- **A deploy shows visitors a broken release.** A release is started and checked on a side port
  before visitors are switched to it; a release that fails after the switch is replaced by the last
  good one. An interrupted deploy is repaired by the next run.
- **A person waits for the ledger**, as in the link-cards design. The server's single CPU adds a way
  to break this, covered under "One CPU".
- **The Pages path rots unnoticed.** CI keeps bundling the Pages Function even while the server is
  the target.

## How it is built

### Processes

```
internet ──443/80──> Caddy ──127.0.0.1:8080──> node site/server/main.mjs
                     TLS (ACME)                   static files + link cards
                     compression, retries         card cache in /var/cache/xrpl-file
```

- **Caddy** terminates TLS with a certificate it obtains and renews itself over ACME (Caddy's default
  issuers: Let's Encrypt, with ZeroSSL as fallback), redirects HTTP to HTTPS, compresses responses,
  and proxies everything to Node. A connection refused while Node restarts is retried for up to 15 s
  (`lb_try_duration`); the old process finishes the responses it has started before it exits (see
  "Shutdown"). Caddy's admin API listens on a Unix socket only the `caddy` user can open, not on
  `localhost:2019`, so no other local user can reconfigure it.
- **Node** is the whole site. Nothing in the Caddy configuration knows about pages, rules or robots,
  so the site's behaviour lives in the repository files Pages reads too.

DNS is a plain A record without the Cloudflare proxy (the owner's choice): no CDN in front, the
server's address is public, and the ACME HTTP challenge reaches Caddy directly.

### The Node server (`site/server/`)

One process, `node site/server/main.mjs`, listening on `127.0.0.1:8080` (the port comes from the
environment, so the release script can start a candidate on another). At start-up it resolves the
real path of its site directory once and serves that release for its whole life, parses `_redirects`,
`_headers` and `_routes.json`, and exits with an error if any of them does not parse. Every response
carries `X-Xrpl-File-Release: <commit>` from the environment, so a check can tell which release
answered.

1. **Static files with Pages semantics.** `resolveRequest` in `site/serve.mjs` already follows Pages
   for `_redirects` (first match, a 200 rule rewrites), pretty URLs and folders. It moves into a module
   both servers use, with the differences from Pages fixed in the production path and checked against
   the live Pages site (measured 2026-11-12):
   - `/_redirects`, `/_headers` and `/_routes.json` answer 404: Pages does not publish them.
   - The HTML redirects (`/x.html` → `/x`, `/x/index.html` → `/x/`) are 308, and keep the query
     string (`/index.html?x=1` → `/?x=1`).
   - The 404 page is the root `404.html`, the only one the site has; the build already fails without
     it.
   - A missing file (`ENOENT`, `ENOTDIR`) is a miss; any other error reading a file is a 500 and a line
     in the journal, never a quiet 404.
   The dev server keeps its own behaviour (`no-store`, 127.0.0.1, free port).
2. **Headers.** Pages' defaults first: `Cache-Control: public, max-age=0, must-revalidate` on every
   static response and `no-store` on the 404 page. Then `_headers`: a new evaluator for the subset of
   its syntax the site uses (a path pattern with `*` as a splat and `:name` as a segment, followed by
   indented `Name: value` lines); every block that matches the requested path adds its headers, which
   is the Pages behaviour the comment at the top of `site/src/_headers` relies on. Where a block sets a
   header Pages sets by default, the block's value replaces the default. The plan pins this with a
   table of paths and the headers the live Pages site returns for them today.
3. **Conditional requests.** Static responses carry an `ETag` made from a hash of the file's content,
   computed once per file per process. `If-None-Match` (a list, `*`, weak comparison) on `GET` or `HEAD`
   answers 304. Pages does the same, and the pages must revalidate, so without it every visit
   downloads every page again.
4. **Routes for the card handler.** A request goes to `handleCardRequest` when its path matches an
   `include` pattern of `site/src/_routes.json` and no `exclude` pattern (exclude wins, as on Pages);
   everything else is static. The server reads the file Pages reads, so the two hosts route the same
   paths.
5. **The card handler's `deps`**, as `site/functions/[[path]].js` provides them on Pages:
   - `assets.fetch` and `passThrough`: the static server above, as a `Response`.
   - `cache`: the disk cache below.
   - `waitUntil`: the promise is kept in a set until it settles (see "Shutdown").
   - `openSource`: `new LedgerClient(ENDPOINTS[network])` with `close()`, as on Pages, behind the cap
     in "One CPU". Node 24 has a global `WebSocket`, so `web/src/ledger.mjs` runs unchanged.
   - `origin`: the configured site origin (`SITE_ORIGIN`, default `https://xrpl-file.staticbit.io`),
     never the request's `Host` header. The origin goes into `og:image` URLs and cache keys, and a
     forged `Host` must not put another domain into a cached card.
   - An exception in the handler answers the static response, as `passThroughOnException` does on
     Pages.
6. **Node and the web `Request`/`Response`.** An incoming `node:http` request becomes a `Request`; a
   `Response` is written back with its status, headers and body. `HEAD` gets no body.

### Shutdown

On `SIGTERM` the server stops accepting connections, lets the responses already being written finish,
and waits for the `waitUntil` set, all within 5 s; then it closes what is left and exits. Work still
running then is a cache fill (a picture read the robot no longer waits for); losing it costs one read
on the next request, which is cheaper than holding a deploy for the 25 s read ceiling. The unit's
`TimeoutStopSec` is 10 s.

### The card cache

`caches.default` does not exist outside Workers. The cache the handler uses on the server:

- An entry is one file holding its headers and body, named by the SHA-256 of the cache key. It is
  written to a temporary file and renamed into place, so a reader sees a whole entry or none, and the
  last of two writes of the same key wins whole.
- An entry expires after the `max-age` of its `Cache-Control`, the lifetimes the handler already sets
  per outcome (a year, an hour, five minutes).
- `match` never rejects and `put` never throws: any error is a miss (`undefined`) or a skipped write,
  and a line in the journal. This matters because the handler does not treat a failing `match` as a
  miss: a rejected `match` sends the robot straight to the generic card, without a ledger read
  (`site/cards/handler.mjs`, `cardPage` and `cardPicture`). An entry that cannot be read or parsed is
  deleted.
- The cache is capped at 256 MiB, temporary files included. Writing past the cap removes the least
  recently used entries.
- A card page embeds the built file page, so a cached card from an older build would keep that
  build's markup for a year. Page entries are therefore kept under the hash of the built `file.html`:
  a release with a different file page starts with no cached card pages. Picture entries do not
  depend on the build and are kept.
- The cache lives in `/var/cache/xrpl-file`, outside the release directories, and survives restarts
  and deploys.

### One CPU

On Pages the function ran on Cloudflare's machines, beside nothing of the site's. Here one Node
process on one CPU serves everyone, so CPU spent verifying a robot's picture delays a person's page.

- **Reads.** At most 4 card reads (manifest or picture) run at once. The cap lives in the server's
  `openSource`: past it, `openSource` throws, and the handler already turns that into the generic card
  (`readCardBefore`) or the banner (`readPictureUnderCeiling`) with the five-minute lifetime. The slot
  is released in `close()`, which the handler calls in a `finally` on every path.
- **Requests.** At most 32 requests are inside the card handler at once. Past that, a robot gets the
  static response at once, with nothing read or cached.
- **Measured, then decided.** The plan measures, on the server, how much four concurrent 1 MB picture
  reads delay a person's page request (the link-cards spec measured about 0.3 s of CPU per read on
  Workers). If the added delay exceeds 100 ms, picture reads move to a worker thread; otherwise they
  stay on the main thread and no seam is added.
- **The worker, if needed.** One worker thread owns a picture read from end to end: it receives the
  network and hash, opens its own `LedgerClient`, runs `readPicture` under its own ceiling and returns
  the picture and outcome. The main thread counts the read against the cap and passes it in through a
  new optional dep (for example `deps.readPicture`); the Pages adapter does not pass it. A worker that
  dies answers `transient` and is started again.

### Preparing the server

`deploy/server/provision.sh` runs on the owner's PC in Git Bash:

```
deploy/server/provision.sh root@94.183.189.150 [xrpl-file.staticbit.io]
```

The second argument is the site's domain, defaulting to `xrpl-file.staticbit.io`; it becomes the
unit's `SITE_ORIGIN` and, at cut-over, the Caddy site. The server is always addressed by its IP, here
and in deploys: the IP does not change when DNS moves, so one pinned host key serves both.

Every SSH connection the script makes uses `-o ControlMaster=no -o ControlPath=none`, so a reused
connection never stands in for a real login. It runs these steps in this order:

1. **Proves key login and the host key.** `ssh -o BatchMode=yes -o PasswordAuthentication=no
   -o StrictHostKeyChecking=yes -o HostKeyAlgorithms=ssh-ed25519` must succeed. That requires the
   server's ed25519 host key in the owner's `known_hosts`; otherwise, or when the key changed, the
   script stops before touching the server and says how to connect once by hand and check the
   fingerprint.
2. **Prepares the deploy key** on the owner's PC: `~/.ssh/xrpl-file-deploy`, ed25519, no passphrase.
   An existing key is reused only if it has no passphrase and its `.pub` matches it; otherwise the
   script stops. Rotating the key is: delete both files, run the script again, update the secret.
3. **Runs the server part** (`deploy/server/setup.sh` and the files it installs) as root over the
   proven connection. Each step checks before it changes anything:
   - packages: Node 24 from NodeSource's signed apt repository, Caddy from its signed apt
     repository, `ufw`, `unattended-upgrades`;
   - a 1 GiB swap file;
   - users: `xrpl-file` runs the site and has no login shell; `deploy` receives releases, has a locked
     password and `/bin/bash` (the forced command runs through the user's shell);
   - the deploy key in `deploy`'s `authorized_keys` as
     `command="/usr/local/bin/xrpl-file-release",restrict ssh-ed25519 …`: whatever the client asks
     to run, the server runs only the release script, which reads the request from
     `SSH_ORIGINAL_COMMAND`;
   - `/etc/sudoers.d/xrpl-file-deploy`: `deploy ALL=(root) NOPASSWD: /usr/bin/systemctl restart
     xrpl-file-site`, nothing else, checked with `visudo -cf` before it is installed; the release
     script calls `sudo -n`;
   - ownership: `/srv/xrpl-file` and everything under it belong to `deploy` (it must replace the
     `current` link) and are readable, not writable, by `xrpl-file`; `/var/cache/xrpl-file` belongs to
     `xrpl-file`; the release script, the unit, the sudoers file, the `sshd` drop-in and Caddy's
     configuration belong to root;
   - the systemd unit `xrpl-file-site.service`: `User=xrpl-file`, `ProtectSystem=strict`,
     `ReadWritePaths=/var/cache/xrpl-file`, `NoNewPrivileges`, `PrivateTmp`, `ProtectHome`,
     `CapabilityBoundingSet=` (empty), `MemoryMax=512M`, `Restart=on-failure`, `TimeoutStopSec=10`;
   - Caddy with its admin API on a Unix socket and no site yet: until cut-over (below) it serves
     nothing public, so it never asks for a certificate while the name still points at Pages;
   - `ufw`: deny incoming by default, allow 22, 80 and 443; 22 is allowed before `ufw` is enabled;
   - `sshd`: a drop-in `/etc/ssh/sshd_config.d/01-xrpl-file.conf` with `PasswordAuthentication no`,
     `KbdInteractiveAuthentication no`, `PermitRootLogin prohibit-password`. The name sorts before
     Ubuntu's `50-cloud-init.conf`, which turns password login on, and in `sshd` the first value of
     a keyword wins. Before the reload, `sshd -t` must pass and `sshd -T -C user=root,…` and
     `-C user=deploy,…` must show password login off and key login on; otherwise the drop-in is
     removed. Before the reload, `setup.sh` also arms a timer (`systemd-run --on-active=5min`) that
     removes the drop-in and reloads `sshd` again.
4. **Proves the new policy.** The script opens a fresh key login as root. If it succeeds, it disarms
   the timer; if it fails, the timer restores the old policy within five minutes. It also checks that
   a password login is refused, and that the deploy key gets nothing but the release script's refusal
   for an unknown request.
5. **Pins the host key.** It takes the IP's ed25519 line from the owner's `known_hosts` with
   `ssh-keygen -F` (hashed entries included), already proven in step 1, for the deploy to use.
6. **Prints the GitHub commands** for the owner to run: `gh secret set SITE_SSH_KEY < ~/.ssh/xrpl-file-deploy`,
   `gh secret set SITE_SSH_KNOWN_HOSTS` with the pinned line, `gh variable set SITE_HOST` with the
   IP. The script does not run them.

**How the scripts are proven.** CI runs `shellcheck` over them, and runs the release script against a
temporary directory with `sudo`, `systemctl` and the network checks replaced by stubs on `PATH`: a
candidate that fails its checks is never switched to; a release that fails after the switch is
replaced by the last good one; an interrupted run is repaired by the next; a bad request, a bad
archive and a second concurrent run are refused. The setup itself is proven on the real server: it
is fresh and holds nothing yet, so the first run is the test, and a second run must report no changes.
If the hosting panel offers a console, the runbook names it as the way back in.

### Releases on the server

`/usr/local/bin/xrpl-file-release` holds an exclusive lock (`flock` on `/srv/xrpl-file/.lock`) for
its whole run, so two deploys, or a deploy and a rollback, never interleave. It accepts two requests
through `SSH_ORIGINAL_COMMAND` and refuses anything else.

On every start it first repairs an interrupted run: if `current` is not the release recorded as last
good (`/srv/xrpl-file/last-good`), it points `current` back there and restarts.

**`release <commit>`** (exactly 40 hex characters):

1. Reads the `.tar.gz` from standard input into a staging file, refusing more than 200 MB.
2. Lists the archive before unpacking it: only regular files and directories, relative paths with no
   `..` component, at most 10,000 entries. Anything else refuses the release.
3. Unpacks it with `--no-same-owner --no-same-permissions` into
   `/srv/xrpl-file/releases/<UTC time to the second>-<commit12>` (an existing name refuses), and
   checks that it holds `site/server/main.mjs` and `site/dist/index.html`.
4. **Checks the candidate before visitors see it.** Starts `node site/server/main.mjs` from the new
   directory on `127.0.0.1:8081`, as `deploy`, with a throw-away cache directory, and runs the smoke
   checks below against it. Then stops it. A failed check refuses the release; `current` never moved.
5. Points `current` at the new release (a new link renamed over the old one), runs
   `sudo -n systemctl restart xrpl-file-site`, and runs the smoke checks against `127.0.0.1:8080`
   for up to 30 s. On success it records the release as last good. On failure, or if the script is
   interrupted (`trap` on errors and signals), it points `current` back at the last good release,
   restarts, and exits non-zero. The first release, which has no last good one, stops the service on
   failure instead.
6. Keeps the five newest releases plus the last good one and the one before it, and removes staging
   files and failed unpacks.

**`rollback`** points `current` at the last good release before the current one, restarts, runs the
smoke checks, and records it as last good; if the checks fail it goes back to where it was.

**The smoke checks**, each with its own 5 s timeout: `X-Xrpl-File-Release` names the expected commit;
`/` answers 200; `/mainnet/<a fixed hash>` answers 200 with the file page; `/nope` answers 404;
`/_redirects` answers 404; an `/element/…` file carries `Access-Control-Allow-Origin: *`; a robot's
`/mainnet/<hash>` answers 200 HTML and `/og/mainnet/<hash>` answers 200 with an image type. The last
two do not require the ledger to answer: the generic card and the banner pass.

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
  how a release reaches the server while DNS still points at Pages. Both share the concurrency group
  `deploy-site-server` without cancelling, and the server's lock covers what GitHub does not.
- It builds the site (`web/build.mjs`, `site/build.mjs`), packs the release, and runs
  `ssh deploy@$SITE_HOST "release $GITHUB_SHA" < release.tar.gz` with `SITE_SSH_KEY` and
  `SITE_SSH_KNOWN_HOSTS`. Missing secrets fail the job with their names.
- The `unit` job keeps building the Pages Function bundle whatever the target, and gains
  `shellcheck` over `deploy/server/*.sh` and the release script's stubbed tests.

### Cut-over (owner steps)

Merges into `main` pause from step 2 until step 6, so that the server and Pages serve the same commit.

1. Run `deploy/server/provision.sh root@94.183.189.150` and the `gh` commands it prints.
2. Run `deploy-site-server` by hand on `main`. Check the site through an SSH tunnel to
   `127.0.0.1:8080` (the pages, a file page, a robot card, `/og/`).
3. In Cloudflare DNS, delete the Pages project's `xrpl-file` CNAME record and add an A record
   `xrpl-file → 94.183.189.150` with the proxy off (grey cloud), TTL 60 s. Then remove the custom
   domain from the Pages project. This is the order Cloudflare documents for detaching a custom domain
   (developers.cloudflare.com/pages/configuration/custom-domains, "Delete a custom domain"). The zone
   has no CAA records, so any ACME issuer may issue for the name (checked 2026-11-12).
4. Run `deploy/server/cutover.sh root@94.183.189.150`. It waits until public resolvers (1.1.1.1,
   8.8.8.8) answer the server's IP for the name, then installs the Caddy site for the domain and
   reloads Caddy, and polls `https://<domain>/` until it answers with a valid certificate and the
   expected release header. Because Caddy asks for a certificate only once the name already points at
   the server, failed validations do not pile up against the issuer's rate limits.
5. If the certificate does not arrive within 15 minutes, the script stops and shows Caddy's last
   errors. The owner either fixes the cause or goes back to Pages (below).
6. Set `SITE_TARGET=server`, run `deploy-site-server` once more on the current `main`, and resume
   merges.

Visitors whose resolvers still hold the old answer reach Cloudflare, where the domain is no longer
attached, until that answer expires (the proxied record's TTL is 300 s). Visitors with the new answer
get a TLS error until step 4 finishes. The window is the DNS expiry plus the time to the certificate;
step 5 bounds how long the owner lets it run.

**Back to Pages:** set `SITE_TARGET=cloudflare` and run the Pages deploy, so the Pages project serves
the current site; delete the A record and add `xrpl-file.staticbit.io` again as a custom domain of the
Pages project, which creates the proxied CNAME. Until Pages shows the domain as active, and until
resolvers forget the A record, visitors may get errors, so this is done at a quiet hour; the server
keeps running for a day so that visitors still holding the A record are served. Workers Paid from the
link-cards design is needed again then.

## What it costs

- The server: €2.99 a month (Burstable, 1 CPU, 2 GiB, 1 TB of traffic, then $1 per TB).
- No Workers Paid. The Pages project stays on the free plan; its `*.pages.dev` address keeps serving
  the last Pages deploy, and its function's requests count against the free daily allowance.

## Left to the builder

- Module boundaries inside `site/server/`, the cache's file layout, and names not given above.
- How the Node adapter streams large bodies (pictures are at most 1 MB).
- The fixed hash and element path the smoke checks use.
- The wording of the runbook in `site/README.md` and the deploy note, in English.
