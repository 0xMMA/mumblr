#!/usr/bin/env bash
# Puts an API key where every new shell picks it up.
#
# Linux has no `setx`: there is no OS-level store the kernel injects into new
# processes, only inheritance from the parent. So the key goes into one file
# with tight permissions, and the shell rc files learn to read it.
#
#   ./scripts/set-key.sh                    # ELEVENLABS_API_KEY
#   ./scripts/set-key.sh ANTHROPIC_API_KEY  # anything else
set -euo pipefail
umask 077

VAR="${1:-ELEVENLABS_API_KEY}"
ENV_FILE="$HOME/.config/mumblr/env"
SOURCE_LINE="[ -f $ENV_FILE ] && . $ENV_FILE"

read -rsp "$VAR: " key; echo
[ -n "$key" ] || { echo "empty input, nothing written" >&2; exit 1; }

mkdir -p "$(dirname "$ENV_FILE")"
touch "$ENV_FILE"

# Replace this variable's line, keep every other key in the file.
{ grep -v "^export $VAR=" "$ENV_FILE" || true; } > "$ENV_FILE.tmp"
printf 'export %s=%q\n' "$VAR" "$key" >> "$ENV_FILE.tmp"
mv "$ENV_FILE.tmp" "$ENV_FILE"
chmod 600 "$ENV_FILE"

for rc in "$HOME/.bashrc" "$HOME/.profile"; do
    [ -f "$rc" ] || continue
    grep -qxF "$SOURCE_LINE" "$rc" || printf '\n# API keys for local development\n%s\n' "$SOURCE_LINE" >> "$rc"
done

echo "$VAR stored in $ENV_FILE (${#key} chars, mode 600)"

# A key that was mistyped costs an hour of debugging the wrong layer, so ask.
if [ "$VAR" = ELEVENLABS_API_KEY ] && command -v curl >/dev/null; then
    code=$(curl -sS -o /dev/null -w '%{http_code}' -H "xi-api-key: $key" \
        https://api.elevenlabs.io/v1/user || echo 000)
    case "$code" in
        200) echo "ElevenLabs accepted the key" ;;
        401) echo "ElevenLabs rejected the key (401) - it is stored, but it is wrong" >&2 ;;
        *)   echo "could not verify (HTTP $code)" >&2 ;;
    esac
fi

echo "Open a new shell, or run: . $ENV_FILE"
