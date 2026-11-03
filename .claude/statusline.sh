#!/usr/bin/env bash
# Claude Code status line - mirrors the devcontainers bash PS1 theme
# Includes: context usage, 5-hour session limit, 7-day weekly limit, reset countdowns.
# JSON is read with node (no jq on this machine).

input=$(cat)

# ---------------------------------------------------------------------------
# Extract every field in one node call, one value per line
# ---------------------------------------------------------------------------
fields=$(printf '%s' "$input" | node "$(dirname "${BASH_SOURCE[0]}")/statusline-fields.js")

{
    IFS= read -r cwd
    IFS= read -r git_branch
    IFS= read -r worktree_branch
    IFS= read -r ctx_used
    IFS= read -r five_pct
    IFS= read -r five_reset
    IFS= read -r week_pct
    IFS= read -r week_reset
} <<< "$fields"

# ---------------------------------------------------------------------------
# PS1-style prompt fields
# ---------------------------------------------------------------------------

[ -n "$worktree_branch" ] && git_branch="$worktree_branch"

# Fallback: detect git branch from the cwd directly (skip optional locks)
if [ -z "$git_branch" ] && [ -n "$cwd" ]; then
    git_branch=$(git --no-optional-locks -C "$cwd" symbolic-ref --short HEAD 2>/dev/null \
        || git --no-optional-locks -C "$cwd" rev-parse --short HEAD 2>/dev/null)
fi

# User (green)
user_part=$(printf '\033[0;32m%s\033[0m' "$(whoami)")

# Arrow (plain, no exit-code colouring available)
arrow=$(printf '\033[0m➜')

# Directory (bold blue) — shorten home prefix
if [ -n "$cwd" ]; then
    home="$HOME"
    short_cwd="${cwd/#$home/~}"
else
    short_cwd="~"
fi
dir_part=$(printf '\033[1;34m%s\033[0m' "$short_cwd")

# Git branch (cyan parens, red branch name)
branch_part=""
if [ -n "$git_branch" ]; then
    branch_part=$(printf '\033[0;36m(\033[1;31m%s\033[0;36m)\033[0m' "$git_branch")
fi

# ---------------------------------------------------------------------------
# Helper: format seconds into a human-readable countdown (e.g. "1h23m")
# ---------------------------------------------------------------------------
format_countdown() {
    local secs="$1"
    if [ -z "$secs" ] || [ "$secs" -le 0 ] 2>/dev/null; then
        echo "now"
        return
    fi
    local h=$(( secs / 3600 ))
    local m=$(( (secs % 3600) / 60 ))
    if [ "$h" -gt 0 ]; then
        printf '%dh%02dm' "$h" "$m"
    else
        printf '%dm' "$m"
    fi
}

# Helper: green <50%, yellow <80%, red >=80%
pct_color() {
    if [ "$1" -ge 80 ]; then
        printf '\033[0;31m'
    elif [ "$1" -ge 50 ]; then
        printf '\033[0;33m'
    else
        printf '\033[0;32m'
    fi
}

# Helper: "<label>:<pct>%(<countdown>)" for a rate limit
limit_part() {
    local label="$1" pct="$2" reset="$3"
    [ -z "$pct" ] && return
    local int
    int=$(printf '%.0f' "$pct")
    local str
    str=$(printf "$(pct_color "$int")%s:%d%%\033[0m" "$label" "$int")
    if [ -n "$reset" ]; then
        local now
        now=$(date +%s)
        str="${str}$(printf '\033[2;37m(%s)\033[0m' "$(format_countdown $(( ${reset%.*} - now )))")"
    fi
    printf '%s' "$str"
}

# ---------------------------------------------------------------------------
# Context window usage and rate limits
# ---------------------------------------------------------------------------
ctx_part=""
if [ -n "$ctx_used" ]; then
    ctx_int=$(printf '%.0f' "$ctx_used")
    ctx_part=$(printf "$(pct_color "$ctx_int")ctx:%d%%\033[0m" "$ctx_int")
fi
five_part=$(limit_part 5h "$five_pct" "$five_reset")
week_part=$(limit_part 7d "$week_pct" "$week_reset")

# ---------------------------------------------------------------------------
# Assemble stats block (only include parts that have data)
# ---------------------------------------------------------------------------
stats=""
for part in "$ctx_part" "$five_part" "$week_part"; do
    [ -z "$part" ] && continue
    if [ -z "$stats" ]; then
        stats="$part"
    else
        stats="${stats}$(printf '\033[2;37m|\033[0m')${part}"
    fi
done

stats_block=""
if [ -n "$stats" ]; then
    stats_block=$(printf ' \033[2;37m[\033[0m%s\033[2;37m]\033[0m' "$stats")
fi

# ---------------------------------------------------------------------------
# Final output — single line
# ---------------------------------------------------------------------------
printf '%s %s %s' "$user_part" "$arrow" "$dir_part"
[ -n "$branch_part" ] && printf ' %s' "$branch_part"
[ -n "$stats_block" ] && printf '%s' "$stats_block"
printf '\n'
