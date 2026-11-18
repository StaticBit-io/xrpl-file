# Security policy

## Reporting a vulnerability

Report a vulnerability privately through GitHub:
[Security → Report a vulnerability](https://github.com/StaticBit-io/xrpl-file/security/advisories/new).
Do not open a public issue for it.

Include what is affected, how to reproduce it, and what an attacker gains.

## What counts

- A reader (`XrplFile.Core`, `web/xrpl-file.js`, the `<xrpl-file>` element) that accepts bytes the
  manifest's Merkle root does not prove, or that a crafted ledger record can crash or stall.
- A writer that signs or submits a transaction the person did not approve, or spends past the
  approved budget.
- The key vault in `XrplFile.Keys`: anything that reveals a key or a password, or that lets a
  changed vault file pass as the original.
- The site and its server (`site/`, `deploy/server`): code execution, a way past the deploy key's
  restrictions, or content served under the site's name that the ledger does not hold.

## Supported versions

Only the latest release of each package and the current `main` branch get fixes.
