# Lua configuration

Oroterm loads `%LOCALAPPDATA%\Oroterm\oroterm.lua`. With an isolated `OROTERM_DATA_DIR`, it loads `oroterm.lua` in that directory. `OROTERM_CONFIG` can specify another file explicitly. Project directories are not searched for executable configuration.

Use **Edit Lua configuration** in the command palette or Settings to create/open the file. **Reload Lua configuration** reloads manually; main-file and local-module changes reload automatically after a short debounce. **Lua configuration status** shows the path, controlled settings, and errors. The bundled `Configuration/oroterm.example.lua` demonstrates settings and nested startup layouts.

```lua
local oro = require('oroterm')
local config = oro.config_builder()

local function font_size()
  return oro.getenv('OROTERM_LARGE_TEXT') == '1' and 18 or 14
end

config.font_size = font_size()
config.shell = oro.default_shell
config.start_directory = oro.home_dir
config.browser = { home_page = 'http://localhost:3000' }
return config
```

Return exactly one table. Conditions, functions, loops, math, strings, tables, bit32, and coroutines are available. LuaCSharp 0.5.7 implements Lua 5.2 syntax with .NET UTF-16 string semantics; this is not a native Lua 5.4/5.5 runtime. No native Lua DLL is required.

## Settings

| Field | Type | Meaning |
| --- | --- | --- |
| `shell` | string | Default command for new shell panes |
| `start_directory` | string | Existing directory for new shells; Windows environment expansion is supported |
| `font` | string | Terminal font family |
| `font_size` | number | 9–32 |
| `font_weight` | string or integer | Named weight (`Regular`, `Medium`, `Bold`, etc.) or 100–900 |
| `color_schemes` / `color_scheme` | table / string | Custom named palettes and the default palette for sessions without their own theme |
| `cursor` | table | Default shape/blink and renderer blink interval |
| `wallpaper` | table | Images, fitting, opacity and initial selection |
| `events` | table | Persistent `format_tab_title` and `format_status` callbacks |
| `scrollback_limit_bytes` | integer or `'unlimited'` | Per terminal: defaults to 50,000,000 bytes (50 MB), matching Ghostty; `0` disables and clears history |
| `scrollback_limit_lines` | integer or `'unlimited'` | Maximum physical history rows, excluding the active screen; defaults to `'unlimited'` |
| `restore_sessions` | boolean | Legacy compatibility option; does not replay closed sessions |
| `ligatures` | boolean | Terminal font ligatures |
| `desktop_notifications` | boolean | Background terminal notifications |
| `allow_clipboard_read` / `allow_clipboard_write` | boolean | Existing application clipboard policies |
| `keybindings` | table | Existing action names mapped to shortcut strings; `''` disables an action |
| `status_bar` | table | Optional bottom bar; hidden by default, with configurable content, size and colors |
| `browser.home_page` | string | HTTP(S) address, search text, or `about:blank` |
| `browser.profile` | string | `independent`, or a connected profile's unique name or ID |
| `browser.open_terminal_links` | boolean | Open terminal HTTP(S) links in browser panes |

Unknown fields, wrong types, duplicate shortcuts, missing launch directories, unsafe URLs, and oversized layouts reject the whole new config. Browser profile names must resolve to an existing connection; duplicate names are ambiguous, so use the ID or rename a connection. Existing panes keep their shell commands and browser profiles. Reloading changes defaults for new panes and applies visual, input, notification, and clipboard settings to current terminals.

`keybindings.openDirectory` opens the focused pane's current directory in Windows Explorer (default `Ctrl+Shift+O`). For example, `config.keybindings = { openDirectory = 'Ctrl+Alt+O' }` remaps it; an empty string disables the shortcut.

Normal UI settings are stored in `preferences.json`, separately from session layout snapshots. Effective settings are preferences overlaid with the Lua table. Lua-controlled settings are read-only in Settings; removing a field or the Lua file restores its underlying preference. Font zoom on a Lua-controlled size is temporary until reload/restart. Config errors retain the current effective settings during live reload and appear in a native error banner; on a cold start, invalid config uses normal preferences. Oroterm never rewrites the Lua file from the settings dialog.

Scrollback limits apply to existing shells on reload as well as newly created shells. If both limits are set, the first reached takes effect. Ghostty prunes whole memory pages, so retention is approximate. Soft-wrapped rows count separately. Lowering a limit can immediately discard older history; raising it cannot restore discarded output. Memory is allocated as output arrives, rather than reserving 50 MB upfront.

```lua
config.scrollback_limit_bytes = 50000000 -- Ghostty's default, per shell
config.scrollback_limit_lines = 'unlimited'
-- Set both to 'unlimited' to remove both limits; use 0 bytes to disable history.
```

## Optional bottom status bar

The bottom bar is hidden by default and reserves no space. Add this to your config to enable it:

```lua
config.status_bar = {
  enabled = true,
  left = { '{session}', '{directory}', '{branch}' },
  right = { '{panes} panes', '{font_size}px' },
  separator = '  ·  ',
  height = 24,
  font_size = 11,
  -- Optional overrides; omit or use '' to follow the active session theme.
  -- background = '#131519',
  -- foreground = '#D7DBE2',
}
```

`left` and `right` are ordered arrays of up to 16 single-line text segments (256 characters each). A segment can mix literal text and placeholders. A segment with an unavailable or empty field is omitted, including its label; separators appear only between remaining segments. Use `{}` for an empty side. Text truncates to fit each half of a narrow window.

| Placeholder | Value |
| --- | --- |
| `{session}` / `{tab}` | Selected session and tab names |
| `{directory}` | Focused terminal's current directory, shortened with `~` for home |
| `{branch}` | Selected session's tracked Git branch; omitted for a browser pane |
| `{status}` | Focused shell's command/progress summary, or its exit message |
| `{exit_code}` | Focused shell's exit code, available after it exits |
| `{shells}` | Running shell count across all sessions |
| `{panes}` / `{tabs}` / `{sessions}` | Pane count in the current tab, tab count in the current session, total session count |
| `{font_size}` / `{viewport}` | Terminal font size and focused terminal viewport dimensions |
| `{url}` | Focused browser pane's URL; omitted for a terminal pane |
| `{version}` | Oroterm version |

Defaults when enabled are `left = { '{status}', '{directory}', '{branch}' }`, `right = { '{shells} shells', '{viewport}' }`, `separator = '  ·  '`, `height = 20`, and `font_size = 10`. Height is 18–64 logical pixels, font size is 9–24, and height must be at least font size + 6. Colors use `#RRGGBB`. Unknown placeholders, malformed braces and invalid values reject the new config while retaining the last working settings.

Changes apply on live reload without restarting shells. Set `enabled = false` or remove `status_bar` to restore the hidden default (unless an underlying preference explicitly enables it). Lua owns the complete `StatusBar` settings object while the table is present. Templates format current metadata; optional runtime callbacks below can override either side. Routine action messages temporarily replace the left text for five seconds when enabled. Errors and limit warnings appear in a dismissible banner even when the bar is hidden.

## Palettes, fonts and cursors

`Configuration/appearance.example.lua` is a complete example with a Mocha-style palette, the Windows ANSI colors from the WezTerm example, indexed colors, status/tab callbacks and optional wallpaper reuse.

```lua
config.font_weight = 'Medium'
config.cursor = { style = 'bar', blink = true, blink_interval = 650 }
config.color_schemes = {
  Personal = {
    foreground = '#CDD6F4', background = '#1F1F28', accent = '#74C7EC',
    cursor = '#F5E0DC', cursor_foreground = '#11111B',
    selection_background = '#585B70', selection_foreground = '#CDD6F4',
    indexed = { [16] = '#FAB387', [17] = '#F5E0DC' },
  },
}
config.color_scheme = 'Personal'
```

Custom palettes support `foreground`, `background`, `accent`, `cursor`, `cursor_foreground`, `selection_background`, `selection_foreground`, `ansi`, `brights`, and `indexed`. Colors use `#RRGGBB`. Supply `ansi` and `brights` together, each with eight colors, or omit both for Ghostty's default ANSI palette. `indexed` overrides indices 16–255. Up to 32 custom schemes are allowed; names are case-insensitive and cannot replace bundled names or `default`. The session-theme gallery includes configured schemes. Startup sessions can reference them by name. Existing sessions using a configured name receive its revised colors on reload; removing a definition preserves an already selected session's colors.

`color_scheme` establishes the global default for sessions whose theme is unset. An explicit session theme takes precedence. Application truecolor and OSC overrides remain effective. `cursor_foreground` affects text under block cursors; selection colors affect selected cells. Removing a global palette override restores underlying preferences.

Font sizes are logical pixels, not WezTerm points. Named weights are `Thin`, `ExtraLight`, `Light`, `Regular`/`Normal`, `Medium`, `SemiBold`, `Bold`, `ExtraBold`, and `Black`; integer weights are 100–900. System fonts use DirectWrite's matching. Bundled fonts provide real Regular, Medium and Bold faces with italic counterparts; other requested weights use the closest bundled group. ANSI bold still selects a bold face. Changing weight recomputes cell geometry and text caches.

Cursor styles are `block`, `bar`, `underline`, and `hollow`. `blink` configures Ghostty's default, and `blink_interval` is the visible/hidden half-cycle in milliseconds (100–2000). Applications retain precedence when they explicitly set a cursor style or blink mode; DECSCUSR reset restores configured defaults. Cursor/indexed-color settings require a 0.7.17+ session host; an older live host shows a notice and remains connected. Activate these host features by deliberately quitting all sessions and reopening when convenient. Font weight, formatting and wallpapers work independently of that upgrade.

## Runtime presentation callbacks

```lua
config.events = {
  format_tab_title = function(tab)
    return {{ text = tab.name, foreground = '#74C7EC', bold = tab.is_active }}
  end,
  format_status = function(ctx)
    local battery = ctx.battery_percent and ('  ' .. ctx.battery_percent .. '%') or ''
    return { left = ctx.session .. ' · ' .. ctx.tab,
             right = oro.strftime('%a %H:%M:%S') .. battery }
  end,
}
```

`format_tab_title` receives `id`, `name`, `session`, `is_active`, `panes`, `attention_count`, `has_attention`, `title`, `directory`, `shell`, `url`, and `has_exited`. `title` is the terminal's reported title; `shell` is its configured command, not foreground-process discovery. Attention counts track attention signals, not all unseen output. The callback only changes presentation; it never renames or replaces the tab.

`format_status` receives the status-template fields listed above, with counts/font size numeric and unavailable `exit_code` nil, plus `pane_id`, `session_id`, `tab_id`, `time` (`HH:mm:ss`), and `date` (`yyyy-MM-dd`). `battery_percent` and `battery_charging` are present only when Windows reports a battery. The status bar remains hidden unless enabled.

Tab callbacks return a string, an array of text segments, or nil for the normal title. Status callbacks return `{ left = ..., right = ... }` or nil. Each side accepts a string or segment array; an omitted/nil side uses its normal template, while `{}` explicitly clears it. Segments can be strings or `{ text = '...', foreground = '#RRGGBB', bold = true, italic = false }`. Each output supports up to 16 single-line segments of 1,024 characters. Formatting uses the terminal font, including bundled Nerd Font glyphs. Segment background colors and arbitrary WinUI markup are not supported.

Callbacks retain their Lua local state until a successful reload or window close. A window-owned, 256 MiB worker evaluates them asynchronously with cached metadata, at roughly one-second intervals and after navigation changes. One batch runs at a time; queued updates coalesce. Hidden/minimized windows do not request updates. A batch has a 500 ms deadline. Invalid output, exceptions, memory exhaustion or timeouts disable runtime formatting, restore native labels/templates and show a Lua error banner without ending shells. Invalid config reload retains the previous worker and settings. No shell execution, CLR access or arbitrary window mutation is exposed.

## Wallpapers

```lua
config.wallpaper = {
  directory = oro.path_join(oro.home_dir, '.config/wezterm/backdrops'),
  -- Or images = { 'backdrops/one.jpg', 'backdrops/two.png' }, relative to this config.
  opacity = 0.04, -- The visible image contribution, like a 96% opaque color overlay.
  fit = 'cover',  -- cover, contain, or stretch
  random = true,
  enabled = true,
}
config.keybindings = { nextWallpaper = 'Alt+F8', toggleWallpaper = 'Alt+B' }
```

Directory scanning includes JPG/JPEG/PNG/BMP files and is not recursive. `images` and `directory` can be combined, with at most 64 distinct images. Relative paths resolve beside the Lua file; `%ENVIRONMENT_VARIABLES%` are expanded. Directory contents are rescanned when config reloads. Missing configured files/directories reject the new config; malformed or oversized image data reports a dismissible loading error.

The command palette provides **Next wallpaper**, **Previous wallpaper**, **Random wallpaper**, **Choose wallpaper**, and **Toggle wallpaper focus mode**. Matching keybinding actions are `nextWallpaper`, `previousWallpaper`, `randomWallpaper`, `chooseWallpaper`, and `toggleWallpaper`, with no default shortcuts. Focus mode temporarily shows the normal terminal background. Selection and focus mode are window-local and reset on reopening; changing tabs/sessions keeps the current image.

Images render behind default terminal cells, fitted independently within each terminal pane. Explicit application backgrounds, selections and terminal image protocols retain their normal layering; browser documents are unaffected. One decoded image is shared across the window, with at most two cached GPU-device copies. Files are limited to 32 MiB and decoded images to 16 megapixels. Rapid cycling coalesces before decoding, device replacement recreates textures, and changing/removing a wallpaper releases its resources. Wallpapers are static; this is separate from terminal image-protocol animation.

## Action bindings and modal keys (0.7.19)

The existing `keybindings = { actionName = 'shortcut' }` form remains supported. `bindings`, `leader`, `key_tables`, and `actions` add aliases, literal input, sequences, modal keys and Lua callbacks. See the complete optional `Configuration/interactions.example.lua`.

```lua
config.keybindings = { switchSession = 'Ctrl+Shift+K', randomWallpaper = 'Alt+/' }
config.leader = { key = 'Ctrl+Alt+Space', timeout_ms = 1500 }
config.bindings = {
  { key = 'Shift+Enter', action = { send_text = '\27\r' } },
  { key = 'Alt+N', action = { sequence = { 'newTab', { paste = 'Get-Location' } } } },
  { key = 'LEADER+R', action = { activate_key_table = 'resize' } },
  { key = 'Alt+S', action = { lua = 'scratch' } },
}
config.key_tables = {
  resize = { timeout_ms = 3000, one_shot = false, bindings = {
    { key = 'H', action = 'resizeLeft' }, { key = 'L', action = 'resizeRight' },
    { key = 'J', action = 'resizeDown' }, { key = 'K', action = 'resizeUp' },
    { key = 'Enter', action = { pop_key_table = true } },
  } },
}
config.actions = {
  scratch = function(ctx)
    return { sequence = { 'newTab', { paste = '# Scratch in ' .. ctx.session } } }
  end,
}
```

An action is a built-in action-name string or a table with exactly one operation: `send_text`, `paste`, `sequence`, `lua`, `activate_key_table`, or `pop_key_table = true`. `send_text` sends literal UTF-8, including controls, without bracketed-paste framing or adding Enter. It needs a 0.7.19+ host; older hosts remain connected and report the upgrade requirement when used. `paste` uses Ghostty's paste handling. Text admission is bounded to 64 KiB. Input acknowledgment means host admission, not shell-command completion. Sequences run in order, stop on error, and can target newly created tabs before visual focus settles.

Lua callbacks receive status fields plus `session_id`, `tab_id`, `pane_id`, `key_table` and boolean `leader_active`. Return an action or nil. They retain Lua locals and use the same isolated worker, 500 ms deadline and memory limits as presentation hooks. Returned actions go through the UI dispatcher; callbacks have no direct CLR, OS execution or arbitrary window APIs. One run allows 128 operations/eight nested levels; the queue holds at most 64 runs.

Leader mode consumes the next non-modifier chord, including unmatched chords. Key tables consume unmatched keys; Escape pops the active table. Matches refresh the timeout; `one_shot = true` pops after one match. Timeouts are 100–30,000 ms, checked without per-mode timers. Window deactivation and successful reload clear modes. IME composition and AltGr retain precedence. Status templates accept `{key_table}` and `{leader}`; Lua formatting receives `key_table` and `leader_active`.

Limits: 256 rich bindings, 32 key tables, 256 bindings per table. Duplicate rich chords within a scope reject the config. Rich bindings override legacy/default shortcuts. `LEADER+` needs a leader and cannot appear inside a key table.

Punctuation accepts comma, period, slash, semicolon, equals, minus, brackets, backslash, quote and backtick, either literally or as `Comma`, `Period`, `Slash`, `Semicolon`, `Equals`, `Minus`, `LeftBracket`, `RightBracket`, `Backslash`, `Quote`, `Backtick`, `Plus`. These identify Windows OEM key positions; printed symbols can differ on non-US layouts. Shifted US symbols such as `?`, `<`, `>` and `~` imply Shift. `Ctrl++` means `Ctrl+Shift+Equals`. Explicit base-key aliases and modifiers identify physical chords. AltGr input bypasses shortcuts.

New built-ins: `switchSession` (default `Ctrl+Shift+K`), `toggleFullscreen` (default `F11`), `toggleMaximized`, `toggleAlwaysOnTop`, `increaseFontSize`, `decreaseFontSize`, `resetFontSize`, and `resizeLeft/Right/Up/Down`. The native session switcher matches names, directories and branches by case-insensitive subsequence, prioritizes name/exact matches and supports arrows/Enter/Escape. Pane switching is separate.

## Appearance and window controls (0.7.19)

```lua
config.appearance = {
  padding = { left = 15, right = 15, top = 10, bottom = 8 },
  line_height = 1.5, cell_width = 1.0, inactive_pane_opacity = 0.75,
  font_features = { liga = 1, calt = 1, ss01 = 1 },
}
config.window = {
  width = 1280, height = 820, state = 'normal', always_on_top = false,
  backdrop = 'acrylic', background_opacity = 0.85,
}
```

Padding is logical pixels, 0–128 per side (defaults 8/8/5/5). `line_height` multiplies font size (1–3, default 1.5); `cell_width` multiplies measured glyph width (0.5–2, default 1). Physical cell dimensions remain integral, and selection/IME/accessibility follow the accepted grid. `inactive_pane_opacity` dims unfocused terminal content including images (0.1–1, default 1). `font_features` accepts up to 32 four-character OpenType tags with integer values 0–65,535, overriding corresponding ligature defaults; visible effects depend on the font.

Window size is logical pixels: width 400–16,384, height 300–16,384, clamped to the display work area. State is `normal`, `maximized` or `fullscreen`. Geometry applies initially and when its configured values change, preserving manual sizing during unrelated reloads. `always_on_top` applies to the overlapped presenter. Backdrops: `none`, `mica`, `mica_alt`, `acrylic`. `background_opacity` (0–1) controls default-background tint over the selected Windows material; glyphs and explicit application backgrounds retain opacity. `none` stays solid. Windows controls material fallback when transparency effects are unavailable. Wallpaper image opacity remains independent; browser documents retain their own rendering.

Both settings objects support preference ownership and live reload. Window actions are temporary. Font fallback remains automatic; custom fallback lists, animated wallpapers and multiple wallpaper layers are not implemented.

## Helpers and modules

`require('oroterm')` exposes:

| Helper | Result |
| --- | --- |
| `config_builder()` | Empty configuration table |
| `home_dir` | Current user's home directory |
| `config_dir` | Directory containing the config file |
| `data_dir` | Oroterm's state directory |
| `default_shell` | Detected PowerShell 7 command, with the existing fallback |
| `version` | Oroterm version string |
| `getenv(name)` / `os.getenv(name)` | Environment variable or nil |
| `file_exists(path)` | Whether a file exists |
| `directory_exists(path)` | Whether a directory exists |
| `strftime(format)` | Current local time; supports `%a`, `%A`, `%b`, `%B`, `%d`, `%m`, `%Y`, `%y`, `%H`, `%I`, `%M`, `%S`, `%p`, `%z`, and `%%` |
| `path_join(first, second)` | Combine two path components |

`require('shared')` loads `shared.lua` beside the config. `require('themes.dark')` loads `themes/dark.lua`. Modules return normal Lua tables/functions. Module names cannot escape the config directory; symlink/junction modules are rejected. Standard `io`, process execution, `os.exit`, `dofile`, `loadfile`, and CLR access are not exposed.

Evaluation runs in an isolated `Oroterm.SessionHost.exe` process, separate from the host owning persistent shells. One-shot checks use `--evaluate-config`; windows use `--runtime-config` and retain that worker only when callbacks are configured. A Windows job limits each worker to 256 MiB and ends it when its owner closes. Initial evaluation has a four-second deadline; runtime batches have a 500 ms deadline. Main source plus modules are limited to 256 KiB and 32 files. Config results and helper output are bounded. Infinite loops and excessive allocations cannot keep the window blocked or terminate the persistent shell host.

## Startup sessions, tabs, and panes

```lua
local oro = require('oroterm')
return {
  startup = {
    mode = 'when_empty',
    sessions = {
      {
        name = 'Development',
        theme = 'Cobalt2',
        tabs = {
          {
            name = 'Shell and browser',
            root = {
              split = 'right', ratio = 0.6,
              first = { shell = oro.default_shell, cwd = oro.home_dir },
              second = { kind = 'browser', url = 'http://localhost:3000' },
            },
          },
          { name = 'Logs', shell = oro.default_shell },
        },
      },
    },
  },
}
```

`startup.sessions` is a contiguous array. Every session has a name and at least one tab. A tab can specify inline leaf fields (`shell`, `cwd`, or `kind='browser'`, `url`, `profile`) or a `root` split tree. Split nodes use `split='right'` or `split='down'`, `first`, `second`, and an optional ratio from 0.1–0.9. Both children can themselves be splits. Leaves inherit the effective default shell, directory, and browser settings.

Each session may specify `theme = 'preset name'`. Bundled choices are **Adventure Time**, **Alien Blood**, **Cobalt2**, **Dracula**, **Fun Forrest**, **Hot Dog Stand**, **Hot Dog Stand (Mustard)**, **Solarized Light**, **Catppuccin Latte**, and **Sakura**. Names are case-insensitive; `theme = 'default'` uses Oroterm's normal appearance. Unknown names reject the new config. These Gogh palettes color both the session interface and its terminal panes, including default foreground/background, cursor, selection and all 16 ANSI colors. Application truecolor, extended indexed colors and OSC overrides remain effective. New tabs/splits inherit the owning session's theme; font/settings reloads preserve it.

Theme declarations belong to startup sessions: live config reload does not rerun startup creation or replace existing sessions' choices. Change a live session using **Session theme…** in its context menu or the command palette. The gallery has preset tiles and a **Use default** action. There is no color editor. Choices are stored per session ID in `session-themes.json`, separately from host layouts, so older hosts cannot drop them. Old IDs are pruned on save and never recreate sessions after a reboot.

Pane colors require a host from 0.7.14 or newer. An older host remains connected and shows an upgrade notice. To activate pane colors, use **Quit Oroterm and stop all sessions**, then relaunch; closing only the window deliberately keeps the previous host alive.

Cold startup runs `startup.sessions` when configured; otherwise it opens one default shell. Previous sessions, tabs and panes on disk are never replayed after a reboot or host exit. `when_empty` and the legacy `replace_saved` spelling both create the configured layout on cold startup and preserve live sessions on reconnect. Reloading config never reruns startup creation. **Create configured startup sessions** in the command palette explicitly adds fresh instances alongside current sessions. Config files can compute these arrays with normal Lua logic; runtime hooks format presentation and do not create sessions.

Limits match the UI: 64 sessions, 64 tabs per session, 16 panes per tab, 256 panes overall, and split depth 12. Browser leaves never allocate a PTY. Closing all sessions does not automatically replay startup actions in that window.

## Verification

Run `./scripts/test-lua.ps1` to build and test parsing, helpers/modules, invalid configuration, preferences, timeout/memory limits, and real WinUI startup/reload/reconnect. `./scripts/test.ps1 -SkipNative -Ui -Sessions -Browser -Lua` includes Lua in the full regression suite. Tests use isolated data directories and do not execute the normal user's config.
