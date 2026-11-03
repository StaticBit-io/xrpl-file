# Desktop app UI languages — acceptance report

Design: `docs/superpowers/specs/2026-11-02-app-ui-languages-design.md`. Plan:
`docs/superpowers/plans/2026-11-02-app-ui-languages-plan.md`.

## Result

| Criterion | Result |
|---|---|
| Languages | The site's six: en, ru, de, es, pt, zh (zh-Hans), 511 phrases each |
| Default language | Remembered choice, else the OS UI culture by primary subtag, else English (`LanguageTests`) |
| Picker | Settings > Language; applies at once without a restart; remembered in `ui-language.txt` (checked in the VM: Russian survived an app restart) |
| A language missing a key fails a test | `CatalogTests.Every_language_has_exactly_the_English_phrases` (it was red for five languages until the catalogs landed) |
| Ids and catalog agree | `CatalogTests.The_code_declares_exactly_the_English_phrases` |
| Same values, plural forms, every phrase renders | `CatalogTests` (placeholders per phrase, CLDR forms per language, render with 0/1/2/5/21) |
| App.Core suite | 965 / 965 green (`-trait- Category=Integration -trait- Category=Slow`) |
| Build | `app/XrplFile.App` and the test project: 0 warnings, 0 errors |

## VM check (MauiLab)

Driven with mouse and keys through the guest's `C:\Lab\ui.ps1` (no DevTools in this build), hypervisor
screenshots cropped to the window. A fresh vault with a generated throwaway password and one generated
testnet key were used; the lab vault was set aside and restored afterwards, byte sizes unchanged.

| Width | English | Russian | German | Chinese |
|---|---|---|---|---|
| 1400 | create vault, accounts, new account, funding, publish, read, history, settings | unlock, accounts, new account, funding, publish, read, history, settings | accounts, settings | — |
| 600 | accounts, new account, funding, publish, read, history, settings | accounts, new account, funding, publish, read, history, settings, More menu | accounts, funding, publish, settings | accounts, funding, publish, settings |

Files: `<lang>-<width>-<screen>.png` in this folder.

Found and fixed during the check: the 72 px rail could not hold the Russian "Заблокировать" (nor German
"Veröffentlichen"/"Einstellungen", Portuguese "Configurações"); the rail grew a horizontal scrollbar and
cut the label. The rail labels are now short in those catalogs (page titles keep the full words), and
`app.css` cuts any rail or tab-bar label longer than the rail with an ellipsis instead of scrolling.

Not covered by screenshots: screens that need a funded account or a published file (estimate, run,
unfinished runs with rows, close account, read results, export/reveal key, damage). Their text is covered
by the catalog tests and the model tests rendering through the English catalog.

## Wording notes for the owner

- English changes are plural corrections only ("1 byte", "1 record", "1 block", "About 1 ledger
  remaining"); everything else is today's wording.
- Russian follows the site ("реестр", "версия реестра", "фрагмент", "отпечаток"), except "publish", which
  the site renders as «записать»: the app has both a Publish tab and a "Writing the file" stage, so
  publish is «публикация» and writing is «запись».
- Terms the site does not cover were chosen per language and applied throughout (ru: хранилище,
  мастер-пароль, сид, тикет, дроп, плательщик, записывающий аккаунт). Batch, NetworkID, AccountDelete and
  network names stay in Latin script.
- A few English phrases read oddly in every language and may deserve a look: `Run.MidDone` ("Stopped while
  finished"), `Accounts.StatusAtLedger` ("At ledger N, M to go" means "closable from ledger N"),
  `Unfinished.DeleteNote`, and `Vault.UnreachableLead` ("it is not offered to create a new one").
