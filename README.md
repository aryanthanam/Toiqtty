# toiqtty

![toiqtty logo](src/Toiqtty/Assets/Brand/toiqtty-logo-v1.png)

Launch `toiqtty.exe` or use the `toiq` CLI. Version 0.10.0 uses toiqtty names throughout its projects, assemblies, native libraries, IPC, Lua API and installation. Settings live in `%LOCALAPPDATA%\toiqtty`; the Lua entry point is `toiqtty.lua`, using `require('toiqtty')`.

A native Windows terminal built with **libghostty-vt**, **WinUI 3**, **Direct3D 12/DirectWrite**, and **ConPTY**. Inspired by cmux’s compact session sidebar, split panes, and attention indicators. Optional **WebView2** panes provide an embedded browser. Terminal composition uses a dedicated native GPU renderer. HarfBuzz shapes eligible ASCII runs; DirectWrite preserves Windows font fallback, Unicode, color and italic text. Win2D supplies font configuration, measurement and reference fixtures.

This is a working first version, not a production-complete terminal. It starts real local shells and maintains independent terminal sessions across workspace switches.

## Run

Requires Windows 11 x64 or Windows 10 build 19041+, .NET 10 SDK, Visual Studio 2022 C++ build tools, a Windows SDK including FXC, Git, and internet access for the first build. The scripts download a checksum-verified Zig 0.16.0 compiler inside `.tools/`, fetch a pinned Ghostty commit, and fetch checksum-pinned HarfBuzz source into `.deps/`. The WinUI build builds the native Direct3D renderer and HarfBuzz core and precompiles its shaders with the installed Windows SDK. HarfBuzz uses its DirectWrite adapter without external FreeType, GLib or Graphite libraries.

```powershell
powershell -ExecutionPolicy Bypass -File scripts/run.ps1
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
- Per-session themes: right-click a session and choose **Session theme…**, or use the command palette. Pick one of ten bundled Gogh presets from a visual gallery. Themes color the session card, title/tab/status bars, browser controls, focus markers, and terminal panes: default foreground/background, cursor, selection and all 16 ANSI colors. Ghostty applies terminal colors without changing shell processes, text or history; application truecolor and OSC overrides remain effective. **Use default** restores normal colors. A host older than 0.7.14 keeps its shells alive and shows a notice: pane colors require **Quit toiqtty and stop all sessions**, then relaunch. Closing only the window does not upgrade the host.
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

Version 0.9.0 uses a dedicated native Direct3D 12 pipeline with batched glyphs, backgrounds, decorations, images and Powerline shapes. DirectWrite still provides shaping, fallback and color emoji. Native GPU caches are bounded, translucent and inactive panes blend through compositor drawing surfaces, and unsupported hardware feature levels fall back to WARP. The session host and terminal protocol remain compatible with existing live shells. Native pixel/debug, composition, resize and device-transition checks accompany the migration; actual driver removal remains a separate hardware check. DX12 command lists, barriers, descriptor heaps and fence-owned upload/allocator slots draw terminal frames; 11-on-12 remains the DirectWrite rasterization and WinUI transparency bridge. Performance results depend on the workload; the migration alone is not a guarantee of lower latency.

Version 0.9.2 gives renderer allocations explicit device, pane, cache-entry and GPU-submission lifetimes. Shared pipelines initialize once per device; pane presentation objects survive alpha/DPI changes; shaped glyph arrays retire with their cache entry; resource pins and upload arenas recycle only after their submission fence completes. Managed WinUI objects still follow their platform lifetime rules. See [ARCHITECTURE.md](ARCHITECTURE.md) for ownership and bounds. Run `scripts/test-native-lifetimes.ps1` for standalone arena checks and `scripts/test-native-gpu-lifetimes.ps1` for native hardware/WARP lifetime checks; both accept `-AddressSanitizer`. WinUI GPU checks run with `scripts/test-directx.ps1 -DebugLayer -Offscreen`.

Version 0.9.4 adds instanced quads, asynchronous DXGI readiness, bounded retained resize capacity, native font formats, HarfBuzz ASCII shaping and cached system color glyphs. Host capture keeps UTF-8 until encoding and negotiates compact style runs, reusable read channels and unchanged-status omission. The frontend still uses Windows Unicode APIs for fallback, input and accessibility. Short paired experiments measure each change; submitted frames are distinct from displayed refreshes. Existing hosts remain compatible, while host-side improvements require a newly started host after ending its shells.

Version 0.7.1 reduces resize and background bookkeeping costs. Managed viewport/transport cells reuse exact-sized, cleared buffers with a process-wide 16 MiB retention limit; native cell storage reuses capacity and releases unusually large buffers when the viewport shrinks. Updated hosts batch hidden-pane metadata every 400 ms and omit unchanged state. Showing a pane cancels its queued metadata wait immediately. Updated hosts also clear selection in the resize request itself, while older hosts retain the explicit fallback request.

Version 0.7.2 fixes a metadata cancellation race during rapid show/hide transitions, bounds queued metadata messages to 256 pane IDs and avoids rebuilding watcher interest sets when the cached list is unchanged. Older session hosts remain supported; existing shells can keep running while a new frontend is installed.

Version 0.7.3 also bounds metadata responses by bytes. A valid batch with long command histories can exceed the 16 MiB IPC limit even when each pane fits individually; the client subdivides those read-only requests. Canceling all readers stops further subdivision. This uses the optional `metadata-batch-v2` capability; older hosts and clients keep individual state polling.

Version 0.7.4 removes parser-lock waits from published status/mouse metadata, lets output drain during a ConPTY resize, and protects the console handle against concurrent resize and close. Request handling also runs independently of pipe acceptance. Targeted contention and handle-lifetime tests failed before these changes and pass afterward.

Version 0.7.5 avoids an extra copy of the retained native-cell buffer during frame capture. The buffer is pinned for the synchronous native call, using the current viewport's cell count. Unicode, extended grapheme, resize and differential snapshot checks cover the same capture path.

Version 0.7.6 computes the exact outgoing snapshot size before allocating its transport buffer, including UTF-8 text, string-length prefixes, changed rows and uncached image pixels. An oversized frame previously allocated about 512 MiB before the 96 MiB limit rejected it; the regression now rejects it after about 912 bytes. Valid full frames and deltas retain the same wire format. Three paired codec measurements produced identical payload bytes with about 10% fewer encoding allocations and similar timings. Its final two-hour differential fuzz run completed 1,826,716 comparisons across 2,281,207,966 cells with no errors, including full/delta snapshots, graphics, resizes and rejected payloads; this fixture does not exercise real shell input or physical GPU events.

Version 0.7.7 fixes selection and mouse-coordinate drift at fractional display scaling. Recovering the renderer's integral physical cell size with a second ceiling could turn a 37-pixel cell into 38 pixels in the terminal engine, shifting hit testing progressively across the grid. Cell sizes now round back to the original integer, and resize deduplication includes cell dimensions. Native selection checks cover 100–300% scaling and the far edges of the grid; WinUI checks also copy text through the persistent session after rapid split resizing.

Version 0.7.8 separates shared terminal/contracts code and frontend services into referenced Core and Client assemblies. Grid geometry now travels with the accepted frame, including the native cell dimensions used for selection and mouse reports. Stale physical metrics cannot block capture. Layout removal no longer closes shells implicitly; explicit asynchronous close/detach operations and window-close drains own teardown. Layout persistence uses isolated, coalesced snapshots, and host disk writes run outside the global session registry lock. Published core checks load the exact release DLLs; added regressions cover delayed geometry, snapshot isolation, save ordering and lifecycle completion. Diagnostic startup parsing is centralized, while full driver extraction and asynchronous shell creation remain further architecture work.

Unchanged output-interest and status reads reuse immutable snapshots. Git branch checks run asynchronously and share one lookup per directory in each metadata pass. Frontend teardown waits for outstanding captures before recycling their storage. These changes preserve protocol 2, existing settings and shell ownership; host-side improvements take effect when an updated host starts after older sessions end.

Version 0.7.0 retains complete tab split trees as well as terminal controls. Switching tabs reuses the existing splitters and tab items, while selection saves are coalesced off the input thread on updated hosts. Hidden layouts and terminal resources remain bounded, and closing a pane still ends its shell. Selected tabs have a clearer marker, inactive close buttons appear on hover or keyboard focus, an overflow menu reaches offscreen tabs, and the sidebar narrows with the window. The unified 32-DIP title/tab row remains.

The renderer reads DirectWrite cluster metrics into reusable bounded storage instead of allocating a projected array for each shaped run. Updated hosts also negotiate per-pane output watches, so busy hidden or unrelated panes do not wake idle visible panes unnecessarily. Protocol 2 and older-host fallbacks remain supported. Pending resize requests are submitted before drawing and restoring input focus, preserving ConPTY input ordering without synchronous IPC in layout callbacks.

Version 0.6.8 uses an event-driven Win2D swap chain for terminal presentation. Frames draw without waiting for CanvasControl/XAML's redraw cycle; the former inter-capture timer is replaced by backpressure while a frame awaits consumption. The renderer retains text, background and image caches, and handles buffer resizing, DPI transitions and device replacement. Keyboard, paste, focus, scrolling and selection use the bounded asynchronous FIFO introduced in 0.6.7: admitted input keeps its order, pastes remain whole, held keys reserve releases, and uncertain failures are reported without replay. Updated hosts advertise output notifications and upstream mouse-tracking metadata; older hosts use compatibility polling and retain their mouse-ownership query. The frontend can update while shells stay alive; host-side features activate after the existing host exits and the next session starts. `scripts/test-input.ps1` checks backpressure, raw ConPTY input, Ctrl+C behavior and request-to-Present latency with stage traces on updated and older hosts. Its timer reference uses the current swap chain, not an older release. Present returning is not a measurement of physical display scanout.

Version 0.5.0 introduces the first cmux-inspired WinUI polish pass: a compact tab/action row, clearer session cards and selected tabs, consistent pane focus, browser chrome, grouped Settings, a searchable command palette with shortcut hints, and an empty session state. `scripts/test-design.ps1` generates real WinUI previews at wide, compact and narrow window sizes, plus sidebar-hidden, browser, palette, Settings and empty-state captures. Validation uses a disposable data directory and local shells.

Version 0.5.1 flattens that interface around the terminal surface: no per-pane title bars or surrounding frames, flat tabs, a compact session list, and hairline split separators. Split, zoom and close-pane controls live in the native overflow menu and keyboard shortcuts. A single shell fills the content area directly. Preview captures use real PowerShell commands and include the single-terminal layout.

Version 0.5.2 matches Ghostty's default scrollback budget: 50,000,000 bytes (50 MB) per terminal, with no line cap. Lua supports `scrollback_limit_bytes` and `scrollback_limit_lines`, each accepting a nonnegative integer or `'unlimited'`. Reload applies limits to live terminals in the updated session host; reopening applies current settings to reattached shells. History uses Ghostty's existing page-based retention. Upgrading an older host requires **Quit toiqtty and stop all sessions** before launching the new release; simply closing the window keeps that older host alive.

Version 0.6.2 adds a native WinUI scrollbar backed by Ghostty's history dimensions and absolute row positioning. Its fixed gutter prevents output reflow when history grows. Middle-click autoscroll uses a dead zone and distance-based speed; switching panes/tabs, losing focus, suspending or closing the view cancels it. Alternate-screen applications have no fabricated history. `scripts/test-scrolling.ps1` verifies native scroll coordinates, mouse reporting, autoscroll behavior, accessible thumb positioning, independent split panes and real session-host transport. Scrollbar metadata is optional in version-2 host replies: older hosts remain compatible, but require an upgrade before they can supply a scrollbar. Stop all sessions before installing this update when ready; a window-only restart keeps the old host alive.

Version 0.6.3 fixes premature autoscroll cancellation around capture loss, release and focus changes. A stationary click remains latched even when capture loss arrives before release, and in-pane exit notifications are ignored. Shift/Ctrl/Alt alone do not cancel the gesture; Escape cancels without sending Escape to the shell. Moving focus outside the pane still cancels after focus settles. This update changes frontend behavior and works with the 0.6.2 session host. `scripts/install.ps1 -InstallDirectory` supports installing alongside a running host without overwriting its files.

Version 0.6.4 routes autoscroll through Ghostty's existing mouse-wheel encoder when an application requests mouse input, allowing it to control the application's own scrollable view. When mouse reporting is disabled, it falls back to terminal history. Wheel reports stay at the gesture's starting point and do not include the Shift modifier used to start the gesture. The scrolling tests include a real raw-input, alternate-screen app that receives and counts wheel reports through ConPTY and the session host. This frontend update remains compatible with the 0.6.2 session host.

Version 0.6.5 guards title-bar caption geometry before constructing WinUI layout sizes. Full dumps from two GPU diagnostic runs identify `UpdateTitleRegions -> GridLength(double)` throwing an invalid-argument exception from a dispatcher callback. Invalid display scales and caption insets now leave the last valid layout intact; subsequent geometry changes restore normal layout. The UI smoke test exercises queued invalid inputs and recovery at 96/168 DPI. The GPU controller allows time for a native crash dump and process exit after a window disappears, and records unhandled exceptions without assuming user closure. This frontend update remains compatible with the 0.6.2 session host.

Version 0.6.6 fixes a separate rapid pane-resize crash. The native dump identifies WinUI's `CheckReentrancy` failure while a synchronous host resize waited inside `SizeChanged` and pumped another pointer message. Layout callbacks now record a pending size; presentation ticks apply the latest size, and the frontend sends serialized asynchronous host requests with at most one pending replacement. Displayed cells, metadata and GPU layers stay together until the resized frame arrives, avoiding blank frames during host latency. `scripts/test-resize.ps1` exercises a 1,000-request burst, nested split resizing, delayed-frame pixel retention, shell input and Kitty images. This frontend update remains compatible with the 0.6.2 session host.

Version 0.5.3 removes repeated host-discovery waits, starts a missing host alongside window construction, reuses the existing connection while its owner is alive, and precompiles release assemblies with ReadyToRun. An exited host is detected before creating a new shell. `scripts/test-startup.ps1` measures process-to-window, first rendered frame and prompt readiness across fresh hosts and reattachments. Add `-Profile` to measure output emitted after the current PowerShell profile loads. Benchmarks use disposable data directories and a separate executable copy so their notification registration cannot affect the running app. A fresh host is not a cold OS or reboot measurement.

Version 0.5.4 combines tabs, session actions and native window controls into one compact 32-DIP title bar. The terminal begins directly below it. Empty title-bar space remains draggable, interactive controls use Windows passthrough regions, and caption-button insets follow DPI changes. Sidebar hiding and horizontal tab scrolling update those regions.

Version 0.5.5 adds session/tab drag reordering and pane relocation with native destination controls. Version 0.6.1 restricts pane relocation to tabs and splits within the owning session, including validation of drag previews and programmatic moves. Version 0.6.9 also retains terminal controls, swapchains, text layers and image textures through these moves, tab/session switches and zoom. Hidden-view retention is bounded to eight controls and an estimated 128 MiB of GPU resources; evicted views leave their shells running. Browser panes retain their WebViews. Attention and notification activation resolve the pane's owner. Moves validate destination capacity and split depth before changing either tree. `scripts/test-navigation.ps1` checks ownership boundaries and real PowerShell PID/state, scrollback, images, browser identity, and frontend reconnects in isolated sessions.

Version 0.5.6 replaces draggable tab buttons with a horizontal WinUI ListView using the same native item-reordering mechanism as the session sidebar. Drag completion returns focus to the active terminal or browser. The terminal renderer batches identical character-spacing updates, uses compact width keys and gradual cache eviction, skips empty trailing cells, reuses dirty-row flags, and avoids resetting unchanged caret properties. Exact-pixel tests compare spacing batches against individual updates across font styles, ligatures, fallback characters, RTL text and long graphemes.

The session transport keeps protocol 2: UTF-8 cells retain their existing binary format, decoder buffers alternate for safe reuse, and row comparisons stop at the first difference before copying that row. Repeated title/cwd reads reuse an 8 KB buffer and cached values. Shutdown replies finish before the host cancels its connections. Existing compatible hosts remain attachable; host-side optimizations take effect when a new host starts after the current shells end. Closing the window alone keeps the current host running.

`scripts/test-performance.ps1` benchmark version 3 measures one, four and eight balanced panes; `-Scale16` includes sixteen. Quiet, controlled-output and saturated-output phases are joined by a mixed phase with one busy pane and idle peers. QPC timestamps record input admission, child receipt/emission, host state arrival, drawing and response-frame Present return through event callbacks; latency samples contain no polling interval. CPU, allocations, actual output throughput, output-watch counters and percentiles are recorded separately. The isolated window is placed above other windows without taking keyboard focus to avoid occlusion throttling. Present return does not measure physical display scanout. `scripts/compare-performance.ps1` pools matched runs and rejects mixed benchmark versions or missing cases.

The October 17 overnight measurements are retained in `artifacts/goal-20261017-overnight/`. Three alternating rounds of 0.7.0, 0.7.4 and 0.7.5 used the same client and a fixed 2240×1435 physical viewport at 175% scaling, with two independent GPU diagnostics active. Comparing 0.7.0 with 0.7.5, managed UI allocations fell by 1.2–21.9% across all sixteen cases. Latency remained mixed: sixteen-pane saturated-output p95 improved from 20.78 to 13.07 ms, while controlled-output p95 worsened from 6.71 to 14.19 ms. These results do not establish a universal 120 Hz response guarantee.

Three paired resize-stress runs per version used measurement-only copies with two additional cumulative diagnostic counters. Managed allocation fell from 8.63 to 5.75 MiB/s, with similar UI CPU cost (0.56 versus 0.54 cores). Three 100-switch runs per version gave an average per-run median of 29.53 ms on 0.6.9, 6.53 ms on 0.7.0 and 6.77 ms on 0.7.5; these are not pooled percentiles. A separate same-client comparison of 64 hidden shells against the original and updated hosts measured 135 seconds per version after warmup: client allocation fell from 1.23 to 0.20 MiB/s and host CPU from about 0.06 to 0.01 cores. That headless measurement excludes WinUI rendering. The 0.7.6 codec measurements above are separate from this 0.7.5 visual corpus. Only the portrait display remained active during the final rerun, so the earlier wide-window geometry and two-display DPI check were unavailable for 0.7.6.

Version 0.6.9 coalesces bulk draws to composition ticks, lets input run between pane draws, reuses receive/text buffers and typography, and pauses unnecessary output watching and minimized-window presentation. New hosts negotiate changed-row transfers; older protocol-2 hosts remain compatible with full frames. The baseline and matched measurement evidence is retained in `artifacts/goal-20261015-performance/`. The experimental `-ResizeStress` probe remains separate from fixed-viewport comparisons; losing its response marker invalidates a latency run. Use session endurance for resize correctness. `scripts/test-performance-regressions.ps1` checks pane reuse, delta/full-frame recovery, background suspension, navigation, resize, scrolling and input, including a legacy host. `scripts/test-endurance.ps1` pins and hashes a release copy and exercises sessionized output, input, resizing, retained tabs, zoom, minimize/restore, images and renderer replacement for a requested duration. Frontend and host debugger attachment can be selected independently with `-FrontendDebuggerMode` and `-HostDebuggerMode` (`Native`, `Managed`, or `None`). The result records those modes. Disabling continuous debuggers retains the strict capture deadlines and health checks, plus a host dump on the first captured transport failure; it does not provide continuous native-crash monitoring.

Three baseline and three updated measurements on this machine, with an isolated endurance workload active, produced the following controlled-output results at approximately 32 MiB/s aggregate. CPU is UI process CPU time divided by wall time, expressed as cores; allocations are managed UI allocations. Latency percentiles pool timestamped samples from the three runs. These results are machine/workload measurements, not universal guarantees.

| Panes | UI CPU cores, 0.6.8 → 0.6.9 | UI allocations MiB/s, 0.6.8 → 0.6.9 | Request-to-Present p95 ms, 0.6.8 → 0.6.9 |
| --- | --- | --- | --- |
| 1 | 2.68 → 0.71 | 98.8 → 11.3 | 13.89 → 11.93 |
| 4 | 2.02 → 0.55 | 63.1 → 9.5 | 12.96 → 6.82 |
| 8 | 2.05 → 0.83 | 53.8 → 13.0 | 22.70 → 9.72 |

Quiet pooled p95 for 0.6.9 was 4.11/3.16/7.99 ms for one/four/eight panes. Saturated eight-pane p95 was 7.52 ms; controlled one/eight-pane p95 still exceeded the 8.33 ms target. That release retained renderer resources but rebuilt tab layouts; 0.7.0 addresses the additional layout/focus cost. A sparse-update fixture transferred approximately 94.5% fewer frame bytes with negotiated deltas than an older full-frame host. Registered process waits and overlapped parent pipes remove blocked workers for shell exit and idle output reads. An isolated four-worker test with eight shells and pending reads changed queued-work admission from over one second to under one millisecond. See `final-comparison.json`, `pipe-workers-before.log`, `pipe-workers-after.log`, and the process-wait logs in the 0.6.9 goal evidence directory.

Version 0.5.7 wakes remote polling when a view attaches, avoiding a hidden-pane wait during startup and tab/zoom transitions. Its regular visible and hidden polling intervals remain 33 and 400 milliseconds.

The October 17 final 0.7.6 sessionized endurance run passed 10,803 active seconds with no external pause or capture failure. It completed 1,713 cycles while retaining all five views, preserving its shell identities and passing the memory/handle and image checks. The separate strict host transport run passed 10,803 seconds, 527,337 snapshots and 525 reattachments; late combined host/client private-memory growth was about 43.5 MiB against its unchanged 64 MiB limit. Both isolated hosts exited normally after their authorized test shutdowns.

A separate 0.7.4 local-terminal GPU diagnostic passed 6.5 hours and all 98 assertions. Final UI assertions need a restored, drawable test window; that run explicitly restored and activated only its own window. Continuous UIA text polling changes the resource workload by allocating terminal-text snapshots: a separate heap investigation found growing allocated snapshot counts but no live ranges or snapshots at either sampled point. That evidence does not establish a live-reference leak. Sustained text observation stays separate from GPU memory validation, whose limits remain unchanged. Failed and interrupted attempts remain in the evidence catalog.

Earlier overnight native-engine and host/transport tests completed four hours, including 687 host reconnects. A separate sessionized GPU diagnostic completed one hour with bounded texture/layout caches. The earlier local-terminal GPU diagnostic crashed after about 3 hours 48 minutes and showed substantial private-memory growth; its four-hour result is incomplete and remains an investigation item. These offscreen rendering checks do not establish onscreen frame rates or physical GPU recovery. The evidence manifest records versions and validation limits.

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
```

The terminal suite checks VT parsing, colors, attributes, fragmented UTF-8, wide glyphs, alternate screens, OSC title updates, replies, bells, key/mouse/focus encoding, paste modes, scrollback, history search, selection formatting, layouts, real shell I/O, resizing, exit status, and teardown timing. It also checks Windows French, German AltGr and US International dead-key translation without activating a layout. The WinUI driver checks splits, shells, persistence, composition commit/cancel, device replacement, live rendering DPI changes and window power notifications. A separate UI Automation client tests the exported text, selection and focused input through Windows. It opens a temporary window and closes it automatically; it does not type into other apps or use the normal saved session. Soak reports are saved under `artifacts`; each UI run has its own `ui-smoke-*` directory.

Synthetic performance, scale and endurance fixtures suppress physical text/key input into their test panes so normal typing cannot contaminate scripted commands. The launcher attempts to return foreground focus to the previously active window. Visible performance comparisons reject minimized windows. Endurance pauses its active clock and onscreen assertions during external suspension; its own minimize/restore recovery steps retain their timeout checks. Thirty minutes of accumulated external suspension marks a run interrupted. Offscreen GPU tests do not measure physical display scanout or actual driver-reset recovery.

## Portable build

For hands-on validation, run `./scripts/test-hardware.ps1`. It builds a diagnostic copy and opens an isolated window with a literal input receiver, a disposable drag/drop fixture and a seven-check guide. Input entered into that receiver is recorded locally and never executed. Select Pass, Fail or Blocked after performing each check; observations are saved automatically under `artifacts/hardware-check-*/hardware-check.json`. `artifacts/hardware-current.json` identifies the current process, report and fixture. Close the diagnostic window when finished. The installed release and normal workspace data are separate.

This driver observes native composition/drop callbacks, notification activation, display scale and window power events. User observations establish IME candidate behavior, Narrator speech and actual sleep/reset recovery; simulated messages or software device changes are separate automated evidence. A single display blocks mixed-monitor validation, and no IME language packs or system settings are installed or changed by the driver.

Version 0.1.8 adds this guided validation mode and fixes retrieval of the initial notification payload on a cold COM launch. `scripts/test.ps1 -Notifications` also tests real Windows COM warm activation and cold-launch payload retrieval after the UI test exits. Version 0.1.9 fixes Japanese IME mode-key routing and cancellation ordering; native candidate selection, exact commit and empty cancellation were verified with Microsoft Japanese IME. Physical mixed-monitor DPI transitions were verified on 2026-10-13 using two displays at 100% (96 DPI) and 175% (168 DPI). Real driver device-loss testing remains unverified.

Version 0.2.0 adds the independent session host and the session/tab/pane hierarchy. Run `./scripts/test.ps1 -SkipNative -Ui -Sessions` to test host lifecycle, detach/reconnect and two-process WinUI close/crash recovery. Each run uses disposable data directories, verifies the original PowerShell PID and in-memory variable, and checks detached output, graphics, tab layouts, focused panes and host shutdown. The isolated hardware and ordinary UI-smoke drivers continue using local sessions unless running the explicit persistent-session probe.

Version 0.3.0 adds WebView2 browser panes and companion cookie sharing. Its session-host IPC protocol is version 2 so older hosts cannot discard browser layout fields. Before activating the staged release, use **Quit toiqtty and stop all sessions** in the installed version, then run `./scripts/activate-release.ps1`. Closing only the window leaves the old host running. Starting the new version while an older host is live shows an upgrade window without changing its sessions or saved layout.

```powershell
./scripts/publish.ps1
```

Produces `artifacts/toiqtty-win-x64.zip`. Extract the entire directory and run `toiqtty.exe`. Both .NET and Windows App SDK runtimes are included; there is no MSIX registration step. The build is unsigned.

Run `./scripts/install.ps1` after publishing to install under `%LOCALAPPDATA%\Programs\toiqtty\versions\<version>`, add **toiqtty** to the current user's Start menu and open it. Windows Search can discover that shortcut. Installation verifies the release manifest and every copied file, stages a new immutable version, and updates the launch shortcut and installation state together. Existing binaries, running shells, settings and browser data are preserved. Use `-ReleaseDirectory` for a different published folder or `-NoLaunch` to install without opening a window. The installed copy runs independently of workspace artifacts.

The ZIP includes installation tools in `tools/`. `./scripts/update.ps1 -ArchivePath <zip> -ExpectedSha256 <hash> -NoLaunch` checks the archive checksum, validates archive paths and installs the verified payload. `./scripts/rollback.ps1 -NoLaunch` verifies and restores the previous installed version. The versioned ZIP's `.json` sidecar contains its checksum; a checksum detects corruption but is not publisher authentication. Updates do not terminate existing frontends or session hosts. Close and reopen the window to use the new frontend; host-side features become available after the older host's shells end naturally. `./scripts/test-release.ps1` exercises update, rollback, tampering rejection and activation failure recovery in disposable directories.

If the current release is running, publish alongside it with `./scripts/publish.ps1 -OutputDirectory artifacts/release-next`. Validate that folder with `./scripts/test.ps1 -SkipNative -Published -ReleaseDirectory artifacts/release-next`.

`./scripts/activate-release.ps1` waits for the old `artifacts/release/toiqtty.exe` to close, copies the staged release into that folder, and reopens the updated app. It never terminates running terminals. Both folders must be inside this workspace's `artifacts` directory. The app displays its version in the title bar.

Settings and layouts live in `%LOCALAPPDATA%\toiqtty\session.json`. Unhandled errors are logged to `crash.log` in that folder. To isolate development data, set `TOIQTTY_DATA_DIR` for the launched process.

## Current limits

PowerShell has automatic command integration, Windows/WSL/remote directory handling and native desktop notifications. PowerShell command text is bounded to fit Ghostty's OSC buffer; the complete command still executes. Version 0.4.1 adds original MIT-licensed Bash 5.1+ and Zsh adapters. They track command boundaries, duration, exit status and percent-encoded cwd; Zsh also supplies bounded command text. Login startup, custom prompts and existing hooks are preserved. Scripts and unknown launch options retain their original invocation. See [adapter documentation](src/Toiqtty/Integration/posix/README.md) for startup behavior, manual loading and compatibility limits. Protocol parsing still accepts upstream-supported metadata emitted by a shell's own configuration. toiqtty does not modify local or remote profiles. Remote directory authorities are displayed without becoming local process directories. Use `scripts/test-interop.ps1 -WslDistro archlinux -Zsh` for the extracted zsh 5.9.1 test client; it requires compatible Arch Linux libraries and installs no packages globally.

Version 0.1.6 adds Kitty file and temporary-file image transfers using Ghostty's existing Windows loader, plus Copy/Move drop-target negotiation, text/HTML/RTF MIME capture and completion callbacks. A native Move is reported only after the terminal application confirms completion. toiqtty integrates upstream protocol implementations; missing upstream features stay unavailable until upstream implements them. This includes OSC 66, Windows shared-memory images, source-side drags, remote file-content drops and Link operations.

toiqtty is Windows x64 only. A custom native Direct3D 12 renderer consumes Ghostty's terminal state and DirectWrite-shaped glyphs; Ghostty's internal macOS embedding API is not used. Ghostty protocol compatibility is a core milestone: see [PROTOCOLS.md](PROTOCOLS.md) for implemented coverage and remaining work. Scaled text (OSC 66) remains unsupported: the pinned Ghostty engine parses and ignores it, so implementing it requires engine screen-state work. Touch selection and Ghostty configuration-file compatibility remain pending. Version 0.1.5 passes a ten-minute four-shell/image soak and a two-minute real WinUI renderer soak. OS layout translation, composition handlers, external UIA, device replacement, rendering DPI changes and power-notification recovery are automated. Physical IME input, Narrator speech/navigation, actual driver loss and real system sleep/wake still need hardware validation. Mixed-monitor transitions have been verified with two monitors at 100% and 175% scaling. Kitty/tmux/WSL interop is opt-in; WSL pixel-size autodetection requires Kitty's explicit window-size option on the tested console path.

The app ships Microsoft's pinned ConPTY console host so graphics escapes reach Ghostty on systems whose built-in console host strips them. The Ghostty pin includes a small, reproducible patch exposing upstream animation and Unicode-placeholder layout to our host. Basic image and animation output is checked through a real PowerShell/ConPTY session and against rendered PNG pixels; this does not establish complete interoperability with every graphics application.

Embedded WebView2 browser panes, companion-extension cookie sharing for selected sites, Lua settings/startup layouts and local persistent shells are implemented. The public `oro` CLI, richer Lua actions, modal key tables, fuzzy session switching and visual/window controls are implemented in 0.7.19. Remote-machine session transport and multi-window attachment remain future work. The private session IPC supports one foreground toiqtty window per data directory. Host failure or reboot ends live local shells; subsequent startup uses configured Lua actions or one default shell. Activity is retained while detached; Windows toast delivery runs in an attached window. See [ARCHITECTURE.md](ARCHITECTURE.md) for the implementation boundaries and next work.

## Command line (0.7.19)

`toiq.exe` ships beside `toiqtty.exe`. The normal per-user installer adds its version directory to the user PATH; existing shells retain their inherited PATH. New processes launched from the refreshed desktop environment can use `oro`. In an existing shell, invoke the installed executable by its full path. `oro --help` lists options, and `oro --version` prints the version.

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

`toiq send-text --pane ID --text TEXT` sends literal UTF-8, including control characters, without adding Enter. `toiq paste --pane ID --text TEXT` uses Ghostty's paste handling. Omit `--pane` to target the selected pane, or use `--session` to target a session's selected pane. Both report input admission, not command completion. Literal input requires a 0.7.19+ host. The CLI does not automatically stop or upgrade an existing host.

The local control pipe is restricted to the current Windows user, versioned and size/deadline bounded. UI mutations use the same action/layout methods as keyboard controls. JSON output goes to stdout; errors go to stderr with exit code 1. Requests are never retried after an ambiguous failure. Dialog-opening actions require the window's normal controls. Remote transport and multiple attached windows remain separate work.

## Sources

- [Ghostty](https://github.com/ghostty-org/ghostty): public terminal-state C API; exact revision in `native/ghostty.lock.json`.
- [Microsoft ConPTY documentation](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session) and [Windows Terminal’s ConPTY connection](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalConnection/ConptyConnection.cpp).
- [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/get-started/start-here) and [Win2D](https://github.com/microsoft/Win2D).
- [cmux](https://github.com/manaflow-ai/cmux): product and interaction inspiration. No cmux code or assets are included.
