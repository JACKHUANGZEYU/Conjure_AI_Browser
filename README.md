# Conjure AI Browser (WPF + Chromium/CEF)

Conjure AI Browser is a Windows desktop browser built on WPF with an embedded Chromium engine (CefSharp) and a built-in Gemini assistant.

## Project Structure
- `ConjureBrowser.sln` – solution entry point.
- `src/ConjureBrowser.App/` – WPF UI (tabs, toolbar, AI panel).
- `src/ConjureBrowser.Core/` – bookmarks, URL helpers, persistence.
- `src/ConjureBrowser.AI/` – AI abstractions + Gemini client.
- `assets/`, `docs/`, `tests/` – assets, documentation, and future tests.

## Tech Stack
- UI: WPF (.NET 8, `net8.0-windows`)
- Engine: CefSharp (`CefSharp.Wpf.NETCore`) for embedded Chromium
- Runtime: `win-x64`
- AI: Google Gemini (UI options: `gemini-2.5-flash`, `gemini-3.0-pro`, mapped to API models `gemini-2.5-flash` / `gemini-3.0-pro-preview`)

## Features
- Navigation: address bar (Enter to go/search), Back/Forward/Reload/Home.
- Bookmarks: toggle ★/☆, menu, persisted to `%LocalAppData%\ConjureBrowser\bookmarks.json`.
- Tabs: multiple Chromium tabs with a “＋” new-tab button and tab headers at the top.
- AI panel:
  - Model selector + API key box (prefills from `GEMINI_API_KEY` if set).
  - Chat-style UI: scrolling conversation log; compact, scrollable input box (multi-line, fixed height) with send button.
  - Uses page text as context for responses.

## Running
From repo root:
```powershell
dotnet restore
dotnet build
dotnet run --project .\src\ConjureBrowser.App
```
Or open `ConjureBrowser.sln` in Visual Studio, set `ConjureBrowser.App` as Startup Project, and F5. If CefSharp complains about natives, set platform to `x64` (Build → Configuration Manager).

## Gemini Setup
- Set `GEMINI_API_KEY` environment variable (recommended) or paste your key into the AI panel.
- Pick a model (`gemini-2.5-flash` or `gemini-3.0-pro`).
- Type in the chat box and hit send (➤). Conversation history stays in the panel; input box is scrollable when long.

## Next Steps
- Add tab close/reorder and session persistence.
- Stream Gemini responses with better error surfacing.
- History/downloads UI and settings.
