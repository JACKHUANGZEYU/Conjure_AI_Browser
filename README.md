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
- Tabs: multiple Chromium tabs with close buttons on each tab and a “+” new-tab button in the tab strip.
- AI panel (per tab):
  - Toggleable panel with model selector and API key box.
  - API key history dropdown (prefills from `GEMINI_API_KEY`; remembers keys you’ve used).
  - Chat-style UI: scrolling conversation log; fixed-height, scrollable input; send button (➤).
  - Each tab keeps its own conversation, model, key, and visibility state; uses current page text as context.

## Running
From repo root:
```powershell
dotnet restore
dotnet build
dotnet run --project .\src\ConjureBrowser.App
```
Or open `ConjureBrowser.sln` in Visual Studio, set `ConjureBrowser.App` as Startup Project, and F5. If CefSharp complains about natives, set platform to `x64` (Build → Configuration Manager).

## Gemini Setup
- Set `GEMINI_API_KEY` environment variable (recommended) or pick a saved key from the dropdown / paste a new one.
- Choose a model (`gemini-2.5-flash` or `gemini-3.0-pro`).
- Type in the chat box and hit send (➤); each tab keeps its own chat history.

## Next Steps
- Tab close/reorder polish and session persistence.
- Stream Gemini responses with better error surfacing.
- History/downloads UI and settings.
