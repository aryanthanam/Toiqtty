# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Toiqtty contributors
# ENV bootstrap: normal Bash startup files execute before installing hooks.
set +o posix
# POSIX mode enables this option as a side effect; restore ordinary Bash's
# default before loading the user's requested options from startup files.
if (( BASH_VERSINFO[0] >= 4 )); then shopt -u inherit_errexit 2>/dev/null || :; fi
if [[ ${TOIQTTY_OLD_ENV_SET-} == x ]]; then export ENV=${TOIQTTY_OLD_ENV-}; else unset ENV; fi
if [[ ${TOIQTTY_HISTORY_WAS_SET-} != x ]]; then HISTFILE=$HOME/.bash_history; fi
unset TOIQTTY_HISTORY_WAS_SET
# Keep bootstrap state unexported so rc-file children cannot inherit it.
export -n TOIQTTY_POSIX_ASSETS TOIQTTY_BASH_NO_RC TOIQTTY_BASH_NO_PROFILE
unset TOIQTTY_OLD_ENV_SET TOIQTTY_OLD_ENV
if shopt -q login_shell; then
    if [[ ${TOIQTTY_BASH_NO_PROFILE-0} != 1 ]]; then
        [[ ! -r /etc/profile ]] || source /etc/profile
        for __toiqtty_profile in "$HOME/.bash_profile" "$HOME/.bash_login" "$HOME/.profile"; do
            if [[ -r $__toiqtty_profile ]]; then source "$__toiqtty_profile"; break; fi
        done
        unset __toiqtty_profile
    fi
elif [[ ${TOIQTTY_BASH_NO_RC-0} != 1 ]]; then
    # Distro-wide interactive rc paths vary. Source one system rc, then the
    # user's rc, as the normal non-login Bash startup sequence does.
    if [[ -r /etc/bash.bashrc ]]; then source /etc/bash.bashrc
    elif [[ -r /etc/bash/bashrc ]]; then source /etc/bash/bashrc
    fi
    [[ ! -r $HOME/.bashrc ]] || source "$HOME/.bashrc"
fi
source "${TOIQTTY_POSIX_ASSETS}/bash.bash"
unset TOIQTTY_POSIX_ASSETS TOIQTTY_BASH_NO_RC TOIQTTY_BASH_NO_PROFILE
