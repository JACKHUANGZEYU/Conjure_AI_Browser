# Conjure AI Browser (WPF + CEF/Chromium)

Windows desktop browser built with WPF, CefSharp (Chromium), and a built-in Gemini assistant.

## Project Structure
- `ConjureBrowser.sln` - solution entry.
- `src/ConjureBrowser.App/` - WPF UI (toolbar, tabs, AI panel, settings tab).
- `src/ConjureBrowser.Core/` - bookmarks, URL helpers, persistence.
- `src/ConjureBrowser.AI/` - AI abstractions plus Gemini client.
- `assets/`, `docs/`, `tests/` - assets, docs, and future tests.

## Tech Stack
- .NET 8 (`net8.0-windows`), WPF
- Embedded engine: CefSharp (`CefSharp.Wpf.NETCore`) on Chromium
- Runtime: win-x64
- AI: Gemini models (`gemini-2.5-flash`, `gemini-3.0-pro` -> API `gemini-3-pro-preview`)

## Features
- Navigation: address bar (Enter to go/search), Back/Forward/Reload/Home.
- Bookmarks: toggle star, bookmarks menu; persisted to `%LocalAppData%\ConjureBrowser\bookmarks.json`.
- Tabs: multiple Chromium tabs with close buttons and a `+` new-tab button.
- AI panel (per tab, toggle via `AI`):
  - Model picker only; uses the global API key from the Settings tab.
  - Chat-style log with per-tab conversation memory; scrollable log and fixed-height input with send button.
  - Uses current page text as context when answering.
- Settings tab (gear button): set a global Gemini API key shared across existing and new tabs (only place to enter it).

## Running
From repo root:
```powershell
dotnet restore
dotnet build
dotnet run --project .\src\ConjureBrowser.App
```
Or open `ConjureBrowser.sln` in Visual Studio, set `ConjureBrowser.App` as Startup Project, and press F5. If CefSharp complains about native binaries, set platform to `x64` (Build -> Configuration Manager).

## AI Usage
1) Open the AI panel (toggle `AI`).
2) Choose a model (`gemini-2.5-flash` or `gemini-3.0-pro`).
3) Set a global API key once in the Settings tab (gear). The AI panel will use it automatically.
4) Type in the input box and press Enter or click send.
5) Each tab keeps its own chat history; Enter only sends when the chat input is focused and non-empty.

## Notes / Next Ideas
- Tab reordering and favicon display.
- History/downloads UI.
- Streaming Gemini responses and richer error messages.
