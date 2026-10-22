# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Toiqtty contributors
# May also be sourced manually, after the user's prompt setup.
[[ $- == *i* && -t 1 ]] || return 0
(( BASH_VERSINFO[0] > 5 || (BASH_VERSINFO[0] == 5 && BASH_VERSINFO[1] >= 1) )) || return 0
for __toiqtty_check in PROMPT_COMMAND PS0 PS1 PS2; do
    if [[ $(builtin declare -p "$__toiqtty_check" 2>/dev/null) =~ ^declare[[:space:]]+-[^[:space:]]*r ]]; then
        unset __toiqtty_check
        return 0
    fi
done
unset __toiqtty_check
if [[ ${__toiqtty_bash_pid-} != "$$" ]]; then __toiqtty_prompt_seen=0; fi
__toiqtty_bash_pid=$$

__toiqtty_bash_uri() {
    local LC_ALL=C input=$1 byte hex
    while [[ -n $input ]]; do
        byte=${input:0:1}; input=${input:1}
        case $byte in
            [a-zA-Z0-9.~_/-]) builtin printf '%s' "$byte" ;;
            *) builtin printf -v hex '%02X' "'$byte"; builtin printf '%%%s' "$hex" ;;
        esac
    done
}

__toiqtty_bash_cwd() {
    local directory=$PWD host=${HOSTNAME-}
    case ${OSTYPE-} in
        msys*|cygwin*)
            if command -v cygpath >/dev/null 2>&1; then
                directory=$(command cygpath -am -- "$directory") || return 0
                directory=/$directory
            fi ;;
    esac
    host=${host//[^a-zA-Z0-9._-]/_}
    builtin printf '\033]7;file://%s%s%s\007' "$host" "$(__toiqtty_bash_uri "$directory")" "${TOIQTTY_CWD_TOKEN:+?toiqtty-cwd=$TOIQTTY_CWD_TOKEN}"
}

__toiqtty_bash_finish() {
    # Bash 5.1+ supplies the original command status to each PROMPT_COMMAND
    # array entry. Returning success here leaves later user hooks runnable.
    local code=$?
    [[ -t 1 ]] || return 0
    if (( __toiqtty_prompt_seen )); then builtin printf '\033]133;D;%d\007' "$code"; fi
    __toiqtty_prompt_seen=1
    return 0
}

__toiqtty_bash_prompt() {
    local start=$'\[\e]133;P;k=i\a\]' end=$'\[\e]133;B\a\]'
    local secondary=$'\[\e]133;P;k=s\a\]' execute=$'\e]133;C\a'
    [[ -t 1 ]] || return 0
    # A framework may replace PS1/PS2/PS0 in its prompt hook. Add one wrapper
    # after those hooks, retaining prompt escapes and nonprinting spans.
    [[ ${PS1-} != "$start"* ]] || PS1=${PS1#"$start"}
    [[ ${PS1-} != *"$end" ]] || PS1=${PS1%"$end"}
    PS1=$start${PS1-}$end
    [[ ${PS2-} != "$secondary"* ]] || PS2=${PS2#"$secondary"}
    [[ ${PS2-} != *"$end" ]] || PS2=${PS2%"$end"}
    PS2=$secondary${PS2-}$end
    [[ ${PS0-} == "$execute"* ]] || PS0=$execute${PS0-}
    __toiqtty_bash_cwd
    # P in the prompt is redraw-safe; emit A only once before a fresh prompt.
    builtin printf '\033]133;A\007'
    return 0
}

# Copy scalar and array values without evaluating them or touching history.
# The shell evaluates each user entry in its normal prompt context.
__toiqtty_user_prompt=()
for __toiqtty_entry in "${PROMPT_COMMAND[@]}"; do
    case $__toiqtty_entry in __toiqtty_bash_finish|__toiqtty_bash_prompt) continue ;; esac
    __toiqtty_user_prompt+=("$__toiqtty_entry")
done
PROMPT_COMMAND=(__toiqtty_bash_finish "${__toiqtty_user_prompt[@]}" __toiqtty_bash_prompt)
unset __toiqtty_user_prompt __toiqtty_entry
