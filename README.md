# toiqtty

![toiqtty logo](src/Toiqtty/Assets/Brand/toiqtty-logo-v1.png)

Launch `toiqtty.exe` or use the `toiq` CLI. Settings live in `%LOCALAPPDATA%\toiqtty`; the Lua entry point is `toiqtty.lua`, using `require('toiqtty')`.

A native Windows terminal built with **libghostty-vt**, **WinUI 3**, **Direct3D 12/DirectWrite**, and **ConPTY**. Inspired by cmux’s compact session sidebar, split panes, and attention indicators. Optional **WebView2** panes provide an embedded browser. Terminal composition uses a dedicated native GPU renderer. HarfBuzz shapes eligible ASCII runs; DirectWrite preserves Windows font fallback, Unicode, color and italic text. Win2D supplies font configuration, measurement and reference fixtures.

This is a working first version, not a production-complete terminal. It starts real local shells and maintains independent terminal sessions across workspace switches.

## Run

Requires Windows 11 x64 or Windows 10 build 19041+, PowerShell 7, .NET 10 SDK, Visual Studio 2022 C++ build tools, a Windows SDK including FXC, Git, and internet access for the first build. MSBuild invokes its helpers with `pwsh`, matching the CI shell. The scripts download a checksum-verified Zig 0.16.0 compiler inside `.tools/`, fetch a pinned Ghostty commit, and fetch checksum-pinned HarfBuzz source into `.deps/`. The WinUI build builds the native Direct3D renderer and HarfBuzz core and precompiles its shaders with the installed Windows SDK. HarfBuzz uses its DirectWrite adapter without external FreeType, GLib or Graphite libraries.

```powershell
pwsh -ExecutionPolicy Bypass -File scripts/run.ps1
```

The first native build takes longer; subsequent builds reuse Zig’s cache. You can also open `Toiqtty.slnx` in Visual Studio with WinUI development support. Run `scripts/build-native.ps1` first.

To build without launching:

```powershell
./scripts/build-native.ps1
dotnet build src/Toiqtty/Toiqtty.csproj
```

## What works

- Real PowerShell, Command Prompt, PowerShell 7 when installed, and WSL shell profiles.
- PowerShell 7 (`pwsh`) is the default when installed; Windows PowerShell is the fallback. Older settings and restored panes using the original default migrate once. Custom shell commands are preserved.
- Persistent sessions in the left sidebar, with directories, Git branches, and attention indicators. Each session contains tabs; each tab has its own horizontal/vertical split tree and independent shell panes.
- Per-session themes: right-click a session and choose **Session theme…**, or use the command palette. Pick one of ten bundled Gogh presets from a visual gallery. Themes color the session card, title/tab/status bars, browser controls, focus markers, and terminal panes: default foreground/background, cursor, selection and all 16 ANSI colors. Ghostty applies terminal colors without changing shell processes, text or history; application truecolor and OSC overrides remain effective. **Use default** restores normal colors. An attached host must support pane themes to apply terminal colors. Closing only the window does not upgrade the host.
- Drag sessions and tabs to reorder them. Move a terminal or browser pane between tabs and splits in its owning session while keeping its running shell or WebView alive.
- Automatic PowerShell command start/end tracking, exit codes, duration and live directory updates, preserving profiles, Starship and PSReadLine bindings. Original MIT-licensed Bash/Zsh adapters track command boundaries, exit status and cwd for recognized interactive native/WSL launches, preserving prompts and startup files.
- Native Windows notifications for background terminals, with workspace/pane activation and a settings toggle. Notification bursts are limited; Activity retains the bounded history.
- Nested horizontal and vertical splits, draggable dividers, directional focus, and pane zoom.
- GPU-backed terminal drawing with colors, bold/italic text, underline, strikeout, Unicode graphemes, and wide characters.
- Bundled JetBrainsMono Nerd Font Mono, including Regular, Medium and Bold faces with italic counterparts and Starship icons. Lua selects font weight without installing fonts. Existing settings using the original Cascadia Mono default migrate once; other custom font choices are preserved.
- Optional bottom status bar, hidden by default. Lua configures its left/right segments, separator, height, font size and colors; colors follow the active session theme unless overridden. See [Lua configuration](LUA_CONFIG.md#optional-bottom-status-bar).
- Custom global/session palettes, extended indexed colors, selection/cursor text colors and upstream cursor defaults. Persistent Lua callbacks format tab titles and status text in a bounded worker. Optional shared wallpapers provide cycling, random selection, a native chooser and focus mode. See [the complete appearance example](src/Toiqtty.Core/Configuration/appearance.example.lua).
- Filled Powerline separators use vector shapes on a grid aligned to physical pixels, preventing seams between colored prompt segments at fractional display scaling.
- Ghostty parsing, alternate screens, reflow, scrollback, keyboard encoding, mouse reporting, focus reports, and bracketed paste.
- Kitty graphics: PNG/RGB/RGBA, direct/file/temp transfers, chunked and zlib transmission, source cropping, scaling, three z-layers, scrollback anchors, deletion/replacement, Unicode placeholders, and animation. Parsing and image state stay in Ghostty; Windows WIC decodes PNG and native Direct3D draws the placements.
- Synchronized output (mode 2026), colored underline variants and overline, cursor shape/blink/color, terminal identity, light/dark queries, and pixel/grid/in-band size reports.
- OSC 8 links open with Ctrl+click (`http`, `https`, and `mailto`). Keyboard encoding carries press, repeat, and release actions plus Windows layout-aware text.
- Word/line multi-click selection, Alt rectangular selection, drag autoscroll, file/text drops, and native Ghostty copy formatting, including wrapped lines and wide characters. Right-click pastes; hold Shift to select in mouse-reporting applications.
- A native scrollbar for each terminal pane, plus browser-style middle-click autoscroll. Hold middle and move up/down to scroll; release to stop. A middle click without dragging starts autoscroll until the next click, non-modifier key, or leaving the pane. Moving farther from the anchor scrolls faster. Mouse-reporting applications receive the middle button normally; hold Shift to start autoscroll instead. Autoscroll sends wheel reports to mouse-reporting apps and scrolls terminal history otherwise. Shift+wheel always scrolls terminal history.
- DirectWrite shaped text runs with configurable ligatures and system font fallback; changed-row text/background layers avoid full-grid redraws.
- OSC 52 and Kitty 5522 clipboard protocols with MIME data, configurable read/write policy and one-time user paste grants.
- Kitty drop-target Copy/Move negotiation with text/HTML/RTF/file MIME data; native completion waits for the terminal client's conclusion.
- Semantic command, progress, program status and notification content with bounded activity history (Ctrl+Shift+H).
- UI Automation viewport text, caret, selection, search and physical range geometry. IME preedit is shown at the terminal caret.
- Bounded input queues and single-instance activation per settings directory.
- Search across terminal history with match navigation and selection highlighting.
- Command palette, workspace renaming, configurable shell/font/directory, and font zoom with Ctrl+wheel.
- Closing the window keeps shells, running commands, scrollback and graphics alive in `toiqtty.SessionHost.exe`. Reopening attaches to the same processes. The host exits after the last live shell ends. After a reboot or host exit, startup uses your Lua startup layout or one default shell; old sessions and panes are not replayed.
- WebView2 browser tabs and nested splits alongside shells, with an address/search bar, back/forward, reload, developer tools, external opening, and popup-to-tab navigation. WebViews remain alive across tab/session switches. URLs, selected browser profiles, and browser-owned cookies/storage persist across window restarts; pages reload when the window reopens.
- A companion extension for Chromium and Firefox browser families shares selected-site cookies into isolated WebView2 profiles using the browser's cookie API. Each browser profile pairs separately. Sharing is one-way, including source updates and deletions, while both apps are open.
- Lua configuration with logic, local modules, environment helpers, validated settings, automatic reload, and startup session/tab/split layouts. Lua runs in a bounded helper process; reload and window reconnect preserve live shells.

Closing a pane ends its shell. Closing a tab ends that tab's split shells; closing a sidebar session ends all its tabs. Use **Quit toiqtty and stop all sessions** in the command palette for a full shutdown. When the last session closes, the window shows a New session action without starting another shell automatically. Tabs can be renamed through their context menu.

Shortcuts can be remapped or disabled in Settings.

## Keyboard shortcuts

| Action | Shortcut |
| --- | --- |
| New session | Ctrl+Shift+N |
| New tab | Ctrl+Shift+T |
| New browser tab | Ctrl+Shift+U |
| Browser address bar | Ctrl+L (browser pane focused) |
| Next / previous tab | Ctrl+Tab / Ctrl+Shift+Tab |
| Split right / down | Ctrl+Shift+D / Ctrl+Shift+E |
| Close pane | Ctrl+Shift+W |
| Move pane | Ctrl+Shift+M |
| Open current folder in Windows Explorer | Ctrl+Shift+O |
| Zoom pane | Ctrl+Shift+Enter |
| Focus adjacent pane | Ctrl+Alt+Arrow |
| Next / previous session | Ctrl+Shift+PageDown / Ctrl+Shift+PageUp |
| Session 1–9 | Ctrl+1–9 |
| Command palette | Ctrl+Shift+P |
| Find in terminal | Ctrl+Shift+F |
| Copy / paste | Ctrl+Shift+C / Ctrl+Shift+V |
| Copy selection or interrupt shell | Ctrl+C |
| Scroll a page | Shift+PageUp / Shift+PageDown |
| Show/hide sidebar | Ctrl+Shift+B |

The folder button beside the tab controls, **Ctrl+Shift+O**, and the command palette open the focused pane's live working directory in Windows Explorer. Session and tab context menus offer **Open in Windows Explorer** for their focused pane without switching to them. For browser panes, toiqtty uses a shell in the same tab/session, or the configured starting directory for a browser-only session. WSL directories use their Windows mapping when available; unmapped remote directories report that they are unavailable instead of opening an unrelated local folder. Remap the `openDirectory` action in Settings or Lua.

Double-click a session to rename it; its current name is selected for replacement. Right-click a session to open its folder, rename or close it. Closing a shell pane terminates its shell. Closing the window saves the layout and leaves live shells running.

Drag a session in the sidebar, or drag a tab by its label, to reorder it. Context menus also offer **Move session up/down** and **Move tab left/right**. To relocate a pane, focus it and choose **⋯ → Move focused pane…** or use the command palette. A tab's context menu offers the same action for that tab's focused pane. Choose an existing tab in the same session, or create a new tab there. Existing tabs receive a split to the right of or below their focused pane. The shell PID, command state, history, and graphics remain alive; empty source tabs disappear. Panes always retain their owning session. Session and tab order persist across reopening.

Hover over a pane to reveal its grab handle in the top-right corner. Drag the handle onto another pane's left, right, top or bottom edge; the highlighted half shows where the split will go. Dropping on a tab splits beside its focused pane; the tab **+** button creates a new tab in the same session. Sidebar sessions and the new-session button are not pane destinations. Escape cancels without changing the layout. The move dialog is also available with **Ctrl+Shift+M**, or Enter while the handle has keyboard focus. Both terminal and browser panes support relocation within their session.

## Interface

Terminal rendering uses a native Direct3D 12 pipeline. Glyphs, backgrounds, decorations, images and Powerline shapes are batched; font fallback and color/italic text use DirectWrite and Direct2D through 11-on-12. Opaque panes present through a composition swapchain, while translucent panes use a premultiplied composition surface. GPU resources and caches have bounded device, pane, font and submission lifetimes. See [ARCHITECTURE.md](ARCHITECTURE.md) for ownership, limits and scheduling.

Visible panes capture on output notifications and keep one unread frame as backpressure. Changed-row transfers, reusable read connections and unchanged-status omission reduce IPC work. Hidden panes stop rendering while their host-owned shells continue running. Cached views and complete tab layouts survive navigation within bounded retention limits. Selection, input, IME and accessibility use the geometry of the accepted frame.

`scripts/test-performance.ps1` measures input admission through response-frame Present, CPU, allocation and output throughput with one, four and eight panes; `-Scale16` adds sixteen. Quiet, controlled, saturated and mixed busy/idle workloads are recorded separately. `scripts/compare-performance.ps1` compares matched runs and rejects mismatched benchmark versions. Present return measures submission, not physical display scanout. Visible comparisons reject minimized windows; offscreen renderer checks establish correctness rather than onscreen FPS.

`scripts/test-performance-regressions.ps1` checks retained panes, delta recovery, background suspension, navigation, resize, scrolling and input lifecycle against the selected current build. `scripts/test-endurance.ps1` exercises an isolated release copy for the requested duration; frontend/host debugger modes can be selected independently. Reports and build output stay under ignored `artifacts/`.

## Lua configuration

For executable settings and startup layouts, see [Lua configuration](LUA_CONFIG.md). Open **Edit Lua configuration** in the command palette. The default path is `%LOCALAPPDATA%\toiqtty\toiqtty.lua`; an example ships beside the app at `Configuration/toiqtty.example.lua`.

## Embedded browser and shared cookies

Use **Browser → New browser tab**, **Split browser right**, or **Split browser down**. Microsoft Edge WebView2 Evergreen Runtime must be installed; the rest of toiqtty still works if it is missing. [Install WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/).

Open **Browser → Profiles and cookie sharing** to connect a browser profile and choose the default for new browser panes. Use **Open companion extension folder** to locate the bundled `BrowserCompanion` directory beside `toiqtty.exe`:

1. Edge, Chrome, Brave, Vivaldi, and compatible Chromium browsers: enable Developer mode on the browser's Extensions page, then **Load unpacked** from `BrowserCompanion/chromium`.
2. Firefox and compatible browsers: use **Load Temporary Add-on** at `about:debugging#/runtime/this-firefox`, selecting `BrowserCompanion/firefox/manifest.json`. The development extension must be reloaded after restarting Firefox. A permanent standard Firefox installation requires a Mozilla-signed package, which is not included.
3. In toiqtty choose **Connect another browser profile**, copy the pairing code into the extension, and name the browser/profile. Pairing is single use and expires after ten minutes.
4. Select the connection in toiqtty, enter origins such as `https://github.com` or `http://localhost:3000`, and **Save site selection**. Reopen the extension popup and choose **Approve sites and sync now**. Browser permissions are requested only for those sites.
5. Choose that connection as the default, **Save defaults**, and create a browser pane. Existing panes keep their original profile; use a pane's **⋯ → Change browser profile** to switch and reload it. The extension syncs about once per minute and after cookie changes; its popup lets you select a Firefox container separately from the default cookie store.

Cookie sharing does not transfer passwords, history, localStorage, IndexedDB, partitioned cookies, or device-bound session keys. Some sites require signing in inside toiqtty. WebView2 uses its own data directory; it never opens or modifies a regular browser's profile database. Disconnecting revokes the source token and stops future sharing. Previously imported cookies remain until cleared from the browser pane menu. Removing an origin stops future sharing for that origin; it does not sign you out automatically. The companion README documents installation and behavior in more detail.

Cookies and extension site permissions apply to the host across ports. Selecting `http://localhost:3000` does not isolate cookies from other localhost ports.

To display a PNG from PowerShell inside toiqtty:

```powershell
./scripts/show-image.ps1 -Path 'C:\path\image.png' -Columns 40
```

## Verify

CI builds, tests, and validates packaging for toiqtty on Windows x64. Generated packages stay on the temporary runner; CI does not upload build artifacts. A separate Ubuntu job checks the Bash/Zsh scripts used by WSL in real PTYs; it does not build a Linux terminal. Full WSL-through-ConPTY interoperability is covered by the optional Windows interop checks below.

```powershell
./scripts/test.ps1                 # Native engine + real ConPTY integration checks
./scripts/test.ps1 -SkipNative -Ui -Notifications # Also deliver and remove one real Windows toast
./scripts/test.ps1 -SkipNative -Ui  # Also exercise the actual WinUI layout in an isolated test session
./scripts/test.ps1 -SkipNative -Published # Validate the portable release after publishing
./scripts/test.ps1 -SkipNative -Ui -Sessions -Browser # Include persistent-shell and real WebView2 regression checks
./scripts/test-browser.ps1         # Browser models, loopback bridge, companion JS, and real WebView2 checks (Node.js required)
./scripts/test-interop.ps1 -WslDistro archlinux # Optional real Kitty/tmux/WSL client matrix
./scripts/test-soak.ps1 -Seconds 600 # Four real shells plus fragmented image replacement and memory sampling
./scripts/test.ps1 -SkipNative -Ui -UiSoakSeconds 120 # Continuous GPU/text cache pressure in an isolated window
./scripts/test-dpi.ps1            # Real mixed-monitor moves, pixel checks and external UIA geometry
./scripts/test-navigation.ps1     # Reordering, live pane moves, and persistence across window closure
./scripts/test-performance.ps1 -Scale16 # Event-based latency, output and mixed busy/idle measurements
./scripts/test-performance-regressions.ps1 # Cached views, row deltas, background and input lifecycle
./scripts/test-endurance.ps1 -Seconds 10800 # Three-hour sessionized stress with crash monitoring
./scripts/test-endurance.ps1 -Seconds 10800 -FrontendDebuggerMode None -HostDebuggerMode None # Same stress without attached debuggers
./scripts/test-endurance.ps1 -Seconds 60 -PauseCheck # Verify external minimization pauses active stress time
dotnet run --project tests/Toiqtty.Smoke -c Release -- --snapshot-fuzz 60 17 # Seeded incremental/full-frame differential check
./scripts/test-startup.ps1        # Timed fresh-host and reattach launches (published release)
./scripts/test-startup-close.ps1  # Window closes and owns evaluator teardown during stalled Lua startup
./scripts/test-directx.ps1 -DebugLayer -Offscreen # Native renderer correctness and debug-layer checks
./scripts/test-native-lifetimes.ps1 # CPU arena ownership; add -AddressSanitizer when supported
./scripts/test-native-gpu-lifetimes.ps1 # Native GPU resource/fence ownership
./scripts/test-native-capture.ps1 # Native UTF-8 transfer and row arena ownership
./scripts/test-release.ps1 -OldReleaseDirectory artifacts/previous-release # Install/update/rollback checks
```

The terminal suite checks VT parsing, colors, attributes, fragmented UTF-8, wide glyphs, alternate screens, OSC title updates, replies, bells, key/mouse/focus encoding, paste modes, scrollback, history search, selection formatting, layouts, real shell I/O, resizing, exit status, and teardown timing. It also checks Windows French, German AltGr and US International dead-key translation without activating a layout. The WinUI driver checks splits, shells, persistence, composition commit/cancel, device replacement, live rendering DPI changes and window power notifications. A separate UI Automation client tests the exported text, selection and focused input through Windows. It opens a temporary window and closes it automatically; it does not type into other apps or use the normal saved session. Soak reports are saved under `artifacts`; each UI run has its own `ui-smoke-*` directory.

Synthetic performance, scale and endurance fixtures suppress physical text/key input into their test panes so normal typing cannot contaminate scripted commands. The launcher attempts to return foreground focus to the previously active window. Visible performance comparisons reject minimized windows. Endurance pauses its active clock and onscreen assertions during external suspension; its own minimize/restore recovery steps retain their timeout checks. Thirty minutes of accumulated external suspension marks a run interrupted. Offscreen GPU tests do not measure physical display scanout or actual driver-reset recovery.

For hands-on validation, run `./scripts/test-hardware.ps1`. It builds a diagnostic copy and opens an isolated window with a literal input receiver, a disposable drag/drop fixture and a seven-check guide. Input entered into that receiver is recorded locally and never executed. Select Pass, Fail or Blocked after performing each check; observations are saved automatically under `artifacts/hardware-check-*/hardware-check.json`. `artifacts/hardware-current.json` identifies the current process, report and fixture. Close the diagnostic window when finished. The installed release and normal workspace data are separate.

This driver observes native composition/drop callbacks, notification activation, display scale and window power events. User observations establish IME candidate behavior, Narrator speech and actual sleep/reset recovery; simulated messages or software device changes are separate automated evidence. A single display blocks mixed-monitor validation, and no IME language packs or system settings are installed or changed by the driver.

## Portable build

```powershell
./scripts/publish.ps1
```

Produces `artifacts/toiqtty-win-x64.zip`. Extract the entire directory and run `toiqtty.exe`. Both .NET and Windows App SDK runtimes are included; there is no MSIX registration step. The build is unsigned.

Run `./scripts/install.ps1` after publishing to install under `%LOCALAPPDATA%\Programs\toiqtty\versions\<version>`, add **toiqtty** to the current user's Start menu and open it. Windows Search can discover that shortcut. Installation verifies the release manifest and every copied file, stages a new immutable version, and updates the launch shortcut and installation state together. Existing binaries, running shells, settings and browser data are preserved. Use `-ReleaseDirectory` for a different published folder or `-NoLaunch` to install without opening a window. The installed copy runs independently of workspace artifacts.

The ZIP includes installation tools in `tools/`. `./scripts/update.ps1 -ArchivePath <zip> -ExpectedSha256 <hash> -NoLaunch` checks the archive checksum, validates archive paths and installs the verified payload. `./scripts/rollback.ps1 -NoLaunch` verifies and restores the previous installed version. The versioned ZIP's `.json` sidecar contains its checksum; a checksum detects corruption but is not publisher authentication. Updates do not terminate existing frontends or session hosts. Close and reopen the window to use the new frontend; host-side features become available after the older host's shells end naturally. `./scripts/test-release.ps1 -OldReleaseDirectory <previous-release>` exercises update, rollback, tampering rejection and activation failure recovery in disposable directories.

If the current release is running, publish alongside it with `./scripts/publish.ps1 -OutputDirectory artifacts/release-next`. Validate that folder with `./scripts/test.ps1 -SkipNative -Published -ReleaseDirectory artifacts/release-next`.

`./scripts/activate-release.ps1` waits for the old `artifacts/release/toiqtty.exe` to close, copies the staged release into that folder, and reopens the updated app. It never terminates running terminals. Both folders must be inside this workspace's `artifacts` directory. The app displays its version in the title bar.

Settings and layouts live in `%LOCALAPPDATA%\toiqtty\session.json`. Unhandled errors are logged to `crash.log` in that folder. To isolate development data, set `TOIQTTY_DATA_DIR` for the launched process.

## Current limits

PowerShell has automatic command integration, Windows/WSL/remote directory handling and native desktop notifications. PowerShell command text is bounded to fit Ghostty's OSC buffer; the complete command still executes. Original MIT-licensed adapters support Bash 5.1+ and Zsh. They track command boundaries, duration, exit status and percent-encoded cwd; Zsh also supplies bounded command text. Login startup, custom prompts and existing hooks are preserved. Scripts and unknown launch options retain their original invocation. See [adapter documentation](src/Toiqtty/Integration/posix/README.md) for startup behavior, manual loading and compatibility limits. Protocol parsing still accepts upstream-supported metadata emitted by a shell's own configuration. toiqtty does not modify local or remote profiles. Remote directory authorities are displayed without becoming local process directories. Use `scripts/test-interop.ps1 -WslDistro archlinux -Zsh` for the extracted zsh 5.9.1 test client; it requires compatible Arch Linux libraries and installs no packages globally.

Kitty file and temporary-file image transfers use Ghostty's Windows loader; drop targets support Copy/Move negotiation, text/HTML/RTF MIME capture and completion callbacks. A native Move is reported only after the terminal application confirms completion. toiqtty integrates upstream protocol implementations; missing upstream features stay unavailable until upstream implements them. This includes OSC 66, Windows shared-memory images, source-side drags, remote file-content drops and Link operations.

toiqtty is Windows x64 only. A custom native Direct3D 12 renderer consumes Ghostty's terminal state and DirectWrite-shaped glyphs; Ghostty's internal macOS embedding API is not used. Ghostty protocol compatibility is a core milestone: see [PROTOCOLS.md](PROTOCOLS.md) for implemented coverage and remaining work. Scaled text (OSC 66) remains unsupported: the pinned Ghostty engine parses and ignores it, so implementing it requires engine screen-state work. Touch selection and Ghostty configuration-file compatibility remain pending. OS layout translation, composition handlers, external UIA, device replacement, rendering DPI changes and power-notification recovery are automated. Physical IME input, Narrator speech/navigation, actual driver loss and real system sleep/wake still need hardware validation. Mixed-monitor transitions have been verified with two monitors at 100% and 175% scaling. Kitty/tmux/WSL interop is opt-in; WSL pixel-size autodetection requires Kitty's explicit window-size option on the tested console path.

The app ships Microsoft's pinned ConPTY console host so graphics escapes reach Ghostty on systems whose built-in console host strips them. The Ghostty pin includes reproducible source patches exposing its existing animation, Unicode-placeholder layout and drag/drop behavior to our host. Basic image and animation output is checked through a real PowerShell/ConPTY session and against rendered PNG pixels; this does not establish complete interoperability with every graphics application.

Embedded WebView2 browser panes, companion-extension cookie sharing for selected sites, Lua settings/startup layouts and local persistent shells are implemented. The `toiq` CLI, Lua action bindings, modal key tables, fuzzy session switching and visual/window controls are implemented. Remote-machine session transport and multi-window attachment remain future work. The private session IPC supports one foreground toiqtty window per data directory. Host failure or reboot ends live local shells; subsequent startup uses configured Lua actions or one default shell. Activity is retained while detached; Windows toast delivery runs in an attached window. See [ARCHITECTURE.md](ARCHITECTURE.md) for the implementation boundaries and next work.

## Command line

`toiq.exe` ships beside `toiqtty.exe`. The normal per-user installer adds its version directory to the user PATH; existing shells retain their inherited PATH. New processes launched from the refreshed desktop environment can use `toiq`. In an existing shell, invoke the installed executable by its full path. `toiq --help` lists options, and `toiq --version` prints the version.

```powershell
toiq sessions --json
toiq new-session --name Development --cwd D:\repo
toiq focus Development
toiq new-tab --session Development --name Logs
toiq split right --session Development
toiq rename-session Development 'Daily work'
toiq action nextWallpaper
toiq action lua:scratch
toiq reload
```

Use session IDs or exact unique names; ambiguous names fail. `--data-dir DIRECTORY` targets an isolated toiqtty instance. `sessions` lists sessions/tabs/panes and can read a detached host's layout without starting anything. `launch` explicitly opens the window. Other commands require an open window and reject workspace mutations while a dialog or drag is active.

`toiq send-text --pane ID --text TEXT` sends literal UTF-8, including control characters, without adding Enter. `toiq paste --pane ID --text TEXT` uses Ghostty's paste handling. Omit `--pane` to target the selected pane, or use `--session` to target a session's selected pane. Both report input admission, not command completion. Literal input requires the corresponding host capability. The CLI does not automatically stop or upgrade an existing host.

The local control pipe is restricted to the current Windows user, versioned and size/deadline bounded. UI mutations use the same action/layout methods as keyboard controls. JSON output goes to stdout; errors go to stderr with exit code 1. Requests are never retried after an ambiguous failure. Dialog-opening actions require the window's normal controls. Remote transport and multiple attached windows remain separate work.

## Sources

- [Ghostty](https://github.com/ghostty-org/ghostty): public terminal-state C API; exact revision in `native/ghostty.lock.json`.
- [Microsoft ConPTY documentation](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session) and [Windows Terminal’s ConPTY connection](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalConnection/ConptyConnection.cpp).
- [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/get-started/start-here) and [Win2D](https://github.com/microsoft/Win2D).
- [cmux](https://github.com/manaflow-ai/cmux): product and interaction inspiration. No cmux code or assets are included.
