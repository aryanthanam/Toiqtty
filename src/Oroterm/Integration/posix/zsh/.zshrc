# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Oroterm contributors
__oroterm_zsh_restore_directory
[[ ! -r ${ZDOTDIR-$HOME}/.zshrc ]] || builtin source "${ZDOTDIR-$HOME}/.zshrc"
if [[ -o login && -o rcs ]]; then
    __oroterm_zsh_continue_startup
else
    builtin source "$__oroterm_zsh_assets/zsh.zsh"
    unset __oroterm_zsh_assets __oroterm_zsh_user_dir_set __oroterm_zsh_user_dir
fi
