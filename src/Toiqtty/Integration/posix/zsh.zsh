# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Toiqtty contributors
# May also be sourced manually, after the user's prompt setup.
[[ -o interactive && -t 1 ]] || return 0
if [[ ${__toiqtty_zsh_pid-} != $$ ]]; then typeset -g __toiqtty_zsh_running=0; fi
typeset -g __toiqtty_zsh_pid=$$

function __toiqtty_zsh_uri {
    emulate -L zsh
    setopt localoptions nomultibyte
    local LC_ALL=C input=$1 byte hex result='' piece
    local -i maximum=${2:-32768} consumed=0 committed=0 next=0
    # Keep a complete UTF-8 prefix when bounding command metadata. Paths are
    # passed without a small limit, and all control bytes are percent encoded.
    while [[ -n $input ]] && (( consumed < maximum )); do
        byte=${input[1]}; input=${input[2,-1]}
        case $byte in
            [a-zA-Z0-9.~_/-]) piece=$byte ;;
            *) builtin printf -v hex '%02X' "'$byte"; piece=%$hex ;;
        esac
        result+=$piece
        (( consumed++ ))
        if [[ -z $input ]]; then
            committed=${#result}
        else
            builtin printf -v next '%d' "'${input[1]}"
            if (( next < 128 || next >= 192 )); then committed=${#result}; fi
        fi
    done
    builtin printf '%s' "${result[1,committed]}"
}

function __toiqtty_zsh_cwd {
    emulate -L zsh
    local directory=$PWD host=${HOST-}
    case $OSTYPE in
        msys*|cygwin*)
            if (( $+commands[cygpath] )); then
                directory=$(command cygpath -am -- "$directory") || return 0
                directory=/$directory
            fi ;;
    esac
    host=${host//[^a-zA-Z0-9._-]/_}
    builtin printf '\033]7;file://%s%s%s\007' "$host" "$(__toiqtty_zsh_uri "$directory")" "${TOIQTTY_CWD_TOKEN:+?toiqtty-cwd=$TOIQTTY_CWD_TOKEN}"
    return 0
}

function __toiqtty_zsh_preexec {
    emulate -L zsh
    [[ -t 1 ]] || return 0
    __toiqtty_zsh_running=1
    builtin printf '\033]133;C;cmdline_url=%s\007' "$(__toiqtty_zsh_uri "$1" 600)"
    return 0
}

function __toiqtty_zsh_finish {
    local code=$?
    emulate -L zsh
    [[ -t 1 ]] || return 0
    if (( __toiqtty_zsh_running )); then
        builtin printf '\033]133;D;%d\007' "$code"
        __toiqtty_zsh_running=0
    fi
    return 0
}

function __toiqtty_zsh_precmd {
    emulate -L zsh
    local start=$'%{\e]133;P;k=i\a%}' end=$'%{\e]133;B\a%}'
    local secondary=$'%{\e]133;P;k=s\a%}'
    [[ -t 1 ]] || return 0
    [[ ${PROMPT-} != "$start"* ]] || PROMPT=${PROMPT#"$start"}
    [[ ${PROMPT-} != *"$end" ]] || PROMPT=${PROMPT%"$end"}
    PROMPT=$start${PROMPT-}$end
    [[ ${PROMPT2-} != "$secondary"* ]] || PROMPT2=${PROMPT2#"$secondary"}
    [[ ${PROMPT2-} != *"$end" ]] || PROMPT2=${PROMPT2%"$end"}
    PROMPT2=$secondary${PROMPT2-}$end
    __toiqtty_zsh_cwd
    builtin printf '\033]133;A\007'
    return 0
}

function __toiqtty_zsh_chpwd {
    emulate -L zsh
    [[ ! -t 1 ]] || __toiqtty_zsh_cwd
    return 0
}

typeset -ga preexec_functions precmd_functions chpwd_functions
preexec_functions=(${preexec_functions:#__toiqtty_zsh_preexec} __toiqtty_zsh_preexec)
precmd_functions=(__toiqtty_zsh_finish ${${precmd_functions:#__toiqtty_zsh_finish}:#__toiqtty_zsh_precmd} __toiqtty_zsh_precmd)
chpwd_functions=(${chpwd_functions:#__toiqtty_zsh_chpwd} __toiqtty_zsh_chpwd)
