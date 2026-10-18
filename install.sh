#!/usr/bin/env bash
set -euo pipefail

# Run as 'ubuntu' (an account allowed to use sudo), NOT as root or steam.
if [ "$(id -u)" -eq 0 ]; then
  echo 'ERROR: run as ubuntu (not root); script uses sudo selectively.' >&2; exit 1
fi

BASE="$(cd "$(dirname "$0")" && pwd)"
GAME="/home/steam/cs2/game/csgo"
API="$GAME/addons/counterstrikesharp/api/CounterStrikeSharp.API.dll"
PLUGINS="$GAME/addons/counterstrikesharp/plugins"
BACKUP="$HOME/cs2-startvote-backup-$(date +%Y%m%d-%H%M%S)"

sudo test -f "$API" || { echo "ERROR: CounterStrikeSharp API missing: $API" >&2; exit 1; }
sudo test -f "$GAME/cfg/server.cfg" || { echo "ERROR: server.cfg missing: $GAME/cfg/server.cfg" >&2; exit 1; }
[ -f "$BASE/source/WarmupStartVote.csproj" ] || { echo "ERROR: run this script inside the extracted package" >&2; exit 1; }

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep '^10\.' >/dev/null; then
  echo 'ERROR: .NET 10 SDK is not installed. Run: sudo apt update && sudo apt install -y dotnet-sdk-10.0' >&2
  exit 1
fi

# Ubuntu typically cannot traverse /home/steam, so copy the API assembly
# as a local BUILD-ONLY reference. It will not be deployed with the plugin.
mkdir -p "$BASE/source/lib"
sudo cp "$API" "$BASE/source/lib/CounterStrikeSharp.API.dll"
sudo chown "$(id -un):$(id -gn)" "$BASE/source/lib/CounterStrikeSharp.API.dll"
chmod 644 "$BASE/source/lib/CounterStrikeSharp.API.dll"

echo '[1/5] Building plugin...'
dotnet build "$BASE/source/WarmupStartVote.csproj" --configuration Release --nologo
DLL="$BASE/source/bin/Release/net10.0/WarmupStartVote.dll"
[ -s "$DLL" ] || { echo 'ERROR: build did not generate DLL'; exit 1; }

mkdir -p "$BACKUP"
echo "[2/5] Backing up existing cfg files and plugin DLL to $BACKUP ..."
for name in server.cfg practice.cfg knife.cfg live.cfg; do
  if sudo test -f "$GAME/cfg/$name"; then sudo cp "$GAME/cfg/$name" "$BACKUP/$name"; fi
done
if sudo test -f "$PLUGINS/WarmupStartVote/WarmupStartVote.dll"; then
  sudo cp "$PLUGINS/WarmupStartVote/WarmupStartVote.dll" "$BACKUP/WarmupStartVote.dll"
fi

# Downtime starts only after a successful build.
echo '[3/5] Stopping CS2 and deploying...'
sudo systemctl stop cs2
# If any deploy step fails, don't leave the server down.
trap 'echo "ERROR: deploy failed; restarting CS2. Backups: $BACKUP" >&2; sudo systemctl start cs2 || true' ERR
sudo install -d -o steam -g steam "$PLUGINS/WarmupStartVote"
sudo install -m 644 -o steam -g steam "$DLL" "$PLUGINS/WarmupStartVote/WarmupStartVote.dll"
sudo install -m 644 -o steam -g steam "$BASE/configs/practice.cfg" "$GAME/cfg/practice.cfg"
sudo install -m 644 -o steam -g steam "$BASE/configs/live.cfg" "$GAME/cfg/live.cfg"
sudo install -m 644 -o steam -g steam "$BASE/configs/knife.cfg" "$GAME/cfg/knife.cfg"

# Preserve user's existing server.cfg, append exactly once.
if ! sudo grep -Eq '^[[:space:]]*exec[[:space:]]+practice\.cfg([[:space:]]*(//.*)?)?$' "$GAME/cfg/server.cfg"; then
  printf '\n// Start in infinite free warmup. (WarmupStartVote)\nexec practice.cfg\n' | sudo tee -a "$GAME/cfg/server.cfg" >/dev/null
fi

trap - ERR
echo '[4/5] Starting CS2...'
sudo systemctl start cs2

echo '[5/5] Deployment complete.'
echo "Backups: $BACKUP"
echo 'Check: sudo systemctl status cs2 --no-pager'
echo "Check plugin: sudo journalctl -u cs2 -n 150 --no-pager | grep -Ei 'Warmup Start Vote|WarmupStartVote|error|exception'"
echo 'In game: !votestart -> !yes -> knife -> winning team !stay/!swap -> live.'
