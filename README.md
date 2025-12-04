# Conjure AI Browser (WPF + Chromium/CEF)

Conjure AI Browser is a Windows desktop browser being built in stages. The current drop is a first runnable version with a Chromium engine (CEF via CefSharp), basic navigation, bookmarks, and a tiny AI panel stub you can later replace with a real LLM.

## Project Structure
- `ConjureBrowser.sln` — solution entry point.
- `assets/` — images and icons (currently empty).
- `docs/` — documentation (currently empty).
- `src/ConjureBrowser.App/` — WPF UI project. Hosts the CefSharp browser control, toolbar, bookmarks menu, and AI panel.
- `src/ConjureBrowser.Core/` — core logic: bookmarks, URL helpers, persistence.
- `src/ConjureBrowser.AI/` — AI abstraction + a simple placeholder assistant implementation.
- `tests/` — reserved for future tests.

## Tech Stack
- UI: WPF (.NET 8, `net8.0-windows`)
- Engine: Chromium Embedded Framework via `CefSharp.Wpf.NETCore`
- Language: C#
- Runtime target: `win-x64` (CefSharp pulls native Chromium binaries for x64)

## Prerequisites
- Windows 10/11
- .NET SDK 8+
- Visual Studio with **.NET desktop development** workload
- NuGet restore (downloads CefSharp + bundled Chromium)
- If native VC++ runtime is missing at run-time, install the **Microsoft Visual C++ Redistributable 2015–2022**.

## What Works in this First Version
- Address bar with Enter-to-navigate or search.
- Back / Forward / Reload / Home.
- Bookmark toggle (★/☆), bookmarks menu, persistence to `%LocalAppData%\\ConjureBrowser\\bookmarks.json`.
- Simple AI panel stub:
  - **Summarize**: grabs page text (`document.body.innerText`) and produces a naive summary.
  - **Ask**: keyword-based snippet search over the page text.

## Running the App
From the repository root:
```powershell
dotnet restore
dotnet build
dotnet run --project .\src\ConjureBrowser.App
```

Or open `ConjureBrowser.sln` in Visual Studio, set `ConjureBrowser.App` as the startup project, and press F5.

**Tip:** If CefSharp complains about missing native binaries, set the solution platform to `x64` in Build → Configuration Manager.

## Next Steps (suggested)
- Add a tab system (tab strip + per-tab Chromium instances).
- Wire a real AI provider in `ConjureBrowser.AI` (OpenAI/local model) and stream responses.
- Add history, downloads UI, and settings.
