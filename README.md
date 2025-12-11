## Running (Development)
From repo root:
```powershell
dotnet restore
dotnet build
dotnet run --project .\src\ConjureBrowser.App
```

## Building & Installing as a Windows Application

### Build a Standalone Release
To create a self-contained Windows executable (no .NET installation required):
```powershell
dotnet publish .\src\ConjureBrowser.App -c Release -r win-x64 --self-contained -o .\publish
```

This creates a `publish` folder containing:
- `ConjureBrowser.App.exe` — the main executable with embedded icon
- All required DLLs (CefSharp/Chromium, .NET runtime)
- Resource files (`locales/`, `.pak` files)

**Note**: The entire `publish` folder is required to run the app. The exe alone will not work because CefSharp requires the Chromium binaries alongside it.

### Install Shortcuts (Current Machine)
To add Desktop and Start Menu shortcuts:
```powershell
# Desktop shortcut
$WshShell = New-Object -comObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut("$env:USERPROFILE\Desktop\Conjure Browser.lnk")
$Shortcut.TargetPath = "D:\Conjure_AI_Browser\publish\ConjureBrowser.App.exe"
$Shortcut.WorkingDirectory = "D:\Conjure_AI_Browser\publish"
$Shortcut.Description = "Conjure AI Browser"
$Shortcut.Save()

# Start Menu shortcut
$StartMenuPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
$Shortcut = $WshShell.CreateShortcut("$StartMenuPath\Conjure Browser.lnk")
$Shortcut.TargetPath = "D:\Conjure_AI_Browser\publish\ConjureBrowser.App.exe"
$Shortcut.WorkingDirectory = "D:\Conjure_AI_Browser\publish"
$Shortcut.Description = "Conjure AI Browser"
$Shortcut.Save()
```

### Distribute to Other Machines
1. Zip the entire `publish` folder:
   ```powershell
   Compress-Archive -Path ".\publish\*" -DestinationPath "ConjureBrowser-v1.0.zip"
   ```
2. Share the zip file with users
3. Users extract and run `ConjureBrowser.App.exe`

### Application Icon
The app uses `src/ConjureBrowser.App/app.ico` (generated from `Conjure.png`). To regenerate:
```powershell
python -c "from PIL import Image; img = Image.open('Conjure.png'); img.save('src/ConjureBrowser.App/app.ico', format='ICO', sizes=[(256,256),(128,128),(64,64),(48,48),(32,32),(16,16)])"
```

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
- UI Framework: MaterialDesignThemes (Chrome-like dark theme)
- Runtime: win-x64
- AI: Gemini models (`gemini-2.5-flash`, `gemini-3.0-pro` -> API `gemini-3-pro-preview` via v1beta)

## Features
- **Navigation**: Chrome-style address bar with smart URL detection.
  - Type a domain (e.g., `google.com`, `github.com`) and press Enter to navigate directly.
  - Type plain text (e.g., `kfc`, `weather today`) and press Enter to search Google.
  - Explicit URLs with schemes (`https://`, `http://`, `file://`) work as expected.
  - `localhost` and `localhost:port` are recognized as local development URLs.
  - Back/Forward/Reload/Home buttons for standard navigation.
- **Omnibox Suggestions**: Chrome-style autocomplete dropdown as you type in the address bar.
  - Shows suggestions from bookmarks (★), history (⏱), URL detection (🌐), and **live Google Autocomplete** (🔍).
  - Live search suggestions are fetched from Google's public Suggest API as you type.
  - **Auto-closes on Enter** — the dropdown disappears after you press Enter to navigate/search.
  - Keyboard navigation: Up/Down arrows to select, Enter to navigate, Escape to close.
  - Click to navigate in current tab; Ctrl+click or middle-click to open in a new tab.
  - Dropdown stays the same width as the address bar, closes on blur, and appears after a brief typing pause (150ms debounce).
  - Mouse: single-click (when unfocused) selects the full address; double-click sets the caret where you click.
- Bookmarks:
  - **Bookmarks Bar**: Horizontal bar below the address bar displays all bookmarks as clickable buttons.
    - Left-click to navigate in current tab; Ctrl+click or middle-click to open in new tab.
    - Right-click on a bookmark for context menu: Open, Open in new tab, Copy link address, Edit, Remove, Move left, Move right.
    - Right-click on empty bar area to access Bookmark Manager (bookmarks.json) or bookmarks folder.
    - Bookmark ordering is preserved and persisted to `%LocalAppData%\ConjureBrowser\bookmarks.json`.
  - Toggle star (☆/★) in toolbar to add/remove current page from bookmarks.
  - Bookmarks dropdown menu (★▼) shows all bookmarks sorted alphabetically.
- **History**: Automatic browsing history with a dedicated History tab.
  - Access via toolbar button (⏱) or Ctrl+H; opens its own tab without reusing browsing tabs.
  - Search box filters a newest-first list; double-click/Enter opens in the current tab, Ctrl+Enter or middle-click opens in a new tab; context menu offers Open, Open in new tab, Copy link, Remove; "Clear browsing data" clears history.
  - Persists up to 5,000 entries in `%LocalAppData%\ConjureBrowser\history.json`, dropping the oldest when the cap is exceeded.
- **Downloads**: Automatic file download handling with progress tracking.
  - Access via toolbar button (⬇) or Ctrl+J keyboard shortcut.
  - Auto-saves files to Windows Downloads folder (`%USERPROFILE%\Downloads`) without prompting.
  - Auto-renames duplicate files (e.g., "file (1).pdf", "file (2).pdf").
  - Downloads tab shows all downloads with real-time progress bars.
  - Actions: Open file, Show in folder, Cancel (for in-progress downloads).
  - Right-click for context menu: Open, Show in folder, Copy URL, Remove from list.
  - Note: Removing from list does not delete the downloaded file.
- **Tab Favicons + Live Titles**: Chrome-style tab headers with site icons and live page titles.
  - Each tab displays a 16x16 favicon fetched from the site.
  - Fallback globe icon (🌐) shown when no favicon is available.
  - Page titles update live as you navigate (window title also updates).
  - Loading indicator (⟳) appears while pages are loading.
  - Favicons are cached per-host to reduce repeated requests.
  - SVG favicons are skipped (WPF doesn't render SVG natively); fallback to /favicon.ico.
- Tabs: multiple Chromium tabs with close buttons and a `+` new-tab button (Chrome-style, positioned right next to the last tab).
- **Popup Handling**: Links that open new windows/tabs (`target="_blank"`, `window.open()`, OAuth flows) now open inside Conjure as new tabs.
  - User-initiated popups open in a new foreground tab (activated).
  - Background tab requests (`window.open` with `_blank` disposition) open without stealing focus.
  - Silent popups (no user gesture, e.g., pop-unders, ad scripts) are automatically blocked.
  - External schemes (`mailto:`, `tel:`) are passed to the system's default handler.
- **AI Assistant Panel** (per tab, toggle via `AI` button in toolbar):
  - Model picker (`gemini-2.5-flash` or `gemini-3.0-pro`) in the panel; uses the global API key from Settings
  - **Chat-style UI**: User messages in blue bubbles (right-aligned), AI responses as plain text
  - **Markdown Rendering**: AI responses render with proper formatting:
    - **Code blocks** with syntax highlighting, language label, and copy button
    - **Tables** rendered as grids with header styling
    - **Headers** (H1-H4) with appropriate font sizes
    - **Lists** (bulleted and numbered)
    - **Bold and italic** text formatting
    - **Inline code** with monospace font and background
    - **Block quotes** with accent border
    - **LaTeX/math formulas** rendered with proper mathematical notation (powered by WpfMath)
  - Per-tab conversation memory; scrollable log and fixed-height input
  - Uses current page text AND visual content (screenshots) as context when answering
  - Can analyze images, maps, charts, and other visual elements on the page
  - **Resizable panel**: Drag the left edge of the panel to resize (280px-600px range)
  - **Clear Chat button**: Located in top-right corner of panel header
- **AI Tools Menu** (🛠 button in toolbar, next to AI button):
  - **Quick Actions** (one-click AI features, configurable in Settings):
    - `📄 Summarize Page`: Summarize the current page
    - `📌 Key Points`: Extract key points from the page
    - `💡 Explain Selection`: Explain highlighted text on the page
    - `⚖ Compare Tabs`: Select 2+ tabs and generate a comparison
  - Each tool can be enabled/disabled in Settings
  - Custom keyboard shortcuts can be assigned to each tool
- **Find in Page**: Chrome-style find overlay for searching within web pages.
  - Open with Ctrl+F; type to search with incremental highlighting.
  - Enter for next match, Shift+Enter for previous match.
  - Toggle "Aa" button for case-sensitive search.
  - Shows match count as "active/total" (e.g., "2/10").
  - Escape closes the find bar and clears highlights.
  - Only works on web tabs (not History/Downloads/Settings).
- **Session Restore**: Automatically reopens your last browsing session on startup.
  - Restores all web tabs in the same order with the same selected tab.
  - Saves session periodically (every ~750ms after changes) for crash safety.
  - Final save on normal app close guarantees session is captured.
  - Internal tabs (History/Downloads/Settings) are not restored.
  - Maximum 20 tabs restored to prevent runaway restores.
  - Session stored in `%LocalAppData%\ConjureBrowser\session.json`.
  - Corrupt session files are renamed to `session.corrupt.json` for debugging.
- **App Menu (⋮)**: Chrome-style menu button at the top-right of the toolbar for quick access to common actions.
  - Click the "⋮" button to open the menu.
  - Menu contains: New Tab, History, Downloads, Find in Page, Settings, About, Exit.
  - Keyboard shortcuts are shown next to each menu item.
  - "Find in Page" is only enabled when viewing a web tab (disabled on History/Downloads/Settings tabs).
  - History, Downloads, and Settings toolbar buttons have been moved into this menu to reduce toolbar clutter.
  - All actions still work via keyboard shortcuts (Ctrl+H, Ctrl+J, Ctrl+F, etc.).
- **Settings** (via App Menu → Settings):
  - Set a global Gemini API key shared across existing and new tabs.
  - **API key persists across browser restarts** — saved to `%LocalAppData%\ConjureBrowser\settings.json`.
  - Environment variable `GEMINI_API_KEY` can override the persisted key on startup.
  - **AI Tools Configuration**:
    - Enable/disable individual AI tools (Summarize Page, Key Points, Explain Selection, Compare Tabs)
    - Disabled tools won't appear in the 🛠 AI Tools menu
    - Assign custom keyboard shortcuts using dropdown selectors:
      - **Modifier dropdown**: Choose from Ctrl, Ctrl+Shift, Ctrl+Alt, Alt, Alt+Shift
      - **Letter dropdown**: Choose A-Z (reserved letters like C, V, X, etc. are disabled for Ctrl-only)
    - Shortcuts are conflict-free by design — reserved system shortcuts cannot be selected
  - **Import from Chrome**:
    - Import bookmarks and browsing history from Google Chrome
    - Auto-detects Chrome profiles (Default, Profile 1, etc.)
    - Merges imported data with existing Conjure data (duplicates skipped)
    - Supports custom Chrome profile names

## Keyboard Shortcuts

Chrome-style keyboard shortcuts work across web tabs and internal tabs (History/Downloads/Settings).

### Address Bar
| Shortcut | Action |
|----------|--------|
| Ctrl+L | Focus address bar and select all |
| Ctrl+K | Focus address bar and select all |
| Up/Down | Select suggestion (when omnibox is open) |
| Enter | Navigate to selected suggestion or typed URL |
| Escape | Close omnibox suggestions |

Mouse: single-click on an unfocused address bar selects all text; double-click just sets the caret without selecting everything.

### Tabs
| Shortcut | Action |
|----------|--------|
| Ctrl+T | Open new tab |
| Ctrl+W | Close current tab (closes window if last tab) |
| Ctrl+Shift+T | Reopen last closed web tab (up to 20 saved) |
| Ctrl+Tab | Switch to next tab |
| Ctrl+Shift+Tab | Switch to previous tab |
| Ctrl+1 to Ctrl+8 | Jump to tab 1-8 |
| Ctrl+9 | Jump to last tab |

### Navigation (Web Tabs Only)
| Shortcut | Action |
|----------|--------|
| Alt+Left | Go back |
| Alt+Right | Go forward |
| F5 | Reload page |
| Ctrl+R | Reload page |
| Ctrl+F5 | Hard reload (ignore cache) |
| Escape | Stop loading (if page is loading) |

### Features
| Shortcut | Action |
|----------|--------|
| Ctrl+F | Open Find in Page overlay |
| Ctrl+H | Open History tab |
| Ctrl+J | Open Downloads tab |

### AI Tools (Custom Shortcuts)

Custom shortcuts for AI tools can be configured in Settings. Example shortcuts:

| Shortcut | Action |
|----------|--------|
| (Custom) | Summarize Page |
| (Custom) | Key Points |
| (Custom) | Explain Selection |
| (Custom) | Compare Tabs |

**Note**: AI tool shortcuts auto-open the AI panel if it's closed.

### Find in Page (when find bar is open)
| Shortcut | Action |
|----------|--------|
| Enter | Find next match |
| Shift+Enter | Find previous match |
| Escape | Close find bar and clear highlights |

**Notes:**
- Shortcuts do not interfere with typing in text fields (copy/paste still works normally).
- Ctrl+Shift+T only reopens web tabs (not History/Downloads/Settings tabs).
- Alt+Left/Right only work on web tabs; they do nothing on internal tabs.
- Find in Page (Ctrl+F) only works on web tabs.
- Escape closes find bar first; if find bar is closed, stops page loading.

## Downloads
- What changed: Added automatic download handling with a dedicated Downloads tab (⬇/Ctrl+J) that tracks progress, allows canceling, and opens completed files/folders.
- How to use: Start a download from any page; it saves automatically to your Windows Downloads folder with auto-renaming for duplicates. Click the ⬇ toolbar button or press Ctrl+J to open the Downloads tab. Use the buttons per row: Open (when completed), Show in folder (when file exists), Cancel (while in progress). Right-click for Open, Show in folder, Copy download URL, or Remove from list (does not delete the file).
- Storage: Files go to `%USERPROFILE%\Downloads`, auto-renaming with suffixes like `file (1).pdf` when needed.
- Settings: None required.

**Manual test checklist (Downloads)**
1) Start a download → it saves to `%USERPROFILE%\Downloads` automatically (no prompt) with duplicate names auto-renamed.
2) Downloads tab shows the new item immediately with live progress.
3) Click Cancel while downloading → status switches to Canceled and the transfer stops.
4) After completion, Open launches the file and Show in folder highlights it in Explorer.
5) Ctrl+J (or the ⬇ button) always opens/selects the Downloads tab.
6) While a download is in progress, opening the Downloads tab via ⬇/Ctrl+J keeps the app stable and shows live progress.

**Known limitations**
- Cancel is best-effort and depends on the site honoring cancellation.
- Removing from list does not delete the downloaded file.
- No persistent download history beyond the current session.

## Bookmarks Bar
- What changed: Added a Chrome-style bookmarks bar beneath the toolbar that stays in sync with the star toggle and bookmarks dropdown; shows pill buttons for each bookmark with scrolling and context actions.
- How to use: Click ☆ to add/remove the current page; the bar updates immediately. Left-click a bookmark to open in the current tab. Ctrl+click or middle-click opens it in a new tab. Right-click a bookmark for Open, Open in new tab, Copy link address, Edit…, Remove, Move left, Move right. Right-click empty space to open Bookmark Manager (bookmarks.json) or its folder.
- Persistence: `%LocalAppData%\ConjureBrowser\bookmarks.json` (schema: `{ "Title": string, "Url": string }`). Order in the file matches the bar order; move left/right persists ordering.
- Settings: None beyond the existing toolbar buttons.

**Manual test checklist (Bookmarks Bar)**
1) Start app → bookmarks bar is visible under the toolbar.
2) With no bookmarks, the hint text shows.
3) Click ☆ on a page → bookmark appears immediately in the bar.
4) Left-click a bookmark → navigates the current tab.
5) Ctrl+click or middle-click a bookmark → opens the URL in a new tab.
6) Right-click → Remove → entry disappears and bookmarks.json updates.
7) Move left/right → order changes and stays after restart.
8) Edit… → title/URL update; invalid URL shows an error and does not save.

**Known limitations**
- No drag-and-drop reordering; use the context menu to move left/right.
- Buttons show text only (no favicons).
- Edit dialog validates basic URL format but does not fetch titles.

## Browsing History
- What changed: History tracking now writes to disk and surfaces a dedicated History tab (⏱/Ctrl+H) with search, open-in-current/new-tab actions, context menu, and a clear button; entries stay sorted newest-first and capped at 5,000.
- How to use: Click the History button (⏱) or press Ctrl+H to open the History tab without replacing any browsing tab. Use the search box to filter by title or URL. Double-click or press Enter to open in the current tab; Ctrl+Enter or middle-click opens a new tab. Right-click shows: Open, Open in new tab, Copy link address, Remove. "Clear browsing data" removes all history entries.
- Persistence: `%LocalAppData%\ConjureBrowser\history.json` stores an array of entries `{ "title": string, "url": string, "visitedAt": UTC ISO string }`, newest-first, with the oldest dropped when exceeding 5,000 entries.
- Settings: None required beyond the default toolbar/shortcut entry point.

**Manual test checklist (History)**
1) Visit 3 sites → close/reopen the app → the three entries remain in History sorted newest-first.
2) Press Ctrl+H (or click the ⏱ button) → History tab opens without hijacking the active browsing tab.
3) Type in the search box → list filters by matching title/URL text.
4) Double-click or press Enter on an entry → it opens in the current browsing tab; Ctrl+Enter or middle-click opens it in a new tab.
5) Right-click → Remove from history → entry disappears and stays gone after reopening.
6) Click "Clear browsing data" → list empties and `history.json` is cleared.

**Known limitations**
- No deduplication; repeated visits create separate entries.
- Skips `about:blank` and `chrome-devtools://` URLs.
- Clear action only removes history entries (does not clear cookies/cache/downloads).
- Timestamps display in local time (HH:mm) even though they are stored in UTC.

## Chrome Import
- What changed: Added ability to import bookmarks and browsing history from Google Chrome.
- How to use: Open Settings (via ⋮ menu → Settings) → scroll down to "Import Data" section → click "Import from Chrome...". Select your Chrome profile from the dropdown. Check which data types to import (Bookmarks, History). Click Import.
- Supported data: Bookmarks (from Chrome's JSON file), Browsing History (from Chrome's SQLite database).
- Behavior: Imported data is **merged** with existing Conjure data. Duplicate bookmarks (same URL) are skipped. History entries with duplicate URLs are also skipped.
- Profile detection: Automatically finds Chrome at `%LOCALAPPDATA%\Google\Chrome\User Data\`. Detects all profiles (Default, Profile 1, etc.) and reads custom profile names from Chrome preferences.

**Manual test checklist (Chrome Import)**
1) Open Settings → scroll to "Import Data" → click "Import from Chrome..." → dialog opens with detected profiles.
2) Select a profile → see available data checkboxes (Bookmarks ✓, History ✓).
3) Import bookmarks → they appear in the Conjure bookmarks bar immediately.
4) Import history → entries appear in Conjure's History tab (Ctrl+H).
5) Re-import → duplicates are skipped; import count shows "X imported, Y skipped".
6) If Chrome not installed → dialog shows "Chrome Not Found" message.

**Known limitations**
- Chrome extensions cannot be imported (not supported by CEF/CefSharp).
- Saved passwords cannot be imported (encrypted by Chrome).
- Chrome must not be running when importing history (database is locked); the importer copies the database to a temp file to work around this.
- Only imports `http://` and `https://` URLs.


## Running
From repo root:
```powershell
dotnet restore
dotnet build
dotnet run --project .\src\ConjureBrowser.App
```
Or open `ConjureBrowser.sln` in Visual Studio, set `ConjureBrowser.App` as Startup Project, and press F5. If CefSharp complains about native binaries, set platform to `x64` (Build -> Configuration Manager).

## AI Usage
1) Open the AI panel (toggle `AI` button in toolbar).
2) Choose a model (`gemini-2.5-flash` or `gemini-3.0-pro`).
3) Set a global API key once in the Settings tab (via ⋮ menu). The AI panel will use it automatically.
4) Type in the input box and press Enter or click send.
5) Each tab keeps its own chat history; Enter only sends when the chat input is focused and non-empty.
6) The AI automatically captures screenshots to analyze visual content (images, maps, charts). Ask questions about what you see on the page!
7) Resize the AI panel by dragging its left edge (280px-600px range).
8) Use the Clear Chat button (🗑) below the Send button to clear the conversation.

**Note**: The AI always uses "Auto" context mode, intelligently considering both page content and general knowledge based on your question.

### Quick Actions
One-click AI features in the panel:
- **📄 Summarize**: Get a bullet-point summary of the current page.
- **📌 Key Points**: Extract 5-8 key points from the page content.
- **💡 Explain Selection**: First highlight text on the page, then click this to get an explanation. The selected text appears in the "Selection" preview chip.
- **⚖ Compare Tabs**: Opens a dialog to select 2+ web tabs. The AI generates a comparison table and analysis.
- **🗑 Clear**: Clears the conversation history and stored selection for the current tab.

### Selection Workflow
1. Navigate to a web page.
2. Highlight any text on the page (click and drag).
3. Click "💡 Explain Selection" in the AI panel.
4. The AI will explain the selected text, showing it in the Selection preview.
5. You can ask follow-up questions about the selection.
6. Click ✕ on the Selection chip to clear it.

### Compare Tabs Workflow
1. Open 2 or more web pages in separate tabs (e.g., two product pages, two articles).
2. Click "⚖ Compare Tabs" in the AI panel.
3. In the dialog, check the tabs you want to compare.
4. Optionally edit the comparison prompt.
5. Click "Compare" to generate a comparison table and analysis.

**Manual test checklist (AI Features)**
1. Open a Wikipedia page → click `Summarize` (from 🛠 menu) → summary appears in bullets.
2. Highlight a paragraph → click `Explain Selection` → explanation appears + selection chip shows.
3. Open 2 product pages in 2 tabs → `Compare Tabs` → output includes comparison table.
4. Click `Clear Chat` button (🗑 below Send) → conversation clears, selection chip clears.
5. Switch tabs → selection preview updates to match that tab's state.
6. Drag left edge of AI panel → panel resizes within 280-600px limits.
7. Settings → disable a tool → it disappears from 🛠 menu.
8. Settings → set a custom shortcut (e.g., Ctrl+Shift+S) → pressing it triggers the tool and opens AI panel.

## Notes / Next Ideas
- Tab reordering and favicon display.
- Streaming Gemini responses and richer error messages.
- History tracking errors are caught and logged so they don't crash the app.
