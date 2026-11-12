#!/bin/bash
# Snowdrift for macOS: install or update with one command (Terminal):
#
#   curl -fsSL https://raw.githubusercontent.com/Ayanokoji771/snowdrift/main/install.sh | bash
#
# Downloads the latest release from GitHub, checks it against the release's SHA-256 checksums and
# its code signature, puts Snowdrift.app where it already lives (or in /Applications) and opens it.
# Settings are kept. SNOWDRIFT_DIR=folder installs there; SNOWDRIFT_NO_START=1 skips opening it.
set -euo pipefail

REPO="Ayanokoji771/snowdrift"
ASSET="Snowdrift-macOS.zip"
FILES="https://github.com/$REPO/releases/latest/download"

ru=false
[[ "$(defaults read -g AppleLanguages 2>/dev/null | sed -n 2p)" == *ru* ]] && ru=true
say() { if $ru; then echo "$1"; else echo "$2"; fi; }

# Where: the running copy, an installed copy, or /Applications (~/Applications without rights).
target=""
if [ -n "${SNOWDRIFT_DIR:-}" ]; then
  target="$SNOWDRIFT_DIR/Snowdrift.app"
else
  pid="$(pgrep -x Snowdrift | head -1 || true)"
  if [ -n "$pid" ]; then
    exe="$(ps -o comm= -p "$pid" || true)"
    case "$exe" in */Contents/MacOS/Snowdrift) target="${exe%/Contents/MacOS/Snowdrift}" ;; esac
  fi
  if [ -z "$target" ]; then
    for d in /Applications "$HOME/Applications"; do
      if [ -d "$d/Snowdrift.app" ]; then target="$d/Snowdrift.app"; break; fi
    done
  fi
  if [ -z "$target" ]; then
    if [ -w /Applications ]; then target="/Applications/Snowdrift.app"; else target="$HOME/Applications/Snowdrift.app"; fi
  fi
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

say "Скачиваю Snowdrift..." "Downloading Snowdrift..."
curl -fsSL -o "$tmp/$ASSET" "$FILES/$ASSET"
curl -fsSL -o "$tmp/SHA256SUMS.txt" "$FILES/SHA256SUMS.txt"
expected="$(awk -v f="$ASSET" '{ n = $2; sub(/^\*/, "", n); if (n == f) print $1 }' "$tmp/SHA256SUMS.txt")"
actual="$(shasum -a 256 "$tmp/$ASSET" | awk '{ print $1 }')"
if [ -z "$expected" ] || [ "$expected" != "$actual" ]; then
  say "Контрольная сумма не совпала — ничего не изменено." "Checksum mismatch — nothing was changed."
  exit 1
fi
say "Контрольная сумма совпала." "Checksum verified."

ditto -x -k "$tmp/$ASSET" "$tmp/new"
fresh="$tmp/new/Snowdrift.app"
if ! codesign --verify --deep --strict "$fresh" 2>/dev/null; then
  say "Подпись приложения не прошла проверку — ничего не изменено." "The app's signature failed its check — nothing was changed."
  exit 1
fi
xattr -cr "$fresh" 2>/dev/null || true
version="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$fresh/Contents/Info.plist" 2>/dev/null || echo '?')"

# Close the copy being replaced (only that one).
for p in $(pgrep -x Snowdrift || true); do
  if [ "$(ps -o comm= -p "$p" || true)" = "$target/Contents/MacOS/Snowdrift" ]; then
    say "Закрываю запущенный Snowdrift..." "Closing the running Snowdrift..."
    kill "$p" 2>/dev/null || true
    for _ in $(seq 1 20); do kill -0 "$p" 2>/dev/null || break; sleep 0.25; done
  fi
done

mkdir -p "$(dirname "$target")"
rm -rf "$target"
mv "$fresh" "$target"

if [ -z "${SNOWDRIFT_NO_START:-}" ]; then open "$target"; fi
say "Готово: Snowdrift $version — $target" "Done: Snowdrift $version — $target"
say "Снежинка — в строке меню. Дальше Snowdrift обновляется сам, или запустите эту команду ещё раз." \
    "The snowflake is in the menu bar. Snowdrift updates itself from now on, or run this command again."
