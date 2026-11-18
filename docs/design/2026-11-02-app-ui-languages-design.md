# Desktop app UI languages — design

Approved by the owner on 2026-11-02 (numbers by locale; XRPL terms translated where the site already
translates them, amendment and transaction type names kept in Latin script).

## Intent

The desktop app (`app/XrplFile.App`, logic in `app/XrplFile.App.Core`) speaks the same languages as
the project site and the `<xrpl-file>` element, with the same vocabulary, so a person reads one set of
words on the site, in the element and in the app.

## Languages and how one is picked

The site's set, in the site's menu order: `en`, `ru`, `de`, `es`, `pt`, `zh`
(`site/src/js/languages.mjs`). Names are written in their own language: English, Русский, Deutsch,
Español, Português, 中文. Chinese is Simplified and tagged `zh-Hans`.

Formatting locales are the site's: `en-GB`, `ru-RU`, `de-DE`, `es-ES`, `pt-BR`, `zh-Hans-CN`. Numbers,
XRP amounts and byte counts are formatted in the language's locale (`1,5 XRP` in Russian). Addresses,
hashes, `xrpl-file://` links and log/trace lines are never localized.

The language at start-up is, in order: the remembered choice when it is a supported language; the OS
UI culture matched by primary subtag (`zh-TW` is `zh`, `pt-PT` is `pt`, `es-419` is `es`); English.
This is the site's `getLang` with the OS in place of the browser.

The choice is made in Settings (a select of native names), applies at once without a restart, and is
remembered in `ui-language.txt` beside `node-urls.json`. An unreadable or unknown value reads as
"nothing chosen", the contract every settings store here follows.

## Architecture

Everything that decides lives in App.Core, namespace `XrplFile.App.Core.Localization`, and is tested on
Linux. Razor components only bind.

- `UiLanguage` — the six languages: code, native name, HTML tag, formatting culture; lookup by tag.
- `TextId` — identifies one phrase. Ids are declared as static fields in one static class per area
  (`AccountsText.Fund`, `RunText.StageHashing`), so a typo is a compile error and no string key is
  written at a call site. The key is `"<Area>.<Field>"`; a test checks every id against that rule.
- `UiText` — a phrase id plus named arguments. Models expose `UiText` instead of `string` for every
  user-visible text, so an already shown message is re-rendered in the new language when the language
  changes. An argument may itself be a `UiText` ("Stopped while {stage}" takes the stage as a phrase,
  not as a lowered English string).
- Catalogs — one JSON file per language, `Localization/Text/<code>.json`, embedded resources, flat keys.
  Placeholders are named, with an optional .NET format: `{count}`, `{rate:N1}`. A plural phrase is an
  object of CLDR forms, chosen by the `count` argument: `ru` has `one`/`few`/`many`, `en`/`de`/`es`/`pt`
  `one`/`other` (Portuguese `one` covers 0 and 1), `zh` `other`. Integers only; the compact-exponent
  `many` form of `es`/`pt` is not used.
- `TextCatalog` — one immutable catalog per language: looks a phrase up, renders a `UiText`, formats
  numbers, XRP amounts, byte counts and durations in the language's locale. Never reads or writes the
  ambient `CultureInfo`.
- `Localizer` — the one localization service (singleton): the current language and catalog, the
  choices, `Choose(language)` (applies, remembers), `Changed`.
- `ILanguageSettingsStore` / `FileLanguageSettingsStore` — the remembered choice.

In the app, `Shell` cascades the current `TextCatalog`; a changed language swaps the catalog and the
cascade re-renders every screen without losing component state. All components inherit a base that
receives it as `T`: `@T[AccountsText.Fund]`, `@T.Render(Model.Message)`. `<html lang>` follows the
language (`zh-Hans` for Chinese) so WebView2 picks Simplified Chinese glyphs.

## Tests

1. Every language catalog has exactly the English keys, the same placeholder names, and the plural
   forms its rule needs. A missing key fails the suite.
2. Every declared `TextId` is in the English catalog and every English key is a declared `TextId`.
3. Language decisions: OS tag matching, fallback, remembered choice, unreadable store, plural rules
   (`ru` 1/2/5/11/21/22, `pt` 0/1/2).
4. Existing App.Core tests keep their English expectations and read model texts through an English
   rendering helper.

## Builder's calls

- Ids are static fields grouped per area rather than one enum: the same compile-time safety, and an
  area's ids live next to each other in their own file.
- The picker lives only in Settings, reached after unlocking; screens before unlocking follow the
  remembered choice or the OS.
- Translations of `ru`, `de`, `es`, `pt`, `zh` are written from the site's and the element's existing
  wording wherever the same phrase appears there.

## Acceptance

Every screen renders in every language; App.Core, Keys and Core suites green; screenshots in the
MauiLab VM in English and Russian at 1400 and 600 px, spot checks in German and Chinese.
