# Desktop app UI languages — plan

Design: `docs/superpowers/specs/2026-11-02-app-ui-languages-design.md`. Infrastructure is in place
(`app/XrplFile.App.Core/Localization/`, `Components/LocalizedComponent.cs`, Shell cascade, Settings
picker). What is left is moving every user-visible string onto it, area by area, then translating.

## Steps

1. Shell, vault and Settings — `Vault`, `Settings`, `Shell`, `Network` areas.
2. Accounts and keys — `Accounts`, `Keys`, `Funding`, `Close` areas.
3. Publish and estimate — `Publish`, `Estimate` areas.
4. Run, unfinished runs, history — `Run`, `Unfinished`, `History` areas.
5. Read — `Read` area.
6. Translations: `ru`, `de`, `es`, `pt`, `zh` (one catalog each, independent).
7. VM screenshots: every screen in `en` and `ru` at 1400 and 600 px; `de` and `zh` spot checks.

Steps 1–5 run one after another (they share `en.json` and some message classes); step 6 runs per
language in parallel.

## Conventions for steps 1–5

**Ids.** One class per area, `app/XrplFile.App.Core/Localization/<Area>Text.cs`, shaped exactly like
`SettingsText.cs`: `public static readonly TextId Name = Id();` and
`private static TextId Id([CallerMemberName] string name = "") => TextId.Of("<Area>", name);`. The area
name is the class name without `Text`. Field names describe the phrase's role (`FundButton`,
`BalanceNotFunded`), not its words. An area class may already exist (`SettingsText`): extend it.

**English catalog.** `app/XrplFile.App.Core/Localization/Text/en.json`, flat keys `"<Area>.<Field>"`, one
block per area, in the order the ids are declared. The value is today's English wording, unchanged:
the copy was reviewed and tests assert it. Do not touch the other five catalogs.

**Composition.** No sentence is built by concatenating phrases or by lowering a phrase into another.
Each variant is its own phrase ("Stopped while {stage}" takes the stage as a `UiText` value, whose own
phrase is written for that position if English needs a different form there). Singular/plural
ternaries become plural phrases: `{ "one": "{count} ticket", "other": "{count} tickets" }`, with the
number passed as `count`.

**Values.** Pass numbers as `long`/`int` (grouped in the language's locale automatically; give a format
in the template when needed, `{rate:N1}`), XRP amounts as `new XrpAmount(drops)`, durations as
`DurationText.Of(...)`, sub-phrases as `UiText`/`TextId`. Do not pre-format a number into a string.
Addresses, hashes, links, file names, network names and node URLs are passed as strings, untranslated.

**Models.** A user-visible `string` property or return value becomes `UiText` (`UiText?` where it was
nullable). Message classes (`*Messages`, `UnlockMessage`, `CreateMessage`) return `UiText`; a `const
string` becomes a `static UiText` property or a `TextId`. Model decisions compare `UiText`s (structural
equality) or ids, never rendered text. Strings for logs, trace lines, `StartupLog`, exception messages
meant for developers, and anything persisted to disk stay English and stay `string`. An enum shown on
screen gets a Core method mapping it to a `TextId` (`ShellModel.NameOf(AppTab)`).

**Razor.** Components inherit `LocalizedComponent` (via `_Imports.razor`) and only bind:
`@T[AreaText.X]`, `@T[Model.Message]` (null renders as nothing), `@T.Number(n)` instead of
`ToString("N0")`, attributes too (`aria-label`, `title`, `placeholder`, `alt`). A sentence with inline
markup around a value uses `@Html(UiText.Of(...), ("name", "strong"))` (strong, em, code, kbd). A
sentence with a button or link inside is split into phrases around the control. Razor comments stay.

**Bridges.** If a converted message class is consumed by a model of a later area that still holds
`string`, render it at that consumer as
`TextCatalog.For(UiLanguage.English).Render(...) /* LOCALIZE(bridge) */` and leave it; the area that owns
the consumer removes it. `RunModel.EnglishUntilConverted` is such a bridge, owned by step 4. After step 5
`grep -rn "LOCALIZE(bridge)\|EnglishUntilConverted"` finds nothing.

**Tests.** Existing assertions keep their English words: `Assert.Equal("Not funded yet.",
TestText.En(model.BalanceLine))`. Where a test compared to a message constant, compare the `UiText`s.
No assertion is deleted or weakened. Add a test where a decision moved (a plural phrase, a variant
split).

**Gates for every step.**
- `dotnet build app/XrplFile.App` and `dotnet build tests/XrplFile.App.Core.Tests`: 0 warnings, 0 errors.
- `dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration" -trait- "Category=Slow"`:
  the only failures are `CatalogTests.Every_language_has_exactly_the_English_phrases` (five, one per
  untranslated language) until step 6.
- `grep` the area's razor files for text outside `@T`/`@Html`: none left but punctuation, symbols and
  untranslatable names.
- One conventional commit per step, English, no attribution lines.
