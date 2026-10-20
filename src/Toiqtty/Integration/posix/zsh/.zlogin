# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Toiqtty contributors
__toiqtty_zsh_restore_directory
[[ ! -r ${ZDOTDIR-$HOME}/.zlogin ]] || builtin source "${ZDOTDIR-$HOME}/.zlogin"
builtin source "$__toiqtty_zsh_assets/zsh.zsh"
unset __toiqtty_zsh_assets __toiqtty_zsh_user_dir_set __toiqtty_zsh_user_dir
