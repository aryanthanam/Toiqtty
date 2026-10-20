#!/bin/sh
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Toiqtty contributors
assets=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd) || exit 1
selected=${1-}
[ "$#" -eq 0 ] || shift
if [ -z "$selected" ]; then
    selected=$(getent passwd "$(id -u)" 2>/dev/null | cut -d: -f7)
    selected=${selected:-${SHELL:-/bin/sh}}
    # WSL's default invocation is a login shell.
    set -- -l "$@"
fi
case "$selected" in
    [a-zA-Z]:/*) selected=$(cygpath -u "$selected") || exit 1 ;;
esac
case "${selected##*/}" in
    bash|bash.exe)
        # ENV is the documented startup hook for interactive POSIX Bash.
        # The bootstrap restores ordinary Bash mode before user startup files.
        TOIQTTY_OLD_ENV_SET=${ENV+x}
        TOIQTTY_OLD_ENV=${ENV-}
        TOIQTTY_HISTORY_WAS_SET=${HISTFILE+x}
        TOIQTTY_BASH_NO_RC=0 TOIQTTY_BASH_NO_PROFILE=0
        for option in "$@"; do
            case "$option" in
                --norc) TOIQTTY_BASH_NO_RC=1 ;;
                --noprofile) TOIQTTY_BASH_NO_PROFILE=1 ;;
            esac
        done
        TOIQTTY_POSIX_ASSETS=$assets
        ENV='$TOIQTTY_POSIX_ASSETS/bash-startup.bash'
        export TOIQTTY_OLD_ENV_SET TOIQTTY_OLD_ENV TOIQTTY_HISTORY_WAS_SET TOIQTTY_BASH_NO_RC TOIQTTY_BASH_NO_PROFILE TOIQTTY_POSIX_ASSETS ENV
        exec "$selected" --posix "$@" -i
        ;;
    zsh|zsh.exe)
        TOIQTTY_OLD_ZDOTDIR_SET=${ZDOTDIR+x}
        TOIQTTY_OLD_ZDOTDIR=${ZDOTDIR-}
        TOIQTTY_POSIX_ASSETS=$assets
        ZDOTDIR=$assets/zsh
        export TOIQTTY_OLD_ZDOTDIR_SET TOIQTTY_OLD_ZDOTDIR TOIQTTY_POSIX_ASSETS ZDOTDIR
        exec "$selected" "$@" -i
        ;;
    *) exec "$selected" "$@" ;;
esac
