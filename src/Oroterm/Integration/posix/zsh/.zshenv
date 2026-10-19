# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Oroterm contributors
# Startup shims source each user file at file scope, retaining its typeset and
# return semantics. Restore the user's ZDOTDIR while each file executes.
if [[ ${OROTERM_OLD_ZDOTDIR_SET-} == x ]]; then
    export ZDOTDIR=${OROTERM_OLD_ZDOTDIR-}
else
    unset ZDOTDIR
fi
typeset -g __oroterm_zsh_assets=${OROTERM_POSIX_ASSETS}
unset OROTERM_OLD_ZDOTDIR_SET OROTERM_OLD_ZDOTDIR OROTERM_POSIX_ASSETS
function __oroterm_zsh_restore_directory {
    emulate -L zsh
    if [[ $__oroterm_zsh_user_dir_set == 1 ]]; then export ZDOTDIR=$__oroterm_zsh_user_dir; else unset ZDOTDIR; fi
    return 0
}
function __oroterm_zsh_continue_startup {
    local continue_rcs=$options[rcs]
    emulate -L zsh
    typeset -g __oroterm_zsh_user_dir_set=${+ZDOTDIR} __oroterm_zsh_user_dir=${ZDOTDIR-}
    if [[ $continue_rcs == on ]]; then
        export ZDOTDIR=$__oroterm_zsh_assets/zsh
    else
        unset __oroterm_zsh_assets __oroterm_zsh_user_dir_set __oroterm_zsh_user_dir
    fi
    return 0
}
[[ ! -r ${ZDOTDIR-$HOME}/.zshenv ]] || builtin source "${ZDOTDIR-$HOME}/.zshenv"
__oroterm_zsh_continue_startup
