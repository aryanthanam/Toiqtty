# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Oroterm contributors
# ENV bootstrap: normal Bash startup files execute before installing hooks.
set +o posix
# POSIX mode enables this option as a side effect; restore ordinary Bash's
# default before loading the user's requested options from startup files.
if (( BASH_VERSINFO[0] >= 4 )); then shopt -u inherit_errexit 2>/dev/null || :; fi
if [[ ${OROTERM_OLD_ENV_SET-} == x ]]; then export ENV=${OROTERM_OLD_ENV-}; else unset ENV; fi
if [[ ${OROTERM_HISTORY_WAS_SET-} != x ]]; then HISTFILE=$HOME/.bash_history; fi
unset OROTERM_HISTORY_WAS_SET
# Keep bootstrap state unexported so rc-file children cannot inherit it.
export -n OROTERM_POSIX_ASSETS OROTERM_BASH_NO_RC OROTERM_BASH_NO_PROFILE
unset OROTERM_OLD_ENV_SET OROTERM_OLD_ENV
if shopt -q login_shell; then
    if [[ ${OROTERM_BASH_NO_PROFILE-0} != 1 ]]; then
        [[ ! -r /etc/profile ]] || source /etc/profile
        for __oroterm_profile in "$HOME/.bash_profile" "$HOME/.bash_login" "$HOME/.profile"; do
            if [[ -r $__oroterm_profile ]]; then source "$__oroterm_profile"; break; fi
        done
        unset __oroterm_profile
    fi
elif [[ ${OROTERM_BASH_NO_RC-0} != 1 ]]; then
    # Distro-wide interactive rc paths vary. Source one system rc, then the
    # user's rc, as the normal non-login Bash startup sequence does.
    if [[ -r /etc/bash.bashrc ]]; then source /etc/bash.bashrc
    elif [[ -r /etc/bash/bashrc ]]; then source /etc/bash/bashrc
    fi
    [[ ! -r $HOME/.bashrc ]] || source "$HOME/.bashrc"
fi
source "${OROTERM_POSIX_ASSETS}/bash.bash"
unset OROTERM_POSIX_ASSETS OROTERM_BASH_NO_RC OROTERM_BASH_NO_PROFILE
