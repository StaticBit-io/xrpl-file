# Dark theme: verification in the MauiLab VM (Task 8)

Date: 2026-10-13/14. Branch `claude/dark-theme-39373b`, build deployed from this worktree (`E:\Claude\xrpl-file\gifted-bartik-39373b`, commit `9c256bd` on top). Spec: `docs/superpowers/specs/2026-10-13-dark-theme-design.md`, sections 2, 3, 4, 6, 7.

The app ran only in the MauiLab guest (Hyper-V), non-elevated, in the interactive session, with WebView2 DevTools on `127.0.0.1:9222` inside the guest. The stand node was the project's `xrpl-file-ci` rippled (`ws://172.29.240.1:6006` from the guest). Screenshots are in this folder (67 files, indexed at the end); none is black or empty (smallest 16,374 bytes, `51-unlock-600.png`).

## 1. Verdict

| Area | Verdict |
|---|---|
| Step 3, every row of section 5's matrix at 1280 px | Pass, Damage not covered (see 7) |
| Step 4, 900 px and 600 px, no horizontal overflow, last alert clear of the action bar | Pass |
| Step 5, focus survives the 720 px line | Pass after a fix (`9c256bd`) |
| Step 6, no stock Bootstrap blue in any state | Pass |
| Step 7, Windows text size 150% | Pass with a caveat: WebView2 applies it as devicePixelRatio 1.5, not as font size |
| Step 8, keyboard pass at 1280 and 600 | Pass |
| Step 8, three negative cases for the `9c256bd` focusout fallback | Pass (one minor observation, O1) |
| Step 8, contrast gate (`ui-a11y-gates`) on `app.css`, rendered hover, disabled and banner states | Pass for every non-disabled state; disabled states are below 4.5:1 / 3:1 by design and exempt (O2) |

Fixes made during Task 8: one, `9c256bd` (section 9). Nothing in `app.css` needed changing.

## 2. Method and deviations

1. **CDP inside the guest.** The WebView2 DevTools endpoint binds to the guest's loopback only; `http://mauilab.mshome.net:9222` refuses connections from the host. A portproxy plus firewall rule was refused by the permission classifier, so `vm-cdp.ps1` (scratch, not in the repo) runs the whole CDP conversation inside the guest through PowerShell Direct and returns the output lines and the PNG. Same parameters as the brief's helper, plus `-EvalFile` and `-SettleMs`.
2. **Widths are emulated** with `Emulation.setDeviceMetricsOverride`, one CDP connection per check (the override lasts only while the connection is open). The native window is about 1424 px wide, so "1280" is emulated too.
3. **Windows text size.** `TextScaleFactor=150` does not change `html` font-size (16px) in this WebView2; it changes `devicePixelRatio` to 1.5 and the native window to 950 CSS px. It acts as display scaling of the whole page, not text-only scaling. Reported as "changed" (screenshots taken), not N/A, with this caveat.
4. **Keyboard input is real.** Keystrokes are `System.Windows.Forms.SendKeys` sent by a scheduled task in the guest's interactive session (`ui.ps1` / `xf-keys.ps1`), and the page logged every `keydown` with `isTrusted = true`. Two lab facts:
   - The app is a WinUI 3 host; keyboard input does not reach the WebView2 page until native focus sits on its `Chrome_WidgetWin_0` child window. The key sender therefore activates the app window and calls `SetFocus` on that child (`AttachThreadInput`) before the first key. No mouse is involved.
   - After `Tab` the page continues from where the last element had focus, so the harness first focuses a zero-size `span#kb-start` (injected by the harness, `tabindex=-1`) at the start of `body`, so the first `Tab` lands on the first control. It is removed again at the end.
5. **The mouse cases use the real mouse.** The negative case "click into the content" was done with `mouse_event` in the guest's interactive session (not CDP `Input.dispatchMouseEvent`); hover measurements use real pointer moves (`mouse_event` absolute move) and `getComputedStyle` read after the pointer settled, with `matches(':hover')` confirmed true for each.
6. **The vault auto-locks after 15 minutes of idle** (`MauiProgram.IdleTimeout`; keystrokes do not count as activity). It locked once during the keyboard pass and was unlocked again from the keyboard (`labtestvault1`). That is why one planned 600 px run was repeated (not a defect).
7. **Gate harness.** The `ui-a11y-gates` scripts take local files only, so a harness was built from the real compiled stylesheet (`wwwroot/app.css` plus `bootstrap.min.css`, same relative `fonts/`): nine screens whose markup is the live DOM (`#app.innerHTML`) captured from the VM (Keys, Publish, Read, History, Accounts, Settings, Add key x3), plus `states.html`, a matrix of every button variant (`btn` and `btn-sm`) in default and disabled, every banner type (and the nested success/danger banner), form fields, switch, badge, code. `states.html` is composed by hand from the real class combinations in the components; it is the only non-captured page. The same measurements were then repeated inside the VM's own WebView2 (section 6.3).

## 3. Steps 3-8, one row per check

Screenshot names are file names in this folder.

### Step 3: states at 1280 px

| # | State | Verdict | Screenshots |
|---|---|---|---|
| 3.1 | Keys | pass | `01-keys-1280.png` |
| 3.2 | Add key, Generate / Import a seed / Open a backup file | pass | `02-add-key-1-1280.png`, `03-add-key-2-1280.png`, `04-add-key-3-1280.png` |
| 3.3 | Reveal seed (prompt, revealed) | pass | `05-reveal-seed-prompt-1280.png`, `06-reveal-seed-1280.png` |
| 3.4 | Export key (form, done, QR) | pass | `07-export-key-1280.png`, `08-export-key-done-1280.png`, `08b-export-key-qr-1280.png` |
| 3.5 | Accounts | pass (see O3, stale "Closed" record) | `09-accounts-1280.png` |
| 3.6 | New account (form, done) | pass | `10-new-account-1280.png`, `11-new-account-done-1280.png` |
| 3.7 | Funding | pass | `12-funding-1280.png` |
| 3.8 | Close account, preflight table | pass (D1, missing space before "(") | `13-close-account-preflight-1280.png`, `13a-close-account-1280.png` |
| 3.9 | Publish picker (with Unfinished runs) | pass | `14-publish-picker-1280.png` |
| 3.10 | Publish, key selected | pass | `15-publish-file-key-selected-1280.png` |
| 3.11 | Estimate | pass | `16-estimate-1280.png` |
| 3.12 | Estimate with its message (unfunded key) | pass | `17-estimate-message-1280.png` |
| 3.13 | Run running | pass | `18-run-running-1280.png` |
| 3.14 | Run finished (real 3 MB publication, link shown) | pass (D4, disabled Cancel left beside Dismiss) | `19-run-finished-1280.png` |
| 3.15 | Run cancelled (extra) | pass | `20-run-cancelled-1280.png` |
| 3.16 | Unfinished runs | pass | `21-unfinished-runs-1280.png` |
| 3.17 | Read: link entry, manifest looked up, reading, finished | pass | `22-read-entry-1280.png`, `23-read-manifest-1280.png`, `24-read-reading-1280.png`, `25-read-finished-1280.png` |
| 3.18 | History | pass (D3, link wraps after "xrpl-") | `26-history-1280.png` |
| 3.19 | Settings (top, scrolled) | pass | `27-settings-1280.png`, `27b-settings-scrolled-1280.png` |
| 3.20 | Change password | pass | `28-change-password-1280.png` |
| 3.21 | Unlock, and after a wrong password | pass (D2, stale notice) | `29-unlock-1280.png`, `30-unlock-wrong-password-1280.png` |
| 3.22 | Create vault | pass | `31-create-vault-1280.png` |
| 3.23 | Settings, a different vault ("Saved. Restart...") | pass | `32-settings-different-vault-1280.png` |
| 3.24 | Vault unreachable | pass | `33-vault-unreachable-1280.png` |
| 3.25 | Damage | N/A, not covered (section 7) | none |

Judgement for Step 3 is by eye on the screenshots (unreadable text, stock Bootstrap colours, layout breakage): 01-04, 06, 08, 09, 12, 13, 15-19, 23, 26, 27, 27b, 29-31 and 33 were inspected by eye in phase 8a; the remaining screens were captured and checked through their text dumps and the state outputs, not individually inspected. The colour pairs of every screen are covered separately by the gate (section 6).

### Step 4: 900 px and 600 px (Review Focus 1 and 3)

`xf.over()` output is `[page fits, #xf-content fits, scrollWidth, clientWidth, [content scrollWidth, clientWidth]]`; `alertGeom` scrolls `#xf-content` to the bottom and measures the last `.alert` against `.xf-actionbar`.

| # | State | Width | Output (verbatim) | Verdict | Screenshot |
|---|---|---|---|---|---|
| 4.1 | Keys | 900 | `[true,true,900,900,[828,828]]` | pass | `34-keys-900.png` |
| 4.2 | Keys | 600 | `[true,true,600,600,[600,600]]` | pass | `35-keys-600.png` |
| 4.3 | Keys, More panel open (`aria-expanded` true) | 600 | `[true,true,600,600,[600,600]]` | pass | `36-keys-600-more.png` |
| 4.4 | History (real link in the first row) | 900 | `[true,true,900,900,[828,828]]` | pass | `37-history-900.png` |
| 4.5 | History | 600 | `[true,true,600,600,[585,585]]` | pass | `38-history-600.png` |
| 4.6 | Estimate (funded; no alert exists) | 900 | `[true,true,900,900,[828,828]]` | pass | `39-estimate-900.png` |
| 4.7 | Estimate (funded) | 600 | `[true,true,600,600,[600,600]]`, alertGeom `alert=false bar=true` | pass, nothing to measure | `40-estimate-600.png` |
| 4.8 | Estimate with message (unfunded key) | 600 | `[true,true,600,600,[600,600]]` `{"alertBottom":561,"alertTop":499,"barTop":577,"barBottom":642,"gap":16}` | pass | `41-estimate-message-600.png` |
| 4.9 | Estimate with message | 900 | `[true,true,900,900,[828,828]]` `{"alertBottom":488,"barTop":504,"gap":16}` | pass | `42-estimate-message-900.png` |
| 4.10 | Estimate with message, 600x520 (content really scrolls) | 600 | `[true,true,600,600,[585,585]]` `{"alertBottom":370,"alertTop":308,"barTop":386,"barBottom":451,"gap":16}` | pass | `43-estimate-message-600x520.png` |
| 4.11 | Run finished (real 3 MB run, 3,182 tx, about 4.5 min) | 900 | `[true,true,900,900,[828,828]]` `{"alertBottom":301.5,"alertTop":165,"barTop":317.5,"barBottom":361.5,"gap":16}` | pass | `44-run-finished-900.png` |
| 4.12 | Run finished | 600 | `[true,true,600,600,[600,600]]` `{"alertBottom":392.5,"alertTop":217,"barTop":408.5,"barBottom":473.5,"gap":16}` | pass | `45-run-finished-600.png` |
| 4.13 | Run cancelled (extra) | 600 / 900 | `[true,true,600,600,[600,600]]` gap 16 (alert 217..258, bar 274) / `[true,true,900,900,[828,828]]` gap 16 | pass | `46-run-cancelled-600.png`, `47-run-cancelled-900.png` |
| 4.14 | Read finished | 900 | `[true,true,900,900,[828,828]]` `{"alertBottom":151,"alertTop":58,"barTop":167,"gap":16}` | pass | `48-read-finished-900.png` |
| 4.15 | Read finished | 600 | `[true,true,600,600,[600,600]]` `{"alertBottom":203,"alertTop":110,"barTop":219,"barBottom":284,"gap":16}` | pass | `49-read-finished-600.png` |
| 4.16 | Unlock (after Lock) | 900 / 600 | `[true,true,900,900,null]` / `[true,true,600,600,null]` (no `#xf-content` on this screen) | pass | `50-unlock-900.png`, `51-unlock-600.png` |

Notes: (1) the "Run finished with its notes" case (`HistoryNote`, `TicketNote`, `StateNote` together) was not reachable (section 7); the single alert clears the bar by 16 px everywhere. (2) At 600 the Keys table scrolls horizontally inside its card (Reveal/Export/Delete are cut off until scrolled, and focus on them scrolls the card, visible in `60-kbd-600-tab16-more.png`); spec section 3 allows exactly this ("Only tables scroll horizontally, inside their own card"); page and `#xf-content` never overflow.

### Step 5: focus survives the 720 px line (Review Focus 2)

Command (the brief's, verbatim): `vm-cdp.ps1 -Width 1280 -Eval "document.querySelector('.xf-rail-item[aria-current=page]').focus(); document.activeElement.className" -Width2 600 -Eval2 "new Promise(r => setTimeout(() => r(document.activeElement.id), 300))"`

| # | Run | Output | Verdict |
|---|---|---|---|
| 5.1 | Before the fix, first official run | `"xf-rail-item"` then `""` | FAIL |
| 5.2 | Before the fix, a 6-run series | second line: `""`, `"xf-content"`, `""`, `"xf-content"`, `"xf-content"`, `""` | FAIL (race) |
| 5.3 | After `9c256bd` and redeploy, 10 runs | `"xf-rail-item"` / `"xf-content"` in 10 of 10 | pass |
| 5.4 | After the fix, reverse direction (`.xf-tab[aria-current=page]` at 600, crossing to 1280), 4 runs | `"xf-tab"` / `"xf-content"` in 4 of 4 | pass |

Cause: `blur` and `focusout` (relatedTarget null) fire before the media-query `change` event, so at `change` `document.activeElement` is `<body>` and the guard in `shell.js` skipped the hand-off. See section 9.

### Step 6: no stock Bootstrap blue (Review Focus 4)

The brief's expression plus a counter; width 1280; every matching element focused in turn.

| # | State | Output (verbatim) | Verdict |
|---|---|---|---|
| 6.1 | Publish, a key selected | `[] scanned=29` | pass |
| 6.2 | Settings, device switch off | `[] scanned=26` | pass |
| 6.3 | Settings, device switch on (checked=true, then switched back) | `[] scanned=26` | pass |
| 6.4 | Add key, Generate | `[] scanned=21` | pass |
| 6.5 | Add key, Import a seed | `[] scanned=22` | pass |
| 6.6 | Add key, Open a backup file | `[] scanned=20` | pass |
| 6.7 | Estimate | `[] scanned=16` | pass |

`:active` cannot be computed this way; covered by `--bs-btn-focus-box-shadow: none` and the per-variant `--bs-btn-active-*` values.

### Step 7: Windows text size 150% (Review Focus 5)

| # | Check | Output | Verdict | Screenshot |
|---|---|---|---|---|
| 7.1 | Before (`TextScaleFactor` not set) | `["16px",1]`, native `innerWidth` 1424 | baseline | none |
| 7.2 | After setting 150 (first attempt, hard `Restart-VM -Force`) | registry write lost | retried | none |
| 7.3 | After setting 150 and the guest's own `Restart-Computer -Force` | `["16px",1.5]`, native `innerWidth` 950, `matchMedia('(min-resolution: 1.5dppx)')` true | changed (as devicePixelRatio, section 2 item 3) | none |
| 7.4 | Keys at 1280 CSS px (1920 px bitmap): rail 72 px, all seven labels unclipped (each `.xf-rail-item` scrollWidth <= clientWidth, 60 px), `[true,true,1280,1280,[1208,1208]]` | | pass | `53-text150-keys-1280.png` |
| 7.5 | Publish at 1280 CSS px | | pass | `54-text150-publish-1280.png` |
| 7.6 | Publish at 600: tab bar y 731..800 of 800, five tabs 116 px wide at x 6..594, none clipped, `[true,true,600,600,[585,585]]` | | pass | `55-text150-publish-600.png` |
| 7.7 | Keys at 600: `[true,true,600,600,[600,600]]` | | pass | `56-text150-keys-600.png` |
| 7.8 | Restored to 100, guest restarted | `[1424,1,"16px"]` | restored | none |

### Step 8: keyboard pass

All keys are real (section 2 item 4): every logged `keydown` had `isTrusted=true`. Each focus is read in the `focusin` handler: `getComputedStyle(element).outlineColor / outlineWidth / outlineStyle / outlineOffset` and `matches(':focus-visible')`.

| # | Check | Result | Verdict | Screenshot |
|---|---|---|---|---|
| 8.1 | 1280, Tab through the shell from the top | Order: Publish, Read, History, Keys, Accounts, Settings, Lock the vault (the seven rail items), then the content (Add key, Reveal). Every one: `rgb(100, 207, 149) 2px solid`, offset `2px`, `:focus-visible` true | pass | `57-kbd-1280-tab9.png`, `58-kbd-1280-rail-history.png`, `59-kbd-1280-rail-lock.png` |
| 8.2 | 600, Tab through the shell | Order: Lock the vault (header bar), Add key, then Reveal/Export/Delete of the three rows, then the tab bar Publish, Read, History, Keys, More. Every one: `rgb(100, 207, 149) 2px solid`, offset `2px`, `:focus-visible` true (the rail is `display:none` at 600 and never focused) | pass | `60-kbd-600-tab16-more.png` |
| 8.3 | 600, More opens with Enter | Focus lands on Accounts (`xf-more-item`), `aria-expanded=true`, panel present | pass | `65-kbd-600-more-open.png` |
| 8.4 | 600, Escape closes More | Panel gone, `aria-expanded=false`, final focus on More (`xf-tab`), 3 of 3 repeats. See O1 for a transient hop | pass | none |
| 8.5 | 600, choosing Settings from More (Enter, Tab, Enter) | Focus on `#xf-content` (text starts "Settings VAULT LOCATION ..."), panel closed | pass | `61-kbd-600-settings-from-more.png` |
| 8.6 | Negative A: Lock pressed from the keyboard (Tab x7, Enter) | Unlock screen shows, `#xf-content` absent, final `document.activeElement` is `BODY`; no jump into a missing content region, no error | pass | `64-neg-lock-from-keyboard.png` |
| 8.7 | Negative B1: a real mouse click into the content while the "Read" rail item is focused | `focusout` relatedTarget is `xf-content` (the browser itself moved focus to the click target, the content region has `tabindex=-1`); the fallback returns at once on a non-null relatedTarget. Final focus `xf-content`, `:focus-visible` false, no ring | pass | `62-neg-click-content.png` |
| 8.8 | Negative B2: a real mouse click on empty rail space while "Read" is focused (relatedTarget null) | `OUT xf-rail-item rel=null`, final `document.activeElement` `BODY`; the fallback did NOT pull focus to `#xf-content` because the lost control still has client rects | pass | `63-neg-click-rail-empty.png` |
| 8.9 | Negative C: Escape in the More panel | Final focus on More in 3 of 3 runs (same as 8.4) | pass | none |
| 8.10 | Unlock again afterwards (`labtestvault1`) | Vault unlocked, rail shown | done | none |
| 8.11 | Contrast gate | section 6 | pass (disabled exempt) | none |

#### Step 8 outputs (verbatim)

Tab pass at 1280 (event log, consecutive duplicates removed; `kb-start` is the harness's own start element):

```
["OUT btn btn-outline-secondary rel=kb-start","IN kb-start [] rgb(249, 249, 249) 3px none off=2px more=undefined fv=true vis=false","OUT kb-start rel=null","IN kb-start [] rgb(249, 249, 249) 3px none off=2px more=undefined fv=true vis=false","KEY Tab trusted=true","OUT kb-start rel=BUTTON","IN xf-rail-item [Publish] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","OUT xf-rail-item rel=BUTTON","IN xf-rail-item [Read] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","OUT xf-rail-item rel=BUTTON","IN xf-rail-item [History] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","OUT xf-rail-item rel=BUTTON","IN xf-rail-item [Keys] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","OUT xf-rail-item rel=BUTTON","IN xf-rail-item [Accounts] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","OUT xf-rail-item rel=BUTTON","IN xf-rail-item [Settings] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","OUT xf-rail-item rel=BUTTON","IN xf-rail-item [Lock the vault] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","OUT xf-rail-item rel=BUTTON","IN btn [Add key] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","OUT btn btn-primary rel=BUTTON","IN btn [Reveal] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true"]
```

Tab pass at 600 (16 Tabs, same format; rows 2 and 3 repeat the Reveal/Export/Delete triple with the identical ring and are abbreviated here):

```
["IN kb-start [] rgb(249, 249, 249) 3px none off=2px fv=true vis=false","KEY Tab trusted=true","IN xf-icon-button [Lock the vault] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","IN btn [Add key] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","IN btn [Reveal] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","IN btn [Export] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","IN btn [Delete] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true", (rows 2 and 3: Reveal, Export, Delete, same ring), "KEY Tab trusted=true","IN xf-tab [Publish] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","IN xf-tab [Read] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","IN xf-tab [History] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","IN xf-tab [Keys] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true","KEY Tab trusted=true","IN xf-tab [More] rgb(100, 207, 149) 2px solid off=2px fv=true vis=true"]
```

More: 16 Tabs, then Enter, Escape, Enter, Tab, Enter (`MINT` = `rgb(100, 207, 149) 2px solid off=2px`; `more=` is aria-expanded/panel state; the harness's earlier listeners could not be removed, so each event appeared several times in the raw log and is collapsed here):

```
KEY Enter      -> IN xf-more-item [Accounts] MINT more=true/panel fv=true
KEY Escape     -> OUT xf-more-item rel=null
               -> IN xf-content [Keys Add key LABEL KIND NETWOR] rgb(249, 249, 249) 3px none off=2px more=false/nopanel fv=true
               -> OUT xf-content rel=BUTTON
               -> IN xf-tab [More] MINT more=false/nopanel fv=true
KEY Enter      -> IN xf-more-item [Accounts] MINT more=true/panel fv=true
KEY Tab        -> IN xf-more-item [Settings] MINT more=true/panel fv=true
KEY Enter      -> OUT xf-more-item rel=null
               -> IN xf-content [Settings VAULT LOCATION The va] rgb(249, 249, 249) 3px none off=2px more=false/nopanel fv=true
```

Repeat run (Enter/Escape x3, then a last Enter): the same sequence three times, `FINAL=xf-more-item/Accounts` (panel open, `65-kbd-600-more-open.png`).

Negative A (Lock from the keyboard), tail of the log and final state:

```
"KEY Tab trusted=true","OUT xf-rail-item rel=BUTTON","IN xf-rail-item [Lock the vault] MINT fv=true vis=true","KEY Enter trusted=true","OUT xf-rail-item rel=null"] ||FINAL active=BODY content=false text=Unlock your vault Vault unlocked The password worked, and ev
```

Negative B1 (click into the content, Read focused):

```
"...IN xf-rail-item [Read] rgb(100, 207, 149) 2px solid off=2px ...","OUT xf-rail-item rel=xf-content","IN xf-content [Settings VAULT LOCATION The va] rgb(249, 249, 249) 3px none off=0px more=false/nopanel fv=false vis=true"] ||FINAL active=xf-content innerWidth=1424
guest: click at 760,734 rect=60,84,1424,713
```

Negative B2 (click on empty rail space, Read focused):

```
"...IN xf-rail-item [Read] MINT more=false/nopanel fv=true vis=true","OUT xf-rail-item rel=null"] ||FINAL active=BODY
guest: click at 96,604 rect=60,84,1424,713
```

## 4. Notes on the real-input harness (reproducibility)

`ui.ps1 -Keys '{TAB}|{TAB}|click:X:Y|move:X:Y|wait500' -StepMs 450 -Activate` registers a scheduled task (`XfKeys`, interactive, non-elevated) that activates the app, focuses its WebView2 child window, then sends each `|`-separated chunk (SendKeys syntax, or `click:` / `move:` in WebView2 CSS pixels, or `waitN`) with a pause between chunks. A CDP session in the guest installs `focusin` / `focusout` / `keydown` listeners, waits while the keys are sent, and returns the log; the emulated width is held by that same connection.

## 5. Spec section 6 and 7 cross-check

- Focus ring: `2px solid rgb(100, 207, 149)` with `2px` offset on all distinct controls tabbed at 1280 and at 600, and on every control scanned in Step 6 (no stock blue). Not clipped in the screenshots (`57`, `60`, `65`).
- Navigation: rail and tab bar carry `aria-label="Main"`, the current entry `aria-current="page"`; More has `aria-expanded` and `aria-controls`, Escape returns to More, a resize closes it (Step 5 plus 8.3-8.5).
- Status never relies on colour alone: every banner in the screenshots carries words (spot check).

## 6. Contrast gate (`ui-a11y-gates`)

### 6.1 Skill scripts on the harness (real `app.css` + Bootstrap, section 2 item 7)

| Script | Result |
|---|---|
| `measure_render.mjs` on `states.html` and the 9 captured screens | OK on all 10: every rendered text element meets AA (text elements counted: states 60, accounts 32, add key generate 15 / import 16 / open 14, history 43, keys 37, publish 54, read 10, settings 31). This script ignores `opacity`, so disabled controls are not judged by it (see 6.2) |
| `axe_audit.mjs --dark` on the same 10 | No `color-contrast` violation on any page. 4 real pages have a `label` finding (CRITICAL): Add key Generate 2 inputs, Import a seed 3, Open a backup file 1, Read 1. `states.html` has 2 `label` + 1 `select-name` findings: harness fields written without labels, not an app finding. Accounts, History, Keys, Publish, Settings: 0 violations |
| `verify_target_size.mjs` on the same 10 | OK on all 10: every target >= 24x24 or exception-covered (the project's own 44 px minimum is set in `app.css`) |
| `verify_responsive.mjs` on the 9 screens | OK, no horizontal overflow at 280/320/414 px (a check of the harness pages; the shell's own overflow is Step 4) |

The `label` finding is pre-existing markup, not the theme: `AddKey.razor` and `Read.razor` pair a `<label class="form-label">` with an `<input>` without `for`/`id` (the same pair exists on `main` in `Read.razor`). Out of scope (O4).

### 6.2 Rendered pairs by state (headless Chrome 1280 px on the harness, same maths as `measure_render.mjs`, plus opacity composited)

Text pairs: ratio of the control's text against its own fill (a transparent fill is composited over the parent's backdrop). Border pairs: ratio of the border against the surface behind the control (WCAG 1.4.11, 3:1, applied to outline buttons and inputs where the border identifies them; for solid buttons the fill is the identifier and its ratio is shown). The numbers are the same in the VM (6.3).

| Variant | default text | hover text | focus text | disabled text (45% opacity) | default border | hover border | disabled border |
|---|---|---|---|---|---|---|---|
| `.btn-primary` | 10.09 (#0D0D0D on #64CF95) | 11.16 (#0D0D0D on #78D7A4) | 11.16 | 3.00 (#141414 on #3B6B51) | 9.03 | 9.99 | 2.84 |
| `.btn-success` | 10.09 (#0D0D0D on #64CF95) | 11.16 (#0D0D0D on #78D7A4) | 11.16 | 3.00 (#141414 on #3B6B51) | 9.03 | 9.99 | 2.84 |
| `.btn-outline-primary` | 9.03 (#64CF95 on #1A1A1A) | 7.49 (#64CF95 on #212C26) | 7.49 | 2.84 (#3B6B51 on #1A1A1A) | 9.03 | 9.03 | 2.84 |
| `.btn-outline-secondary` | 15.19 (#F9F9F9 on #202222) | 13.34 (#F9F9F9 on #2A2C2C) | 13.34 | 4.15 (#7E7E7E on #1D1E1E) | 3.27 | 7.32 | 1.64 |
| `.btn-danger` | 6.21 (#0D0D0D on #D6776A) | 7.51 (#0D0D0D on #DE8B80) | 7.51 | 2.25 (#141414 on #6F443E) | 5.56 | 6.72 | 2.12 |
| `.btn-outline-danger` | 5.56 (#D6776A on #1A1A1A) | 4.72 (#D6776A on #312524) | 4.72 | 2.12 (#6F443E on #1A1A1A) | 5.56 | 5.56 | 2.12 |
| `.btn-outline-warning` | 13.08 (#D3E7AB on #1A1A1A) | 10.26 (#D3E7AB on #2D2F29) | 10.26 | 3.65 (#6D765B on #1A1A1A) | 13.08 | 13.08 | 3.65 |
| `.btn-link` | 7.32 (#A8A8A8 on #1A1A1A) | 9.03 (#64CF95 on #1A1A1A) | 7.32 | 2.52 (#5A5A5A on #1A1A1A) | n/a | n/a | n/a |

`btn-sm` gives the same numbers for every variant and state.

| Banner / field / navigation | Pair | Ratio | Need | Verdict |
|---|---|---|---|---|
| `.alert-warning` (also its `strong` and `a.alert-link`) | #D3E7AB on #30332B | 9.69 | 4.5 | pass |
| `.alert-danger` (also `strong`, `a`) | #D6776A on #342725 | 4.58 | 4.5 | pass (thin margin, O5) |
| `.alert-success` (also `strong`, `a`) | #64CF95 on #233029 | 7.17 | 4.5 | pass |
| `.alert-info` (also `strong`, `a`) | #F9F9F9 on #2D2D2D | 13.08 | 4.5 | pass |
| `.alert-secondary` (also `strong`, `a`) | #F9F9F9 on #2D2D2D | 13.08 | 4.5 | pass |
| `.alert-danger` nested in `.alert-success` | #D6776A on #0D0D0D | 6.21 | 4.5 | pass (this is why `.alert .alert` sits on the page colour) |
| `.form-control` value | #F9F9F9 on #2D2D2D | 13.08 | 4.5 | pass |
| `.form-control` placeholder | #A8A8A8 on #2D2D2D | 5.79 | 4.5 | pass |
| `.form-control` / `.form-select` border on a card | #6B6B6B on #1A1A1A | 3.27 | 3 | pass |
| `.form-control` focus border | #64CF95 on #1A1A1A | 9.03 | 3 | pass |
| `.form-label` | #A8A8A8 on #1A1A1A | 7.32 | 4.5 | pass |
| `.form-check-label` | #F9F9F9 on #1A1A1A | 16.53 | 4.5 | pass |
| `.text-danger` on a card | #D6776A on #1A1A1A | 5.56 | 4.5 | pass |
| `.xf-muted` on a card | #A8A8A8 on #1A1A1A | 7.32 | 4.5 | pass |
| `.xf-figure` on a card | #EBAF8E on #1A1A1A | 9.16 | 4.5 | pass |
| `.badge.text-bg-danger` | #0D0D0D on #D6776A | 6.21 | 4.5 | pass |
| `code` on a card | #F9F9F9 on #1A1A1A | 16.53 | 4.5 | pass |
| Rail item default / hover / focus | #A8A8A8 on #0D0D0D / #F9F9F9 on #2D2D2D / #A8A8A8 on #0D0D0D | 8.17 / 13.08 / 8.17 | 4.5 | pass |
| Rail item current (default, hover, focus) | #64CF95 on #16201B | 8.65 | 4.5 | pass |
| Tab bar item default / hover / focus | #A8A8A8 on #111111 / #F9F9F9 on #2D2D2D | 7.94 / 13.08 / 7.94 | 4.5 | pass |
| Tab bar item current | #64CF95 on #19241E | 8.30 | 4.5 | pass |
| `.nav-tabs .nav-link` active | #64CF95 on #0D0D0D | 10.09 | 4.5 | pass |
| `.nav-tabs .nav-link` inactive / hover / focus | #A8A8A8 / #F9F9F9 / #F9F9F9 on #0D0D0D | 8.17 / 18.46 / 18.46 | 4.5 | pass |
| `.form-control:disabled` (45%) | #7E7E7E on #232323; border #3E3E3E | 3.91; 1.64 | 4.5; 3 | exempt, O2 |

The disabled rows are below 4.5:1 (text) and 3:1 (border) for 7 of 8 variants. Spec section 2 states disabled = 45% opacity (as xrpl-vanity), and WCAG 1.4.3 and 1.4.11 exempt inactive user interface components, so this is not a conformance failure and nothing was changed; it is listed as O2 because the lowest values (`btn-danger` 2.25, `btn-outline-danger` 2.12, `btn-link` 2.52, `btn-outline-primary` 2.84) are visibly faint.

### 6.3 The same states measured in the VM's own WebView2 (not headless Chrome)

Method: the probe (same composite maths) runs inside the app's page in the guest, on the real element's `getComputedStyle`. Hover uses real pointer moves.

Hover on real screens, 1424 px native width, `matches(':hover')` true for each:

| Element | Pair on hover | Ratio | Border ratio |
|---|---|---|---|
| rail Publish / Read / History / Accounts / Settings / Lock the vault | #F9F9F9 on #2D2D2D | 13.08 | n/a |
| rail Keys (current) | #64CF95 on #16201B | 8.65 | n/a |
| `.btn-primary` "Add key" | #0D0D0D on #78D7A4 | 11.16 | 11.16 |
| `.btn-outline-secondary` "Reveal" / "Export" / "Copy" / "Resume" / "Show in file manager" / "Choose a vault file" | #F9F9F9 on #2A2C2C | 13.34 | 7.32 |
| `.btn-outline-danger` "Delete" / "Forget" | #D6776A on #312524 | 4.72 | 5.56 |
| `.btn-outline-danger.btn-sm` "Clear list" (on the page colour) | #D6776A on #251A18 | 5.42 | 6.21 |
| `.btn-outline-primary` "Choose a file" | #64CF95 on #212C26 | 7.49 | 9.03 |
| `.btn.btn-sm.btn-outline-primary` "Read" (History) | #64CF95 on #212C26 | 7.49 | 9.03 |
| `.list-group-item-action` (key picker rows) | #F9F9F9 on #2D2D2D | 13.08 | n/a |

Disabled in the VM: Publish "Estimate" (`btn-primary` disabled): #0D0D0D on #34644A, 2.85 (opacity 0.45), exempt (O2).

Banners in the VM: the one natural banner on a reachable screen, `alert-warning` "Choose a file to publish...", #D3E7AB on #252720 = 11.35 (on the page colour). Each of the five types, plus a danger banner nested in a success one and `strong` / `a.alert-link` inside each, was injected into the real WebView DOM on the Settings screen: warning 9.69, danger 4.58, success 7.17, info 13.08, secondary 13.08, nested danger 6.21 on #0D0D0D, nested success 7.17 (identical to 6.2). Earlier screenshots (`17`, `19`, `20`, `25`, `32`, `41`-`49`) show the natural success / warning / secondary / info banners rendered in the VM; they were not re-measured individually.

Tightest text pair on each real VM screen (every visible text element swept, disabled excluded; ratio / need 4.5): Publish 5.56 (Delete), History 5.56 (Forget), Accounts 5.56 (Close), Keys and Add key screens 7.32 (form-label), Settings 7.32 (section title), Read 7.32 (label). No text element in the VM is below 5.56:1 outside disabled controls.

## 7. Not covered

| State | Why |
|---|---|
| Damage (the warning banner at the top of Keys) | Needs a corrupted vault file to trigger; not reproduced. The `alert-warning` and `btn-outline-warning` it uses were measured on the harness (6.2), and `alert-warning` in the VM |
| Run finished with its notes (`HistoryNote`, `TicketNote`, `StateNote`) | None of the notes appeared in any reachable run (finished and cancelled runs each showed exactly one alert); the 16 px gap to the action bar was measured on the single-alert case |
| Read: the failed-save banner nested in the success banner | Not reproduced in the VM; the equivalent nesting was injected and measured (6.3) |
| `btn-success` (Add key, after generating), `btn-danger` (confirm delete/close), `btn-outline-warning` (Damage), `btn-link` on a real screen | Not reached in the VM with the stand; measured on the harness with the same stylesheet and engine (6.2) |
| Resuming an unfinished run | Deliberately not done: a known Core defect (TicketStrategy no-mint sweep -> `invalidLgrRange`) makes a resumed ticketed run pay for a manifest and then fail; the Unfinished list is only screenshotted |
| "Close permanently" of the throwaway account | Not pressed; the account (label "throwaway") still holds 1,000 XRP |
| `:active` styles | Cannot be computed; covered by the per-variant `--bs-btn-active-*` values |
| Android/iOS, light theme | Out of scope (spec section 8) |

## 8. Observations and defects (nothing fixed unless section 9 says so)

| ID | Where | What | Class |
|---|---|---|---|
| D1 | Close account banner (`13-close-account-preflight-1280.png`, `13a-close-account-1280.png`) | Text reads "...rwWmGwn...Zzhv(currently holds 1,000,000,000 drops)": no space before "(". Same family as commit `7d2ecaa` | minor, text/markup, out of scope |
| D2 | Unlock screen after Lock (`29-unlock-1280.png`, `50-unlock-900.png`, `64-neg-lock-from-keyboard.png`) | A stale "Vault unlocked - The password worked, and every entry checked out clean." notice shows before any attempt | functional, probably pre-existing, not theme, out of scope |
| D3 | History row (`26-history-1280.png`) | The link wraps after "xrpl-" (hyphen break) instead of anywhere | cosmetic, minor |
| D4 | Run finished (`19-run-finished-1280.png`) | A disabled Cancel stays beside Dismiss after "Published." | observation |
| O1 | `shell.js` focusout fallback (`9c256bd`) with Escape in More | The item that had focus is removed from the DOM and the fallback runs on `setTimeout(0)` before Blazor's `FocusAsync`, so focus visits `#xf-content` for one frame and then lands on More (log: `OUT xf-more-item rel=null`, `IN xf-content`, `OUT xf-content rel=BUTTON`, `IN xf-tab [More]`, 3 of 3). The final state is correct; the hop may be announced by a screen reader. Possible fix: skip the fallback when the lost control sits in `.xf-more` and is no longer connected (Blazor refocuses explicitly in both its Escape and choose-from-More paths) | minor, shell.js; the check itself passes (final focus on More), so not fixed |
| O2 | Disabled controls | 45% opacity (spec section 2) gives text 2.12-4.15:1 and borders 1.64-3.65:1; exempt by 1.4.3 / 1.4.11, listed in 6.2 | by design |
| O3 | Accounts, stand key row (`09-accounts-1280.png`) | The row still reads "Closed - sent 797,967,100 drops..." although the account was refunded later and publishes fine; an earlier session's local record | data, not theme |
| O4 | Add key (Label, Network, Seed, Backup password) and Read (Link) | `<label class="form-label">` is not associated with its `<input>` (no `for`/`id`), axe `label` CRITICAL; the same on `main` | pre-existing, a11y, out of scope |
| O5 | `.alert-danger` | 4.58:1 on its own tint (#342725): the thinnest margin over 4.5 among the banners; fine as shipped, any tint change needs re-measuring | note |
| O6 | Windows text size | The 150% setting reaches the page as devicePixelRatio 1.5, not as a larger font | environment |
| O7 | Keys table at 600 | Focus on a row's buttons scrolls the table card horizontally, hiding the label column (`60-kbd-600-tab16-more.png`) | expected (spec 3: tables scroll in their card) |
| O8 | Lab | The 15-minute idle auto-lock fires during long keyboard-only sessions (keystrokes are not activity); unlock again with the keyboard | environment |

## 9. Fixes made during Task 8

| Commit | Files | Why |
|---|---|---|
| `9c256bd` `fix(app): focus on a hidden nav control reaches the content region even when the engine drops it first` | `app/XrplFile.App/wwwroot/shell.js` (+20 lines) | Step 5 failed about half the time: `blur` and `focusout` (relatedTarget null) fire before the media-query `change` event, so `document.activeElement` was `<body>` at `change` and the existing guard skipped the hand-off to `#xf-content`. Fix: a once-registered (bubbling) `focusout` listener on `document`; when focus leaves a control inside `.xf-rail, .xf-tabbar, .xf-topbar` with relatedTarget null, a `setTimeout(0)` check: if `activeElement` is body/null and the lost element no longer has client rects, focus `#xf-content`. The `change` handler stays. After redeploy 10 of 10 (rail) and 4 of 4 (tab bar) passes; build `dotnet build app/XrplFile.App/XrplFile.App.csproj -c Debug` 0 warnings 0 errors; `dotnet run --project tests/XrplFile.App.Core.Tests` Total 535, Failed 0 |

Phase 8c (the keyboard and contrast work in this report) made no code change: all its checks passed or are exempt.

## 10. Screenshot index (67 files; 66 is added in section 11)

Step 3 (1280): `01-keys-1280.png`, `02-add-key-1-1280.png`, `03-add-key-2-1280.png`, `04-add-key-3-1280.png`, `05-reveal-seed-prompt-1280.png`, `06-reveal-seed-1280.png`, `07-export-key-1280.png`, `08-export-key-done-1280.png`, `08b-export-key-qr-1280.png`, `09-accounts-1280.png`, `10-new-account-1280.png`, `11-new-account-done-1280.png`, `12-funding-1280.png`, `13-close-account-preflight-1280.png`, `13a-close-account-1280.png`, `14-publish-picker-1280.png`, `15-publish-file-key-selected-1280.png`, `16-estimate-1280.png`, `17-estimate-message-1280.png`, `18-run-running-1280.png`, `19-run-finished-1280.png`, `20-run-cancelled-1280.png`, `21-unfinished-runs-1280.png`, `22-read-entry-1280.png`, `23-read-manifest-1280.png`, `24-read-reading-1280.png`, `25-read-finished-1280.png`, `26-history-1280.png`, `27-settings-1280.png`, `27b-settings-scrolled-1280.png`, `28-change-password-1280.png`, `29-unlock-1280.png`, `30-unlock-wrong-password-1280.png`, `31-create-vault-1280.png`, `32-settings-different-vault-1280.png`, `33-vault-unreachable-1280.png`.

Step 4 (900 / 600): `34-keys-900.png`, `35-keys-600.png`, `36-keys-600-more.png`, `37-history-900.png`, `38-history-600.png`, `39-estimate-900.png`, `40-estimate-600.png`, `41-estimate-message-600.png`, `42-estimate-message-900.png`, `43-estimate-message-600x520.png`, `44-run-finished-900.png`, `45-run-finished-600.png`, `46-run-cancelled-600.png`, `47-run-cancelled-900.png`, `48-read-finished-900.png`, `49-read-finished-600.png`, `50-unlock-900.png`, `51-unlock-600.png`.

Step 7 (text size 150%; there is no 52, a mislabelled early shot was deleted): `53-text150-keys-1280.png`, `54-text150-publish-1280.png`, `55-text150-publish-600.png`, `56-text150-keys-600.png`.

Step 8 (keyboard and negative cases): `57-kbd-1280-tab9.png` (9 Tabs, ring on the first Reveal), `58-kbd-1280-rail-history.png` (ring on the History rail item), `59-kbd-1280-rail-lock.png` (ring on Lock), `60-kbd-600-tab16-more.png` (ring on More), `61-kbd-600-settings-from-more.png` (Settings opened from More), `62-neg-click-content.png`, `63-neg-click-rail-empty.png` (after the two mouse cases, no ring on the rail), `64-neg-lock-from-keyboard.png` (Unlock after Lock from the keyboard), `65-kbd-600-more-open.png` (More open, ring on Accounts).

`62` and `63` are byte-identical on purpose: both end with no focus ring anywhere (focus on `#xf-content` or on `body`), which is the visible half of 8.7 and 8.8; the focus facts are in the logs above.

## 11. Fixes after the final review

Five findings of the final whole-branch review, all fixed. Build `dotnet build app/XrplFile.App/XrplFile.App.csproj -c Debug`: 0 warnings, 0 errors; `dotnet run --project tests/XrplFile.App.Core.Tests`: Total 535, Failed 0. The build was redeployed to the guest (`C:\Lab\app`, from this worktree) and unlocked; checks 2, 4 and 5 below ran there.

| # | Finding | Commit | Change | Check |
|---|---|---|---|---|
| 1 | `.btn-outline-danger` pressed state: `--negative` text 4.12:1 over a card | `6309d20` | `--bs-btn-active-bg` alpha 0.20 to 0.14 in `app.css` | arithmetic below |
| 2 | Escape closed More only while focus was inside `.xf-tabbar` | `8596614` | `shell.js`: one document-level `keydown` listener (registered once) calls `[JSInvokable] Shell.OnEscape` only while `#xf-more` is in the DOM; `Shell.razor`: `OnEscape` asks `ShellModel.MoreOpen` and calls the existing `CloseMoreAndReturnFocus`; the `@onkeydown` on the nav and `OnTabbarKey` are gone | VM, below |
| 3 | `.xf-content { overflow-x: hidden }` clipped unbreakable strings outside `<code>` | `6309d20` | `overflow-wrap: break-word` added to `.xf-content` | CSS only; tables still scroll in their cards |
| 4 | Run finished showed a disabled Cancel beside Dismiss | `31a7d3b` | `Run.razor`: Cancel rendered under `@if (!view.IsFinished)` | VM, below, `66-run-finished-600-after-review.png` |
| 5 | On Escape in More the removed item's `focusout` fallback hopped focus to `#xf-content` for a frame | `8596614` | `shell.js`: `if (!lost.isConnected) return;` inside the fallback's `setTimeout` | VM, below |

**Finding 1, arithmetic.** Colour `--negative` = rgb(214, 119, 106); card = #1A1A1A = rgb(26, 26, 26). Composite = text x alpha + card x (1 - alpha); WCAG ratio = (L1 + 0.05) / (L2 + 0.05) with sRGB-linearised luminance.
- alpha 0.20: composite rgb(63.6, 44.6, 42.0), ratio 4.14:1 (the review's 4.12 is the same figure to rounding; below 4.5).
- alpha 0.14: composite rgb(52.3, 39.0, 37.2), ratio 4.58:1 (equal to `.alert-danger`, O5).

**Check 2 (Escape with focus outside the nav), 600 px, real keystroke.** More opened, focus put on the header Lock button (`.xf-topbar`, outside `<nav class="xf-tabbar">`), real Escape sent by SendKeys (`isTrusted=true`). Event log, verbatim tail:
`"IN xf-icon-button [Lock the vault] ... more=true/panel ...","KEY Escape trusted=true","OUT xf-icon-button rel=BUTTON","IN xf-tab [More] rgb(100, 207, 149) 2px solid off=2px more=false/nopanel fv=true vis=true"] | active=xf-tab | expanded=false panel=false`
Verdict: PASS: panel closed (`aria-expanded=false`, no `#xf-more`), focus on the More button with the focus ring.

**Check 5 (no hop to `#xf-content`), 600 px, real keystroke.** More opened (focus on Accounts), real Escape. Event log, verbatim tail:
`"IN xf-more-item [Accounts] ... more=true/panel ...","KEY Escape trusted=true","OUT xf-more-item rel=null","IN xf-tab [More] rgb(100, 207, 149) 2px solid off=2px more=false/nopanel fv=true vis=true"] | active=xf-tab | expanded=false panel=false`
Verdict: PASS: no `focusin` on `xf-content` between the Escape and the focus on More.

**Check 4 (Run finished, action bar), 600 px.** A real publication of `C:\Lab\small.bin` (4,096 bytes, 5 chunks, 8 transactions) from "stand key", published link `xrpl-file://1/8C1B3917653318E3298F489E257C5C59927FE55B75B3F2F9845C43349283EC9A`. Output verbatim: `{"finished":true,"bar":["Dismiss"],"over":"[true,true,600,600,[600,600]]", ...}`. Verdict: PASS: the action bar holds only Dismiss; no horizontal overflow. Screenshot: `66-run-finished-600-after-review.png`. The run was dismissed afterwards; no existing unfinished run was resumed.
