"""Exercise original shell adapters in real PTYs; no third-party test libraries.

python3 tests/posix-adapters.py [--zsh /path/to/zsh] [--zsh-root /path/to/package]
"""
import argparse
import os
import pathlib
import pty
import re
import select
import shutil
import signal
import tempfile
import time

parser = argparse.ArgumentParser()
parser.add_argument('--zsh', default=shutil.which('zsh'))
parser.add_argument('--zsh-root')
args = parser.parse_args()
source = pathlib.Path(__file__).resolve().parents[1] / 'src/Toiqtty/Integration/posix'
passed = 0


def check(value, message):
    global passed
    if not value:
        raise AssertionError(message)
    passed += 1
    print('PASS:', message, flush=True)


class Shell:
    def __init__(self, launcher, executable, home, flags=(), extra=None, initial_marker=b'\x1b]133;B\x07'):
        self.data = b''
        self.pid, self.fd = pty.fork()
        if self.pid == 0:
            env = dict(os.environ, HOME=str(home), TERM='xterm-256color', TERM_PROGRAM='toiqtty')
            for name in ('ENV', 'ZDOTDIR', 'PROMPT_COMMAND', 'BASH_ENV', 'POSIXLY_CORRECT', 'BASHOPTS', 'SHELLOPTS', 'HISTFILE'):
                env.pop(name, None)
            env.update(extra or {})
            os.chdir(home)
            os.execve('/bin/sh', ['sh', str(launcher), executable, *flags], env)
        self.until(initial_marker)

    def until(self, marker, timeout=8, offset=0):
        deadline = time.monotonic() + timeout
        while marker not in self.data[offset:]:
            if time.monotonic() > deadline:
                raise AssertionError(f'timed out waiting for {marker!r}: {self.data[-5000:]!r}')
            if select.select([self.fd], [], [], 0.1)[0]:
                try:
                    part = os.read(self.fd, 65536)
                except OSError:
                    part = b''
                if not part:
                    raise AssertionError(f'shell exited before {marker!r}: {self.data[-5000:]!r}')
                self.data += part
        return self.data

    def prompt_after_completion(self):
        self.until(b'\x1b]133;D;')
        start = self.data.index(b'\x1b]133;D;')
        self.until(b'\x07', offset=start)
        end = self.data.index(b'\x07', start) + 1
        self.until(b'\x1b]133;B\x07', offset=end)

    def command(self, text, expected=0):
        self.data = b''
        os.write(self.fd, text.encode() + b'\n')
        self.prompt_after_completion()
        codes = re.findall(rb'\x1b\]133;D;([0-9]+)\x07', self.data)
        check(codes == [str(expected).encode()], f'{self.name}: one completion with exit {expected}: {text[:65]} | {self.data!r}' if codes != [str(expected).encode()] else f'{self.name}: one completion with exit {expected}: {text[:65]}')
        starts = re.findall(rb'\x1b\]133;C(?:;[^\x07]*)?\x07', self.data)
        check(len(starts) == 1, f'{self.name}: one command start')
        return self.data

    def close(self):
        try:
            os.write(self.fd, b'exit\n')
            end = time.monotonic() + 3
            while time.monotonic() < end:
                if os.waitpid(self.pid, os.WNOHANG)[0]:
                    return
                time.sleep(.02)
            os.kill(self.pid, signal.SIGKILL)
            os.waitpid(self.pid, 0)
        finally:
            os.close(self.fd)


def exercises(shell, adapter, home):
    shell.command('false', 1)
    result = shell.command("printf 'PRIOR=%s\\n' \"$?\"")
    check(b'PRIOR=1\r\n' in result, shell.name + ': prompt hooks preserve $? for the next command')
    shell.command('(exit 7)', 7)
    check(b'USER_STATUS=7' in shell.data, shell.name + ': user prompt hook receives original exit status')
    shell.command('set -u; false', 1)
    shell.command('set +u')
    shell.command('set -e; false && true', 1)
    shell.command('set +e')
    result = shell.command("printf 'VALUE=%s\\n' 'café 中; %'")
    check('VALUE=café 中; %'.encode() in result, shell.name + ': Unicode and delimiters execute unchanged')
    shell.command(". '" + str(adapter).replace("'", "'\\''") + "'")
    shell.command('true')
    shell.data = b''
    os.write(shell.fd, b'\x0c')
    time.sleep(.1)
    while select.select([shell.fd], [], [], 0)[0]:
        shell.data += os.read(shell.fd, 65536)
    check(b'\x1b]133;C' not in shell.data and b'\x1b]133;A\x07' not in shell.data, shell.name + ': redraw does not invent a command or a fresh prompt')
    shell.command("printf 'redirected' > output.txt")
    check((home / 'output.txt').read_bytes() == b'redirected', shell.name + ': metadata never enters redirected command output')
    target = home / "space ' % 中"
    target.mkdir(exist_ok=True)
    result = shell.command("cd '" + str(target).replace("'", "'\\''") + "'")
    check(b'%20%27%20%25%20%E4%B8%AD' in result, shell.name + ': cwd metadata encodes Unicode, quotes, spaces and percent')
    shell.data = b''
    os.write(shell.fd, b'for item in 1 2; do\n')
    shell.until(b'\x1b]133;P;k=s\x07')
    os.write(shell.fd, b'printf "MULTI=%s\\n" "$item"; done\n')
    shell.prompt_after_completion()
    check(len(re.findall(rb'\x1b\]133;C(?:;[^\x07]*)?\x07', shell.data)) == 1, shell.name + ': multiline input is a single command')
    shell.data = b''
    os.write(shell.fd, b'sleep 10\n')
    shell.until(b'\x1b]133;C')
    time.sleep(.15)
    os.write(shell.fd, b'\x03')
    shell.until(b'\x1b]133;D;130\x07')
    shell.prompt_after_completion()
    check(True, shell.name + ': Ctrl+C completes a running command with 130')
    shell.command('true')


with tempfile.TemporaryDirectory(prefix='toiqtty-adapters-') as temp:
    root = pathlib.Path(temp)
    assets = root / "assets ' $(touch NEVER_CREATED)"
    shutil.copytree(source, assets)
    launcher = assets / 'launch-posix.sh'
    bash = shutil.which('bash')
    for login in (False, True):
        home = root / ('bash-login' if login else 'bash-interactive')
        home.mkdir()
        (home / '.bash_profile').write_text("printf 'PROFILE_READ\\n'\n. \"$HOME/.bashrc\"\n")
        (home / '.bash_login').write_text("printf 'WRONG_PROFILE\\n'\n")
        (home / '.bashrc').write_text("""
printf 'RC_READ\\n'
__fixture_status() { printf '\\nUSER_STATUS=%s\\n' "$?"; PS1='CUSTOM-BASH> '; }
PROMPT_COMMAND=(__fixture_status)
PS2='CONTINUE> '
trap ':' DEBUG
""")
        shell = Shell(launcher, bash, home, ('-l',) if login else (), {'ENV': 'original literal $value'})
        shell.name = 'bash-login' if login else 'bash'
        try:
            check(shell.data.count(b'RC_READ\r\n') == 1 and b'WRONG_PROFILE\r\n' not in shell.data, shell.name + ': user startup files read once')
            check((b'PROFILE_READ\r\n' in shell.data) == login, shell.name + ': login startup selection')
            result = shell.command("printf 'ENV_VALUE=%s\\n' \"$ENV\"; shopt login_shell; set -o | grep '^posix'", 0)
            check(b'ENV_VALUE=original literal $value' in result and re.search(rb'posix\s+off', result), shell.name + ': restores ENV and ordinary Bash mode')
            check(bool(re.search(rb'login_shell\s+on', result)) == login, shell.name + ': retains actual login-shell state')
            result = shell.command("printf 'HISTORY_FILE=%s\\n' \"$HISTFILE\"; shopt inherit_errexit || true")
            check(('HISTORY_FILE=' + str(home / '.bash_history')).encode() in result and re.search(rb'inherit_errexit\s+off', result), shell.name + ': restores normal history path and inherited-errexit default')
            shell.command('set +o history')
            exercises(shell, assets / 'bash.bash', home)
            result = shell.command('trap -p DEBUG')
            check(b"trap -- ':' DEBUG" in result, shell.name + ': existing DEBUG trap is untouched')
        finally:
            shell.close()
    for flags in (('--norc',), ('--noprofile', '-l')):
        home = root / ('bash-skips-' + str(len(flags)))
        home.mkdir()
        (home / '.bashrc').write_text("printf 'UNEXPECTED_RC\\n'\n")
        (home / '.bash_profile').write_text("printf 'UNEXPECTED_PROFILE\\n'\n")
        shell = Shell(launcher, bash, home, flags)
        shell.name = 'bash-' + '-'.join(flags)
        try:
            check(b'UNEXPECTED_' not in shell.data, shell.name + ': respects startup suppression flags')
            shell.command('false', 1)
        finally:
            shell.close()
    home = root / 'bash-scalar'
    home.mkdir()
    (home / '.bashrc').write_text("PROMPT_COMMAND='printf \"USER_STATUS=%s\\n\" \"$?\"'\nPS1='STATUS[$?]> '\n")
    shell = Shell(launcher, bash, home)
    shell.name = 'bash-scalar'
    try:
        result = shell.command('false', 1)
        check(b'USER_STATUS=1' in result and b'STATUS[1]>' in result, 'Bash scalar hook and prompt expansion preserve failure status')
    finally:
        shell.close()
    home = root / 'bash-readonly'
    home.mkdir()
    (home / '.bashrc').write_text("PROMPT_COMMAND=':'\nreadonly PROMPT_COMMAND\nPS1='READONLY> '\n")
    shell = Shell(launcher, bash, home, initial_marker=b'READONLY> ')
    try:
        check(b'readonly variable' not in shell.data and b'\x1b]133;' not in shell.data, 'Bash readonly prompt hooks decline integration without startup errors')
    finally:
        shell.close()
    home = root / 'bash-history-override'
    home.mkdir()
    history_file = str(home / 'custom-history')
    shell = Shell(launcher, bash, home, ('--norc',), {'HISTFILE': history_file})
    shell.name = 'bash-history-override'
    try:
        result = shell.command("printf 'SAVED_HISTORY=%s\\n' \"$HISTFILE\"")
        check(('SAVED_HISTORY=' + history_file).encode() in result, 'Bash preserves an explicitly inherited history file')
    finally:
        shell.close()
    if not args.zsh:
        raise AssertionError('Zsh is required; supply --zsh for an extracted client')
    for login in (False, True):
        home = root / ('zsh-login' if login else 'zsh-interactive')
        home.mkdir()
        user = home / 'changed-zdotdir'
        user.mkdir()
        # Exercise our user startup fixtures without host-global rc files such
        # as Ubuntu's interactive compinit. User rc/login processing stays on.
        env_file = "unsetopt globalrcs\nprintf 'ENV_READ\\n'\n"
        if args.zsh_root:
            package = pathlib.Path(args.zsh_root)
            env_file += f"fpath=('{package}/usr/share/zsh/functions/Misc' $fpath)\nmodule_path=('{package}/usr/lib/zsh/5.9.1' $module_path)\n"
        env_file += f"export ZDOTDIR='{user}'\n"
        (home / '.zshenv').write_text(env_file)
        (user / '.zprofile').write_text("printf 'PROFILE_READ\\n'\n")
        (user / '.zlogin').write_text("printf 'LOGIN_READ\\n'\n")
        (user / '.zshrc').write_text("""
printf 'RC_READ\\n'
function __fixture_status { printf '\\nUSER_STATUS=%s\\n' "$?"; PROMPT='CUSTOM-ZSH> '; }
precmd_functions+=(__fixture_status)
PROMPT2='CONTINUE> '
""")
        shell = Shell(launcher, args.zsh, home, ('-l',) if login else ())
        shell.name = 'zsh-login' if login else 'zsh'
        try:
            check(shell.data.count(b'ENV_READ\r\n') == 1 and shell.data.count(b'RC_READ\r\n') == 1, shell.name + ': user env/rc read once')
            check((b'PROFILE_READ\r\n' in shell.data) == login and (b'LOGIN_READ\r\n' in shell.data) == login, shell.name + ': native login-file ordering')
            result = shell.command("printf 'ZDOTDIR_VALUE=%s\\n' \"$ZDOTDIR\"")
            check(str(user).encode() in result, shell.name + ': user .zshenv can change ZDOTDIR normally')
            exercises(shell, assets / 'zsh.zsh', home)
            long_command = "printf '%s' '" + ('中' * 700) + "' >/dev/null"
            result = shell.command(long_command)
            encoded = re.search(rb'cmdline_url=([^\x07]*)', result).group(1)
            import urllib.parse
            decoded = urllib.parse.unquote_to_bytes(encoded.decode()).decode('utf-8')
            check(len(encoded) < 1900 and decoded.startswith("printf '%s'"), shell.name + ': long command metadata is bounded at a UTF-8 boundary')
        finally:
            shell.close()
    home = root / 'zsh-no-rcs'
    home.mkdir()
    disabled_env = ''
    if args.zsh_root:
        disabled_env = f"fpath=('{args.zsh_root}/usr/share/zsh/functions/Misc' $fpath)\nmodule_path=('{args.zsh_root}/usr/lib/zsh/5.9.1' $module_path)\n"
    disabled_env += f"ZDOTDIR='{home}'\nPROMPT='RCS-OFF> '\nunsetopt rcs\n"
    (home / '.zshenv').write_text(disabled_env)
    shell = Shell(launcher, args.zsh, home, initial_marker=b'RCS-OFF> ')
    try:
        shell.data = b''
        os.write(shell.fd, b'printf "USER_DIR=%s\\n" "$ZDOTDIR"\n')
        shell.until(b'RCS-OFF> ')
        check(('USER_DIR=' + str(home)).encode() in shell.data and b'\x1b]133;' not in shell.data, 'Zsh NO_RCS retains the user directory without injecting hooks')
    finally:
        shell.close()
    check(not any(root.rglob('NEVER_CREATED')), 'asset paths with shell syntax remain literal')
print(f'{passed} POSIX adapter PTY checks passed.')
