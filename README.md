# Conjure AI Browser (WPF + Chromium/CEF)

Conjure AI Browser is a Windows desktop browser being built in stages. The current drop is a first runnable version with a Chromium engine (CEF via CefSharp), basic navigation, bookmarks, and a Gemini-backed AI panel.

## Project Structure
- `ConjureBrowser.sln` — solution entry point.
- `assets/` — images and icons (currently empty).
- `docs/` — documentation (currently empty).
- `src/ConjureBrowser.App/` — WPF UI project. Hosts the CefSharp browser control, toolbar, bookmarks menu, and AI panel.
- `src/ConjureBrowser.Core/` — core logic: bookmarks, URL helpers, persistence.
- `src/ConjureBrowser.AI/` — AI abstractions + Gemini client.
- `tests/` — reserved for future tests.

## Tech Stack
- UI: WPF (.NET 8, `net8.0-windows`)
- Engine: Chromium Embedded Framework via `CefSharp.Wpf.NETCore`
- Language: C#
- Runtime target: `win-x64` (CefSharp pulls native Chromium binaries for x64)
- AI: Google Gemini (UI options: `gemini-2.5-flash`, `gemini-3.0-pro`; mapped to API models `gemini-2.5-flash` / `gemini-3.0-pro-preview`)

## Prerequisites
- Windows 10/11
- .NET SDK 8+
- Visual Studio with **.NET desktop development** workload
- NuGet restore (downloads CefSharp + bundled Chromium)
- If native VC++ runtime is missing at run-time, install the **Microsoft Visual C++ Redistributable 2015–2022**.

## What Works in this Version
- Address bar with Enter-to-navigate or search.
- Back / Forward / Reload / Home.
- Bookmark toggle (★/☆), bookmarks menu, persistence to `%LocalAppData%\ConjureBrowser\bookmarks.json`.
- AI panel (Gemini):
  - Model selector: `gemini-2.5-flash` or `gemini-3.0-pro`.
  - API key entry (auto-fills from `GEMINI_API_KEY` env var if set; you can also paste directly).
  - **Summarize page**: sends full page text (truncated to ~20k chars) to Gemini for a concise summary.
  - **Ask**: sends question + page text to Gemini for an answer.

## Running the App
From the repository root:
```powershell
dotnet restore
dotnet build
dotnet run --project .\src\ConjureBrowser.App
```

Or open `ConjureBrowser.sln` in Visual Studio, set `ConjureBrowser.App` as the startup project, and press F5.

**Tip:** If CefSharp complains about missing native binaries, set the solution platform to `x64` in Build → Configuration Manager.

### Gemini setup
- Set an environment variable `GEMINI_API_KEY` (recommended), or paste your key into the AI panel field.
- Pick the model in the AI panel (`gemini-2.5-flash` or `gemini-3.0-pro`).
- Click “Summarize page” or “Ask” to get a response in the AI panel.

## Next Steps (suggested)
- Add a tab system (tab strip + per-tab Chromium instances).
- Stream Gemini responses and add error surfacing in the UI.
- Add history, downloads UI, and settings.
