# Contributing to Amber

Thanks for helping. This page covers code and translations. The UI, comments and docs are written in Simplified Chinese first; English is fine for issues and pull requests.

## Ground rules

- Original code only. Don't copy or port code from other launchers or from decompiled binaries. Public protocol and format docs are fine.
- No telemetry, analytics or remote kill switches.
- Never test against a real `%APPDATA%\.minecraft`; use a separate test game directory.
- Use established cryptographic primitives; don't invent new ones.
- New behaviour gets unit tests, and a GUI self-check step when it is visible in the UI.

## Build and test

```bash
cd src
dotnet build Amber.slnx -c Release
dotnet test Amber.Core.Tests -c Release
```

GUI self-check (drives the real window and writes `report.txt`):

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-selftest.ps1 -Report <dir>
```

Layout check only (every page × 4 languages × 6 window sizes, screenshots plus a list of clipped controls in `layout.txt`):

```bash
AMBER_LAYOUTCHECK=<dir> src/Amber.App/bin/Release/net10.0/Amber.exe
```

## Translations

UI text lives in `src/Amber.Core/Localization/`. Use the terms in [docs/i18n-glossary.md](docs/i18n-glossary.md) (for example, an installed game folder is an *instance*, never a *version*).

| File | Language |
|---|---|
| `zh-Hans.json` | Simplified Chinese — the source; every key is defined here |
| `zh-Hant.json` | Traditional Chinese (Taiwan wording) |
| `en.json` | English (US) |
| `ja.json` | Japanese |

Each file is a flat JSON object `"key": "text"`, keys sorted alphabetically.

- In XAML use `{l:T some.key}` (with `xmlns:l="using:Amber.App.Localization"`); in C# use `L.T("some.key")`, or `L.F("some.key", a, b)` for text with `{0}`, `{1}` placeholders.
- Never write user-visible Chinese (or any other language) directly in `Amber.App`; the test `NoHardCodedChineseInUi` fails if you do.
- Keep every placeholder (`{0}`, `{1:F0}`, `{0:yyyyMMdd-HHmm}`) in every language; `TranslationsAreCompleteAndKeepPlaceholders` checks this and that all languages have the same keys.
- Use the official Minecraft terms for each language (World / ワールド / 世界, Resource Pack, Data Pack, Shader Pack, Mod / MOD / 模組, Server / サーバー / 伺服器, Open to LAN / LANに公開 / 在區域網路上開放).
- The key name says where the text appears (`page.area.purpose`). If two places share the same Chinese text but need different words in another language, give them separate keys.
- After changing text, run the layout check above: longer translations must wrap or trim, not get cut off.

Adding a language: add `<code>.json` with every key from `zh-Hans.json`, add the code and its own name to `L.Languages` in `src/Amber.Core/Localization/L.cs`, map the system culture in `L.FromCulture`, and add the code to the translation test.

## Pull requests

Keep changes focused, describe what you tested, and include before/after screenshots for UI changes.
