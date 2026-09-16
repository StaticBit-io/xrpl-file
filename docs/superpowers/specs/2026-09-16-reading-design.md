# Reading a link back into a file — design

Part C4 of the desktop application, and the last part of the project. C2 publishes a file and hands
back an `xrpl-file://` link. This is the part that takes such a link — anyone's, not only one this
application produced — and turns it back into the file, on disk, verified.

| Part | What it is | State |
|---|---|---|
| A | tickets and the publication session | shipped |
| B | the key vault | shipped |
| C1 | the shell and the vault screens | shipped |
| C2 | publishing: a file, a key, an estimate, a run | shipped |
| C3 | the throwaway account's life, including reclaiming its reserve | shipped |
| **C4** | **reading a link back into a file — this document** | **in progress** |

`XrplFileReader` in `XrplFile.Core` already reads. It has done since part A, it is covered by the
committed vectors, and the browser reader in `web/` is a second implementation of the same format
checked against the same vectors. **C4 adds nothing to `XrplFile.Core`** — the first part of the
application that does not.

## 1. Scope

**In.** A Read tab. Pasting a link, or pressing "read" beside a row of the publications list C2
keeps. Showing the file's name, size and type from its manifest **before** anything is downloaded.
Reading it block by block with progress and cancellation. Saving it where the person chooses. One
sentence for every failure, written from the reader's side.

Also in, because the tab cannot be reached from the publications list without it:

- moving which tab is showing out of `Shell.razor` and into a model (section 4.4);
- one new method on `IFilePickerService` that takes a stream rather than a byte array
  (section 3.2) — added beside the existing one, which is not touched.

**Out.** Showing the file's contents. Reading with the vault locked. Raising the reader's size
ceiling. Anything added to `XrplFile.Core`.

## 2. Decisions

| # | Decision | Why |
|---|---|---|
| H1 | The manifest is read and shown first; the blocks are read only after the person agrees | The manifest carries the name, the size and the type, and costs one transaction lookup. Downloading twenty megabytes to find out what they were is the wrong order |
| H2 | The application drives the block loop rather than calling `ReadAsync` | `ReadAsync` is one call with no progress and no way out. A file this size over a public node is minutes of a screen saying nothing |
| H3 | Every cryptographic check stays in the library | `BlockListAsync` verifies the list against the manifest's root and `BlockAsync` verifies each block against its hash. The application reimplements no hash and no tree — section 3.1 lists exactly what it does take on, and none of it is cryptography |
| H4 | Blocks are written to a temporary file as they arrive, and nothing reaches the destination until the whole file has verified | Nothing incomplete ever reaches the place the person chose. A failure at the nine-hundredth block of a thousand leaves them with nothing, rather than with something that looks like their file. Note this is not a filesystem rename — section 3.2 explains why it cannot be on this platform, and what it is instead |
| H5 | The temporary file lives in the operating system's temporary directory, not the application's data directory | It is scratch, and the system sweeps it if this application ever fails to. The application's own data directory is swept by nobody |
| H6 | The read outlives the screen but not the window | It is a singleton like every other model here, so switching tabs does not kill a download. Closing the window ends it — and unlike a publication, a read that ends can simply be started again |
| H7 | The reader's ceiling stays at its own default of 20,000,000 bytes, and is now policy rather than architecture | With the file streamed to disk, memory no longer decides the limit. What remains is the node: two hundred megabytes is roughly two hundred thousand requests, which is hours and a plausible way to be cut off by a public node |
| H8 | The reading errors get their own sentences, not C2's | The same `XrplFileError` values mean different things to a reader. `BlockMismatch` to a publisher means the ledger did not return what was just written; to a reader it means the file in the ledger does not match its own manifest. Reusing C2's wording would tell a reader about a publication that never happened |
| H9 | The node comes from the link's own network | An `xrpl-file://` link names its network. Nothing else has to be asked, and nothing else may disagree |
| H10 | Which tab is showing moves into a model | Reaching the Read tab from the publications list is a decision, and today it lives in markup where nothing can reach it — including the button that needs to make it. C1's own E2 says a component holds no decisions |
| H11 | Reading needs no key, and still lives behind the same unlock as everything else | Nothing here signs anything, so the lock is not protecting this tab. But the shell is one window with one gate, and an exception to it would mean two shells with two sets of rules |
| H12 | `IFilePickerService` gains a method rather than changing one | The existing byte-array save has one consumer: writing an encrypted key container out of the vault. That path is reviewed and shipped, and reading a file is not a reason to touch it |

## 3. What the application takes on

### 3.1 The four things that move out of `ReadAsync`

`XrplFileReader.ReadAsync` does six things. Three of them are the library's own checks and stay
there: `BlockListAsync` verifies the block list hashes to the manifest's root, `BlockAsync` verifies
each block against its own hash, and both refuse with typed errors. The other three, plus the
assembly itself, move into the application's loop:

1. **The size ceiling.** `manifest.Size` against the reader's `MaxBytes`, refused as
   `XrplFileError.TooLarge` before a single block is fetched.
2. **The node's history.** The manifest names the ledger its first chunk was written in, and a node
   that has pruned past it cannot answer. `ILedgerSource.EarliestLedgerAsync` is public, so this is
   three lines and needs nothing added to the library: an earliest ledger later than the manifest's
   first is `XrplFileError.NodeLacksHistory`.
3. **Assembly.** Appending each verified block to the temporary file in order.
4. **The final length.** The bytes written against `manifest.Size`. The library's own comment notes
   this is a belt-and-braces check — `BlockAsync` holds every block to the length the manifest
   implies for it, so the total can only come out wrong if the block list did, and that is verified
   before any block is read. It is kept anyway, because it is one comparison standing between a
   truncated file and a file the person is told is theirs.

**One test makes this safe.** A differential test reads the same file from the same fake ledger two
ways — through `XrplFileReader.ReadAsync` and through the application's loop — and asserts the bytes
are identical. If the application's assembly ever diverges from the library's, that fails. Without
it, the four items above are four opportunities for the application to read files slightly
differently from the reference implementation, which is the one thing a file format cannot afford.

### 3.2 The save seam

`IFilePickerService` today hands back bytes and never a path, deliberately: on this platform the
save dialog is WinRT's `FileSavePicker`, which answers with a `StorageFile`, and C1's remarks say a
picker's file is not reliably re-openable as an ordinary path. That rules out the pattern this
repository uses elsewhere — write a temporary file beside the target and rename it — because a
`StorageFile` has no directory to put a sibling in.

So the temporary file lives in the operating system's temporary directory, and when the read has
verified, the application opens it and hands it to the picker as a stream:

```
Task<bool> SaveStreamAsync(string suggestedFileName, Stream content, CancellationToken cancellationToken)
```

Added beside `SaveFileAsync`, which is unchanged (H12). The final copy is local disk work on a file
already on local disk; the network part of the read is over by then.

The suggested name is `Manifest.Name`, which is nullable — a file may have been published without
one. When it is absent the application suggests something derived from the link rather than nothing,
and the person renames it in the dialog if they care.

## 4. The application

### 4.1 `ReadModel` — the link and the manifest

Holds the link text, parses it with `XrplFileLink.TryParse`, resolves the node with
`NodeEndpoint.Resolve` against the link's own network (H9), and fetches the manifest.

What it refuses, each with its own message: a link that does not parse; a network with no node URL
configured and none built in — the same refusal the publish screen makes, for the same reason; a
manifest larger than the ceiling; a node that does not answer.

What it shows once the manifest is in: the name, the size, the type, the writer's address, and the
number of blocks the file is in. Then a button.

### 4.2 `ReadRun` — the loop

A singleton holding at most one read (H6): the cancellation source, the progress, the outcome, and
the path of the temporary file.

It is deliberately a much smaller thing than C2's `PublicationRun`. That type carries state files,
a history, generation pinning against a stale write, and resumption — and it was the most
defect-dense code in the project, with one critical and seventeen important findings across four
review rounds. **None of that machinery is here, and the reason is one sentence: a read costs
nothing and can simply be started again.** An interrupted publication cannot.

What it keeps from `PublicationRun`, because those lessons were paid for:

- a snapshot type, so a screen takes one consistent view rather than reading fields one at a time —
  C2 had to fix that twice, both times after a frame drew a cancel button over a finished result;
- an in-flight refusal, so a second start cannot run beside the first;
- a `StopAsync` that cancels and waits, for the window closing.

The temporary file is deleted on success, on failure, on cancellation and when the window closes.
That last one is the item that gets forgotten; C2 forgot the equivalent and it took a review round.

### 4.3 Errors

`ReadErrorMessages` maps every `XrplFileError` to one sentence, with a test that enumerates the enum
so a value added later fails the suite rather than reaching a screen as a number. The sentences are
written from the reader's side (H8), and the reading half finally becomes reachable for real:

- `InvalidLink` — this is not an xrpl-file link.
- `ManifestNotFound` — the node has no transaction at that hash. Either the link is wrong or it
  belongs to a different network.
- `NodeLacksHistory` — this node's history does not go back far enough to hold the file. Another
  node with deeper history can read it.
- `CorruptManifest` — there is a manifest there, but not one this version understands.
- `ListMismatch`, `BlockMismatch`, `ChunkMissing` — the file in the ledger does not match what its
  own manifest says it should be. Nothing was saved.
- `TooLarge` — the file is larger than this application will read.
- `NodeUnreachable` — the node did not answer.

The publishing errors — `SubmitRefused`, `InsufficientBalance`, `BudgetExceeded`, `FeeMismatch`,
`ValidationTimeout`, `ResumeMismatch`, `SessionAlreadyRun` — cannot occur here, because nothing is
submitted. They still get a sentence, because the test enumerates the enum, and it says plainly that
it is not something reading can cause.

### 4.4 Which tab is showing

`Shell.razor` keeps the current tab in a private field, and the publications list needs to change it
(H10). A small model in `XrplFile.App.Core` holds it instead: which tab is showing, and a method to
show a different one. The shell reads it; the publications list calls it, having first put the link
it wants read on `ReadModel`. Nothing else about the shell changes — the unlock gate, the idle-lock
poll and the lock button stay exactly where C1 put them.

This touches C1's shipped shell. It is a small change and in the direction C1 set for itself: a
component holds no decisions, and "which tab is showing" is one — it is simply the last decision
still living in markup because until now nothing outside the shell had to make it.

## 5. Testing

Everything in `XrplFile.App.Core` is tested on Linux in continuous integration as C1, C2 and C3's
models are, against the fake ledger. The differential test of section 3.1 is the one that matters
most.

**No standalone stand is needed.** Reading submits nothing, so there is no protocol behaviour here
that a fake ledger cannot express — which is exactly why parts A and C3 needed a stand and this part
does not. The library's own read path is already covered against the committed vectors, and by a
second implementation in `web/` checked against the same ones.

The Windows-only screens get what C1's E9 gave the others: a compile job, with every decision they
might make living in a model instead.

## 6. What this part does not do

It does not show the file. It saves it and lets the operating system decide what opens it.

It does not read with the vault locked (H11).

It does not add anything to `XrplFile.Core`. After three parts that each added one or two public
members to a published library, this one needed none — the reader was finished in part A, and what
was missing was never the reading.
