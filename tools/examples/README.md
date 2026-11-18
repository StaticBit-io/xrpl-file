# Publishing the mainnet examples

This guide takes the owner from their own files to the links on the [examples page](../../site/README.md#examples-page).
It covers phase 2 of the work: preparing the files, rehearsing on testnet, publishing on mainnet and
putting the links into the site.

Three rules govern everything below:

- **Claude never runs the mainnet command.** Claude prepares the files, regenerates the plan and
  rehearses on testnet. The mainnet command is run by the owner, with their own accounts, in their own terminal.
- **Seeds are typed only at the tool's hidden prompt.** Never put a seed in an argument, an
  environment variable, a file, a chat message or a log. On mainnet the tool refuses redirected input.
- **Mainnet publication is permanent.** A published file cannot be changed or deleted, and the fees
  are spent. Read the quote before typing the confirmation phrase.

## What is in this folder

| Path | Purpose |
|---|---|
| `prepare.sh` | Turns the owner's raw drawings and video into publishable files and writes `prepared.json`. |
| `test-prepare.sh` | Tests `prepare.sh` against generated inputs. |
| `make-plan.mjs` | Turns `prepared.json` into `plan.json` and copies `about.txt` and `chunks.svg` next to the prepared files. |
| `plan.json` | The publication plan read by `tools/XrplFile.Fixtures`. The committed one lists stand-in files. |
| `make-stand-ins.sh` | Generates stand-in files from ffmpeg test patterns, used before the owner's files exist. |
| `media/about.txt`, `media/chunks.svg` | The text and the SVG written for the page; their bytes on the ledger are the bytes in git. |

Requirements: Bash, `ffmpeg` and `ffprobe`, Node.js, and the .NET SDK. The owner's raw and prepared
files live under `run/examples/`, which git ignores.

## 1. Prepare the owner's files

1. Put the drawings (PNG, JPEG, WebP, GIF, BMP or TIFF) and exactly one H.264/AAC video (MP4, MOV or M4V)
   in one folder, for example `run/examples/owner`. Add `cover.webp` or `cover.jpg` if the owner supplies
   a cover.
2. Run the script. Without a cover file, `--cover-at` names the second of the video to take the cover from:

   ```bash
   tools/examples/prepare.sh --cover-at 4 run/examples/owner run/examples/media
   ```

   A supplied cover wins over `--cover-at`; with neither, the script stops.
3. Read the `PASS` lines. Each promised property is checked: a drawing that is already PNG, JPEG or WebP
   and at most 1,000,000 bytes is copied unchanged, a larger one becomes WebP under 1,000,000 bytes, the
   video gets `+faststart` without re-encoding when needed, and the cover is WebP, at most 65,536 bytes,
   at most 960 px on the long side. Any `FAIL` stops the script.
4. Check `run/examples/media/prepared.json`: it lists every file with type, size and pixel dimensions.
   The dimensions and sizes go into the site later.

## 2. Regenerate the plan

1. Write the plan from `prepared.json`. The script also copies `about.txt` and `chunks.svg` into
   `run/examples/media`:

   ```bash
   node tools/examples/make-plan.mjs run/examples/media/prepared.json > tools/examples/plan.json
   ```

2. Open `plan.json` and check the order: the cover first, then the video that names it, then the
   drawings, `about.txt` and `chunks.svg`. A cover must be published before the video that refers to it.
3. Every entry has writer `a`; the tool refuses any other writer when it runs with your own accounts.
4. Commit `plan.json` so that the plan the owner publishes is the one that was rehearsed.

## 3. Rehearse on testnet

The rehearsal runs the same files and the same plan through the same tool on testnet, where the
accounts are throwaways funded by the faucet and nothing costs anything.

1. Run the plan in the default testnet mode (three positional arguments, no addresses, no prompts):

   ```bash
   dotnet run --project tools/XrplFile.Fixtures -- tools/examples/plan.json run/examples/media run/examples-testnet
   ```

2. Wait for the final line `N of N files have links`. Each file is read back from the ledger and
   verified before its link is written to `run/examples-testnet/links.json`.
3. To exercise the own-accounts path, including the quote and the prompts, run it with two testnet
   accounts (`--network testnet --writer <rAddress> --payer <rAddress>`). On testnet the tool reads the
   seeds from redirected input, so a script can drive it. A seed that belongs to a regular key of the
   writer or the payer is also accepted: the tool confirms it against the account's `RegularKey` before
   anything is priced or submitted. A regular key was published and verified this way on testnet.
4. Open a few of the testnet links in the browser at `/testnet/<HASH>` and check that they show the files.

## 4. Publish on mainnet

Only the owner runs this. Use two different accounts of your own: a writer and a payer. The tool refuses
the same address twice. Each account must hold its base reserve plus the fees it pays; the quote shows
both. Nothing is created or deleted on the accounts, and the run spends only fees.

1. Run the command with your own addresses:

   ```bash
   dotnet run --project tools/XrplFile.Fixtures -- --network mainnet --writer <rAddress> --payer <rAddress> \
     tools/examples/plan.json run/examples/media run/examples
   ```

   The node defaults to `wss://xrplcluster.com`; `--node <wss-url>` overrides it.
2. Type the writer's seed at the hidden prompt, then the payer's. Nothing is echoed. A seed whose address
   differs from the one on the command line is accepted only as that account's regular key, and only if
   the account's `RegularKey` names it.
3. Read the quote. The tool prices the whole plan from the live network before any transaction. It
   prints one line per file (bytes, transactions, fee in XRP), then the totals, and what the writer and the
   payer hold against what each needs. "Required" is each account's base reserve once plus the
   worst-case fees of every file, so the expected fee is lower. The tool stops without submitting if
   either account falls short.
4. To go ahead, type the confirmation phrase exactly:

   ```text
   publish to mainnet
   ```

   Anything else ends the run with nothing submitted.
5. Wait for the final line `N of N files have links`. The links are in `run/examples/links.json`.
   Every file was read back from the ledger and verified before its link was written.

Each file's session is capped at the fee estimate printed for it, with no headroom. If a fee rises after
the owner confirmed, the run stops instead of spending more, and running it again prints a new quote and
asks again.

## 5. Resume after an interruption

A run that stops (Ctrl+C, a lost connection, a rise in fees) is repeated with the same command and the
same arguments. Files already in `links.json` are skipped, and an unfinished file is resumed from its
state under `run/examples/state/<key>`.

A resumed run asks for both seeds and the confirmation phrase again, because it prices what is left and
the owner must see that number. A file being resumed is printed with `resume: at most`: transactions
already submitted are not repeated, so the work left costs no more than the printed figure and may cost
less. That figure is the cap for the resume. If the remainder would cost more than was printed, the
tool stops without submitting.

If the tool reports that a state was started for another writer or another cover, the run was begun
with different arguments. Delete that state folder only when the owner means to start that file again.

## 6. Check the published files

1. For every link in `links.json`, open `https://xrpl-file.staticbit.io/mainnet/<HASH>` in a browser
   (the hash is the part of the link after `xrpl-file://mainnet/`). Check that the file displays and
   that the name, size and type match `prepared.json`.
2. Play the video and confirm that playback starts before the whole file has loaded.
3. Share one `/mainnet/<HASH>` link in Telegram. The card shows the file itself when it is a PNG, JPEG,
   WebP or GIF of at most 1 MB, the cover when the file has one, and the site banner otherwise. The
   video's card should show its cover.

## 7. Put the links into the site

1. Hand `run/examples/links.json` to Claude, or edit `site/src/js/examples.mjs` directly: in `EXAMPLES`,
   replace each testnet `link` with the mainnet one and set `bytes`, `width` and `height` from
   `prepared.json` (a picture or a video needs the dimensions; `svg` and `text` do not).
2. Rename the `stand-in-*` keys. The key is the file's base name as the plan writes it. Rename it in
   `EXAMPLES` and, to the same value, in `EXAMPLE_STRINGS` in `site/src/js/examples-strings.mjs` for all
   six languages (`en`, `ru`, `de`, `es`, `pt`, `zh`). Drop the stand-in wording from captions.
3. Run the checks; the tests fail on a language that lacks a key:

   ```bash
   node web/build.mjs
   node --test site/test/*.test.mjs
   node site/build.mjs
   ```

4. Open `/examples` and the five language pages from `site/dist` and check each card once more against
   step 6.

The cover is not an example of its own: it belongs to the video. How a card is built is described in the
[examples page notes](../../site/README.md#examples-page).
