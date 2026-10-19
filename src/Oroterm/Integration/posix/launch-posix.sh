#!/bin/sh
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Oroterm contributors
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
        OROTERM_OLD_ENV_SET=${ENV+x}
        OROTERM_OLD_ENV=${ENV-}
        OROTERM_HISTORY_WAS_SET=${HISTFILE+x}
        OROTERM_BASH_NO_RC=0 OROTERM_BASH_NO_PROFILE=0
        for option in "$@"; do
            case "$option" in
                --norc) OROTERM_BASH_NO_RC=1 ;;
                --noprofile) OROTERM_BASH_NO_PROFILE=1 ;;
            esac
        done
        OROTERM_POSIX_ASSETS=$assets
        ENV='$OROTERM_POSIX_ASSETS/bash-startup.bash'
        export OROTERM_OLD_ENV_SET OROTERM_OLD_ENV OROTERM_HISTORY_WAS_SET OROTERM_BASH_NO_RC OROTERM_BASH_NO_PROFILE OROTERM_POSIX_ASSETS ENV
        exec "$selected" --posix "$@" -i
        ;;
    zsh|zsh.exe)
        OROTERM_OLD_ZDOTDIR_SET=${ZDOTDIR+x}
        OROTERM_OLD_ZDOTDIR=${ZDOTDIR-}
        OROTERM_POSIX_ASSETS=$assets
        ZDOTDIR=$assets/zsh
        export OROTERM_OLD_ZDOTDIR_SET OROTERM_OLD_ZDOTDIR OROTERM_POSIX_ASSETS ZDOTDIR
        exec "$selected" "$@" -i
        ;;
    *) exec "$selected" "$@" ;;
esac
