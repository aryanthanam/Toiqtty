# Third-party components

Oroterm uses the following dependencies. cmux is a design reference only; its source and assets have not been copied.

The Bash/Zsh adapters in `Integration/posix` are original Oroterm code, licensed under MIT in `Integration/posix/LICENSE`. They do not include the previously bundled Ghostty/Kitty shell scripts or bash-preexec.

| Component | Source | License |
| --- | --- | --- |
| libghostty-vt | https://github.com/ghostty-org/ghostty | MIT; full license shipped as `GHOSTTY-LICENSE` |
| Windows App SDK / WinUI 3 | https://github.com/microsoft/WindowsAppSDK | Microsoft distribution terms in the NuGet packages; open-source components under MIT |
| Microsoft Edge WebView2 SDK (Windows App SDK dependency) | https://learn.microsoft.com/en-us/microsoft-edge/webview2/ | Microsoft SDK distribution terms, shipped as `licenses/WEBVIEW2-LICENSE`; Evergreen Runtime is installed separately |
| LuaCSharp 0.5.7 | https://github.com/nuskey8/Lua-CSharp/tree/6f449d2f03d7b1eab99c1fd3bf8f2aff337f45a9 | MIT; shipped as `licenses/LUA-CSHARP-LICENSE`; managed Lua 5.2 interpreter |
| Win2D | https://github.com/microsoft/Win2D | MIT source; Microsoft binary distribution terms linked by the NuGet package |
| Microsoft ConPTY / OpenConsole 1.25.260930003 | https://github.com/microsoft/terminal | MIT; shipped as `licenses/ConPTY-LICENSE` |
| .NET | https://github.com/dotnet/runtime | MIT; third-party notices included with the runtime |
| JetBrainsMono Nerd Font Mono (Nerd Fonts 3.5.1, JetBrains Mono 2.304) | https://github.com/ryanoasis/nerd-fonts/tree/v3.5.1/patched-fonts/JetBrainsMono/Ligatures | SIL Open Font License 1.1; shipped as `licenses/JetBrainsMonoNerdFont-OFL.txt`, with component notices in `licenses/NerdFonts-LICENSE` |
| Zig compiler (build tool only) | https://ziglang.org | MIT and bundled third-party licenses |

The pinned source revision and compiler version are recorded in `native/ghostty.lock.json`. Native dependency license files are copied by the publish script. Runtime/package notices should remain alongside the application when redistributing a portable build.

Oroterm applies `native/patches/graphics-host.patch` to that Ghostty revision to expose existing animation and Unicode-placeholder rendering helpers. Ghostty's MIT license applies to the upstream code; the patch is included in the source workspace.
# Session theme palettes

The ten bundled session UI presets are adapted from [Gogh](https://github.com/Gogh-Co/Gogh) under its MIT license option. Original authors, the pinned source commit, exact color mappings, and source hashes are recorded in `licenses/GOGH-THEMES.json`; the license is included as `licenses/GOGH-LICENSE-MIT`. Only palette data is incorporated, not Gogh's installation scripts. Session UI accents use a named color from each source palette. Alien Blood uses its source bright foreground for readable interface text.

