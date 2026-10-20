# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Toiqtty contributors
# Startup shims source each user file at file scope, retaining its typeset and
# return semantics. Restore the user's ZDOTDIR while each file executes.
if [[ ${TOIQTTY_OLD_ZDOTDIR_SET-} == x ]]; then
    export ZDOTDIR=${TOIQTTY_OLD_ZDOTDIR-}
else
    unset ZDOTDIR
fi
typeset -g __toiqtty_zsh_assets=${TOIQTTY_POSIX_ASSETS}
unset TOIQTTY_OLD_ZDOTDIR_SET TOIQTTY_OLD_ZDOTDIR TOIQTTY_POSIX_ASSETS
function __toiqtty_zsh_restore_directory {
    emulate -L zsh
    if [[ $__toiqtty_zsh_user_dir_set == 1 ]]; then export ZDOTDIR=$__toiqtty_zsh_user_dir; else unset ZDOTDIR; fi
    return 0
}
function __toiqtty_zsh_continue_startup {
    local continue_rcs=$options[rcs]
    emulate -L zsh
    typeset -g __toiqtty_zsh_user_dir_set=${+ZDOTDIR} __toiqtty_zsh_user_dir=${ZDOTDIR-}
    if [[ $continue_rcs == on ]]; then
        export ZDOTDIR=$__toiqtty_zsh_assets/zsh
    else
        unset __toiqtty_zsh_assets __toiqtty_zsh_user_dir_set __toiqtty_zsh_user_dir
    fi
    return 0
}
[[ ! -r ${ZDOTDIR-$HOME}/.zshenv ]] || builtin source "${ZDOTDIR-$HOME}/.zshenv"
__toiqtty_zsh_continue_startup
