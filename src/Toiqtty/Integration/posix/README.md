# Toiqtty Bash and Zsh integration

These adapters are original Toiqtty code under the MIT license in this directory. They use the shell's documented startup and prompt facilities and Ghostty-supported OSC 7 and OSC 133 messages. They contain no Ghostty/Kitty integration code or bash-preexec dependency.

## Behavior

- Bash 5.1+ uses `PS0` to mark execution and a `PROMPT_COMMAND` array to finish the command, report its exit status, and mark the next prompt. Scalar and array user hooks retain their contents and original command status. Bash command text is intentionally omitted: tracking does not depend on history, DEBUG traps, or editor bindings.
- Zsh uses `preexec_functions`, `precmd_functions`, and `chpwd_functions`. The native preexec callback supplies command text; its metadata is limited to 600 UTF-8 bytes without cutting a character. Execution always receives the complete command.
- Both report percent-encoded directory URIs, retain user prompts, mark continuation prompts, and avoid fresh-prompt markers on redraw. Re-sourcing removes only the adapters' own hook entries before attaching one copy, so it can repair hooks replaced by a prompt framework.
- Existing DEBUG/EXIT/signal traps and Readline/ZLE key bindings are untouched. Hooks return success so later hooks can run. Shell exit completion is handled by the terminal's existing EOF processing.

The Windows launcher recognizes plain interactive Bash/Zsh commands, supported login flags, and WSL's default shell or explicit Bash/Zsh launch forms. Scripts, `-c`, `-f`, restricted/debug modes, custom rc-file arguments, unknown WSL options, and other executables retain their invocation. Explicit inherited `POSIXLY_CORRECT`, `BASHOPTS`, or `SHELLOPTS` also disable automatic augmentation. Unsupported Bash versions retain normal shell startup but do not install prompt hooks.

## Startup

No profiles are written. Bash's short-lived POSIX-mode `ENV` bootstrap restores normal Bash mode, the original `ENV`, the normal history filename, and the `inherit_errexit` default before reading the requested startup files. Login profiles and `--norc`/`--noprofile` are respected; user startup files can then set their own shell options normally.

Zsh startup shims let Zsh run its system startup files in order, source each user startup file at file scope, and install integration after the final user rc/login file. The user's `ZDOTDIR` is restored while user files run and before the first prompt, including changes made by those files. System startup files can observe the temporary shim `ZDOTDIR`; unusual system configurations that replace it can bypass automatic injection. Manual sourcing is the fallback for those configurations. Zsh hook functions that fail can prevent later hooks from running, according to Zsh's normal semantics.

## Manual loading

Source the matching adapter **after** your prompt framework is configured:

```bash
source '/path/to/Toiqtty/Integration/posix/bash.bash'
```

```zsh
source '/path/to/Toiqtty/Integration/posix/zsh.zsh'
```

Manual loading also supports remote shells when these original files are copied to the remote machine. Toiqtty does not install remote files or edit remote profiles. Source-browser cookies and Lua configuration have no role in shell integration.

## Validation

`python3 tests/posix-adapters.py` uses real Bash and Zsh PTYs with isolated homes. Supply `--zsh /path/to/zsh --zsh-root /path/to/extracted/package` for the local extracted test client. The Windows smoke test's optional WSL matrix exercises the adapters through ConPTY and libghostty, including command state, duration, cwd, Unicode, Ctrl+C and shell EOF. Native MSYS2/Cygwin launching is recognized but needs those runtimes for end-to-end testing.

Design references: [Bash manual](https://www.gnu.org/software/bash/manual/), [Zsh startup files](https://zsh.sourceforge.io/Doc/Release/Files.html), [Zsh hook functions](https://zsh.sourceforge.io/Doc/Release/Functions.html).
