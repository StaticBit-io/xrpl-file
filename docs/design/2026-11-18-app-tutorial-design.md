# Desktop app tutorial — design

Status: draft for the owner's review, 2026-11-18.

## Intent

The desktop application (`app/XrplFile.App`, logic in `app/XrplFile.App.Core`) is about to have its
first release. A newcomer opens a vault and lands on a screen of accounts, keys, networks and
reserves, with no idea which of them they need for the one thing they came to do. The tutorial shows
them, on the real screens, how to do the five things the application is for.

The model is the tutorial in xrpl-vanity (`StaticBit-io/xrpl-vanity-generator`,
`docs/2026-11-05-xrpl-vanity-tutorial-design.md`): a spotlight over the real interface, offered once,
reopened at will. It differs in one deliberate way, explained under "Nothing is done for real".

## Goals

After a topic, a user knows where the controls for it are and what each of them does:

1. **Create an account**: generate a key in the vault, and why it has to be funded before it can
   publish.
2. **Add an existing key**: import a seed, or open a backup file.
3. **Export a key**: write a backup file or show it as a QR code, under its own password.
4. **Publish a file**: choose the file, the account, the payer, take an estimate, run, get the link.
5. **Read a file by its link**: look up an `xrpl-file://` link, read it, save it.

The owner chose these five topics, the spotlight form, five separate topics rather than one chain,
and a "?" button on the rail as the way in.

## Non-goals

- Teaching the XRP Ledger itself beyond the one sentence a step needs (reserves, Batch, tickets).
- Settings, the vault location, device unlock, closing accounts, unfinished runs. They are named where
  a topic passes them, and nothing more.
- A tutorial on the project site or for the `<xrpl-file>` element.
- Video, animation for its own sake, progress scores.

## Anti-goals

The tutorial has failed, even if it works, when:

- It writes a key to the vault, writes a backup file, spends or reserves any XRP, or changes any
  setting other than its own "offered" flag.
- It discards something of the user's: a finished read that was not saved, a link typed in the Read
  box, a run in progress.
- A user cannot get out. No step is a dead end; every step has a way forward or out.
- It nags. It is offered once, by itself, and never again.
- A step's text names a button differently from the button itself.

## Nothing is done for real

In xrpl-vanity the user performs every action in a sandbox. Here four of the five topics end in an
action with consequences: a key in the vault, a file holding a secret, XRP spent. The application has
no faucet, so a publication cannot be practised without the user's own XRP.

So a lit control is **shown, not used**: during a topic the whole page is inert, the lit controls
included, and the bubble's own buttons are the only thing that responds. The one exception is the
Read topic (below), because reading costs nothing and leaves nothing behind.

## How it looks

The screen is dimmed; one or more regions stay lit with an outline; a bubble sits beside them, never
covering them, fully inside the window. The bubble shows the topic name, a title, one or two
sentences, "step n of m", **Next** (or **Finish** on the last step) and **Leave**. Escape leaves at
once: nothing is lost by leaving, so there is nothing to confirm. A step whose target is not on the
screen (no key in the vault, say) has a bubble in the middle of the window and no lit region.

Colours, fonts and radii come from the application's existing theme tokens, in both themes. WCAG 2.2
AA: contrast, keyboard use, focus moved to the bubble on every step, the bubble labelled as a dialog.

## The way in

- **The "?" button.** On the wide layout, a rail item above Lock with a question-mark icon and the
  word for "Learn"; on the phone layout, an entry in the More menu beside Settings. It opens the
  **topic list**: a dialog with the five topics, one line each, and Close.
- **The first-run offer.** The same dialog, with one line of welcome above the list and **Not now**
  instead of Close, shown once: the first time the unlocked shell is drawn while the vault holds no
  key at all. Whatever the answer — a topic, Not now, Escape, the window closed — the offer is not
  shown again. "Not now" says the topics stay under "?". A user whose vault already holds keys is
  never offered it.
- The offered flag lives in its own file beside `ui-language.txt`. A missing file means "not offered
  yet". A file that exists but cannot be read counts as offered, so a broken disk never nags; this is
  the one place the flag departs from the other settings stores, where unreadable means "nothing
  chosen". If writing the flag fails, the offer still closes and is simply offered again next start.

- **When the offer is decided.** Each time the shell enters the unlocked stage, until the flag is
  written. "No key at all" means the vault list holds no entry, damaged entries included: a vault
  with damage is a vault somebody used. A freshly created vault and an existing empty one are treated
  alike, on purpose. The flag is written when the offer is shown, before it is answered, as in
  xrpl-vanity, so a crash or a closed window does not bring it back.

A topic cannot start while it would collide with work in progress; its line in the list is disabled
with the reason under it:

- Publish a file, while a publication is running;
- Read a file, while a read is running.

While an account close runs, the rail, the tab bar and the More button are disabled already, so "?"
is unreachable and needs no rule of its own.

## The topics

Wording is the builder's within the copy rules; the content of each step is fixed here. A button
named in a bubble is inserted from the catalog phrase the button itself uses (`{generate}` and the
like), never retyped.

A topic always has the same steps, whatever the vault holds, so "step n of m" never changes under
the user. A step whose target is not on the screen (no key-only row to fund, Paid by when no key is
selected) keeps its place as a centred bubble that describes the control and says when it appears. A
control drawn disabled (Estimate cost with no file chosen) is lit as it is. The one exception is
topic 3 with no key in the vault, which is a single step.

**1. Create an account** (Accounts tab)
1. The Publishing accounts section: publications go out from throwaway accounts the application
   creates, and they live here.
2. The New account button. **Next** opens the New account screen.
3. Name and network: the name is for telling accounts apart; testnet XRP is free practice, mainnet
   costs real XRP.
4. The Generate button: it makes a key in the vault. The account is not on the ledger yet.
5. Back on the list, the Fund button of a key-only row if there is one, else the centre: send XRP to
   the address from any wallet; the account appears once it holds its reserve.
6. Finish: export the new key so it exists outside this vault (points to topic 3).

**2. Add an existing key** (Accounts tab)
1. The Import key button. **Next** opens the import screen on its seed tab.
2. The seed field and Preview: a seed is the key itself; the preview shows the address before
   anything is stored.
3. The backup tab, its file button and password: a backup file written by this application's export.
4. Finish: imported keys are listed under Your keys and publish like any other.

**3. Export a key** (Accounts tab)
1. If the vault holds no key: centred bubble, "export needs a key", and **Finish** offers topic 1.
   Otherwise the "Not exported" tag if a row carries one, else the first row: a key that was never
   exported exists only in this vault.
2. The row's actions menu, opened by the tutorial, with Export key lit. **Next** opens the export
   screen for that row.
3. The password field and Create backup: the backup has its own password, separate from the master
   password; the file and its password together open the key.
4. Finish: save it as a file, or show it as a QR code, and keep it away from the vault's own disk.

**4. Publish a file** (Publish tab)
1. The file picker.
2. Publish from: the account that writes the file and is named in its link.
3. Paid by: a second account pays for large files through Batch; Nobody publishes with tickets,
   slower, holding reserve while it runs. Shown only when the screen shows it.
4. Estimate cost: nothing is spent until the run is started from the estimate, which shows the cost
   and the time first.
5. The History tab on the rail: every publication and its link stays there; an interrupted run is
   listed under unfinished runs and resumed without paying twice.
6. Finish.

**5. Read a file by its link** (Read tab) — done for real
1. The link box, filled by the tutorial with the site's sample link
   (`xrpl-file://mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5`, the
   project logo, the same as `HERO_LINK` in `site/src/js/config.mjs`). Look up is lit **and usable**;
   the step moves on when the lookup answers. **Next** is there too.
2. The lookup result (name, size, time to read) and Read, usable; the step moves on when the read
   finishes.
3. Save, usable and optional: reading needs no account and costs nothing.
4. Finish.

If the lookup or the read fails (no node, no history), the step says what happened in one line and
**Next** still moves on.

The topic is done for real only when the Read tab is idle and empty: no link in the box, no lookup
result, no finished read. Otherwise the tutorial touches none of it: Look up and Read stay inert, the
box keeps what it holds, and the topic is shown, not done. This also applies to a finished read the
tutorial itself made earlier and the user left unsaved; the tutorial cannot tell it from the user's.

When it is done for real, the link box itself stays inert (only Look up, Read and Save respond), so
the text in it is always the tutorial's. When the topic ends:

- if no read was started, the box is emptied and the lookup result cleared, leaving the tab as it was;
- if a read is still running, it is cancelled, which deletes its temporary copy as any cancel does;
- if a read finished, the result stays on the Read tab, saved or not, like any read the user made.

`ReadModel` gains whatever reset it needs for this; `StartRead` alone does not clear a lookup.

## Behaviour

- **Starting** a topic puts every tab back on its main screen, even the tab already showing: an open
  sub-screen (New account, Import, Export, Funding, Reveal, the Estimate screen) and an open row menu
  are closed, with what they held, exactly as moving to another tab and back closes them. A
  half-typed seed or backup password is lost the same way; it was never stored. Today these resets
  happen only because a tab change disposes the component, and `ShellModel.Show` of the tab already
  showing does nothing, so this needs the model-level navigation below. The Close screen cannot be
  open: "?" is disabled while a close runs, and Close is otherwise left by its own Back. The More
  menu closes.
- **Leaving** (Leave, Finish, Escape) returns to the tab that was showing when the topic started, on
  its main screen. Nothing else is restored, because nothing else was changed.
- **The vault locks** (Lock, the idle timer, the window closing): the topic ends at once, silently.
  The shell already notices every lock in two places, `LockAsync` and its once-a-second
  `NoticeLock` poll, and moves to Accounts there; it ends the topic in the same two places. The
  overlay also draws nothing unless the stage is unlocked, so a lock the tutorial was not told about
  still cannot leave a bubble over the unlock screen.
- **The language changes**: impossible during a topic (Settings is inert); the topic list follows
  the language like every screen.
- **The window resizes** or switches between wide and phone layouts: the bubble is placed again
  against the lit regions as they now are.

## Architecture

Everything that decides lives in `XrplFile.App.Core` and is covered in
`tests/XrplFile.App.Core.Tests`; Razor only draws what the model says, as everywhere in this
application.

- **The script**: topics, their steps, which regions each step lights, which are usable, which tab
  and sub-screen each step needs, when a step moves on by itself, and the step's text ids. A fixed,
  data-like description.
- **The tutorial model** (singleton): offered or not, the topic list with each line's availability,
  the active topic and step, Next / Leave / Finish, availability rules, and the events the screens
  report (lookup answered, read finished, vault locked).
- **Sub-screen navigation moves into App.Core.** Today the Accounts screen's sub-screen, its active
  row and its open row menu are private fields of `Accounts.razor`, the import screen's tab is a
  private field of `AddKey.razor`, and the Estimate screen is a private flag of `Publish.razor`. The
  tutorial must open some of them and reset all of them, so they become model state, the way
  `ShellModel.Tab` left `Shell.razor`, with one "back to the main screen" operation per tab.
- **Bubble placement** is pure geometry in App.Core (lit rectangles, bubble size, viewport → position),
  tested; a small JS module measures regions and reports resizes, as `shell.js` does today.
- **Regions** are marked in markup by name (a `data-` attribute or a thin wrapper component) on both
  the rail and the tab bar, so whichever layout is showing is the one measured.
- **Strings**: a `TutorialText` id class and `Tutorial.*` keys in all six `Text/*.json`, written by
  the `writing-site-copy` rules (no long dash, no internal terms). The existing catalog tests already
  fail when a language lacks a key; a new test checks every step's button placeholders resolve to
  the catalog phrase of that button.

## Constraints

- MAUI Blazor Hybrid on Windows; no new third-party package; no network access added beyond the
  Read topic's own read through the configured mainnet node.
- The native window is about 713 px tall; the bubble must fit there and on the phone layout.
- Six languages from day one: en, ru, de, es, pt, zh.
- Tests run as `dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"`.
- Checked by eye in the MauiLab VM in both layouts and both themes, in at least English and one other
  language.

## Approaches considered

1. **Spotlight, shown not done** (chosen by the owner). Real screens, nothing written or spent; the
   Read topic done for real because it is free.
2. **Spotlight, done for real** with a practice key removed at the end. Rejected: it writes to the
   vault, and the publish topic would still stop at the estimate for want of XRP.
3. **A help section of illustrated cards** without a spotlight. Rejected: screenshots in six
   languages and two themes go stale with every screen change, and the user still has to find the
   control.

## Calls made by the author, not the owner

- One dialog serves as both the first-run offer and the "?" topic list.
- The offer appears only when the vault holds no key; an unreadable flag counts as offered.
- Lit controls are inert, except Look up, Read and Save in the Read topic.
- The Read topic uses the site's mainnet logo link.
- Leaving returns to the tab the user started from.
- Topics are blocked, not queued, while conflicting work runs.
- Starting a topic resets every tab to its main screen, losing half-typed input as a tab change does.
- A topic has a fixed number of steps; a missing target becomes a centred bubble, not a skipped step.
- The offer is written as shown; a vault with damaged entries counts as used.
- The Read topic is done for real only on an idle, empty Read tab; leaving cancels its running read
  and empties a box it filled but never read.
- No step lights the "?" item or the More menu.

## Left to the builder

- The exact shape of the model and script types, and how the overlay is drawn (dimming, outline,
  inert).
- Step wording within the rules above.
- The flag file's name and format.
- Which phrase the "?" item shows as its label.
