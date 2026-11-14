#!/usr/bin/env bash
# End-to-end integration test against a real Jellyfin container (see docker-compose.yml).
#
# Prereqs: docker + docker compose, seed_music.sh already run, and the plugin staged by
# ./install_plugin.sh (copied into ./plugin/, which docker-compose.yml mounts into the
# container at /config/plugins/MultiLyrics).
#
# Steps performed:
#   1. wait for Jellyfin to come up
#   2. complete first-time setup via API (admin user)
#   3. create an API key
#   4. add the music library + scan
#   5. verify the plugin is loaded
#   6. run the Multi-Lyrics scheduled task
#   7. check sidecars + GET /Audio/{id}/Lyrics + status endpoint
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8096}"
ADMIN_USER="${ADMIN_USER:-admin}"
ADMIN_PASS="${ADMIN_PASS:-integration-test-pass}"

pass=0
fail=0

check() {
  local name="$1" cond="$2"
  if eval "$cond"; then
    echo "PASS  $name"; pass=$((pass+1))
  else
    echo "FAIL  $name"; fail=$((fail+1))
  fi
}

echo "== waiting for Jellyfin =="
for i in $(seq 1 60); do
  if curl -sf "$BASE_URL/System/Info/Public" >/dev/null 2>&1; then break; fi
  sleep 5
done
curl -sf "$BASE_URL/System/Info/Public" >/dev/null || { echo "Jellyfin did not come up"; exit 1; }

echo "== first-time setup =="
# Create the admin user via the startup wizard flow (10.11 startup endpoints).
curl -sf -X POST "$BASE_URL/Startup/Configuration" \
  -H 'Content-Type: application/json' \
  -d '{"UICulture":"en-US","MetadataCountryCode":"us","PreferredMetadataLanguage":"en"}' >/dev/null || true
curl -sf -X POST "$BASE_URL/Startup/User" \
  -H 'Content-Type: application/json' \
  -d "{\"Name\":\"${ADMIN_USER}\",\"Password\":\"${ADMIN_PASS}\"}" >/dev/null
curl -sf -X POST "$BASE_URL/Startup/Complete" >/dev/null

echo "== authenticate =="
AUTH=$(curl -sf -X POST "$BASE_URL/Users/AuthenticateByName" \
  -H 'Content-Type: application/json' \
  -H 'X-Emby-Authorization: MediaBrowser Client="it", Device="it", DeviceId="it", Version="1.0"' \
  -d "{\"Username\":\"${ADMIN_USER}\",\"Pw\":\"${ADMIN_PASS}\"}")
TOKEN=$(echo "$AUTH" | python3 -c 'import sys,json; print(json.load(sys.stdin)["AccessToken"])')
AUTHZ="Authorization: MediaBrowser Token=${TOKEN}"
echo "authenticated."

echo "== create API key =="
APIKEY=$(curl -sf -X POST "$BASE_URL/Auth/Keys/New?app=itest" -H "$AUTHZ" | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d.get("AccessToken") or d["AuthKeys"][-1]["AccessToken"])')

APIH="X-Emby-Token: ${APIKEY}"

echo "== add music library + scan =="
curl -sf -X POST "$BASE_URL/Library/VirtualFolders?name=Music&collectionType=music&paths=/media/music&refreshLibrary=true" \
  -H "$AUTHZ" >/dev/null || \
curl -sf -X POST "$BASE_URL/Library/VirtualFolders" -H "$AUTHZ" -H 'Content-Type: application/json' \
  -d '{"name":"Music","collectionType":"music","paths":["/media/music"],"refreshLibrary":true}' >/dev/null

for i in $(seq 1 30); do
  ITEMS=$(curl -sf "$BASE_URL/Items?IncludeItemTypes=Audio&Recursive=true&Limit=10" -H "$APIH" || echo "")
  COUNT=$(echo "$ITEMS" | python3 -c 'import sys,json; print(json.load(sys.stdin).get("TotalRecordCount", 0))' 2>/dev/null || echo 0)
  [ "$COUNT" -ge 5 ] && break
  sleep 5
done
check "5 audio items indexed" "[ \"$COUNT\" -ge 5 ]"

echo "== verify plugin loaded =="
PLUGINS=$(curl -sf "$BASE_URL/Plugins" -H "$APIH")
check "Multi-Lyrics plugin present" "echo '$PLUGINS' | grep -qi 'Multi-Lyrics'"

echo "== run scheduled task =="
TASK_ID=$(curl -sf "$BASE_URL/ScheduledTasks" -H "$APIH" | python3 -c '
import sys,json
for t in json.load(sys.stdin):
    if t.get("Key") == "MultiLyricsFetchLyrics":
        print(t["Id"]); break')
check "task discovered" "[ -n '${TASK_ID:-}' ]"
curl -sf -X POST "$BASE_URL/ScheduledTasks/Running/${TASK_ID}" -H "$APIH" >/dev/null
for i in $(seq 1 60); do
  STATE=$(curl -sf "$BASE_URL/ScheduledTasks?hidden=false" -H "$APIH" | python3 -c "
import sys,json
for t in json.load(sys.stdin):
    if t.get('Key') == 'MultiLyricsFetchLyrics':
        print(t['State']); break" 2>/dev/null || echo "")
  [ "$STATE" = "Idle" ] && break
  sleep 5
done
check "task finished" "[ '$STATE' = 'Idle' ]"

echo "== per-track expectations =="
get_item() {
  curl -sf "$BASE_URL/Items?IncludeItemTypes=Audio&Recursive=true&SearchTerm=$1" -H "$APIH" | \
    python3 -c 'import sys,json; items=json.load(sys.stdin).get("Items",[]); print(items[0]["Id"] if items else "")'
}
IT1=$(get_item "Never%20Gonna%20Give%20You%20Up")
IT3=$(get_item "Nonexistent%20Song%20XYZ")
IT4=$(get_item "Yellow")

check "track1 id found" "[ -n '${IT1:-}' ]"

# 1) synced sidecar for the LRCLIB track
check "track1 .lrc sidecar" "[ -f ./music/01_never_gonna_give_you_up.lrc ]"
check "track1 synced status" \
  "curl -sf '$BASE_URL/MultiLyrics/Status/$IT1' -H '$APIH' | grep -q '\"Synced\"'"

# 3) unknown track: miss recorded, retry scheduled
check "track3 retry scheduled" \
  "curl -sf '$BASE_URL/MultiLyrics/Status/$IT3' -H '$APIH' | grep -q 'RetryAfterUtc'"

# 4) plain sidecar (.txt) unless alignment upgraded it to .lrc
check "track4 plain or aligned sidecar" \
  "[ -f ./music/04_yellow.txt ] || [ -f ./music/04_yellow.lrc ]"

# Jellyfin serves lyrics
check "GET /Audio/{id}/Lyrics returns lyrics" \
  "curl -sf '$BASE_URL/Audio/$IT1/Lyrics' -H '$APIH' | python3 -c 'import sys,json; d=json.load(sys.stdin); sys.exit(0 if d.get(\"Lyrics\") else 1)'"

# Manual override: force a specific source
check "manual override endpoint responds" \
  "curl -sf -X POST '$BASE_URL/MultiLyrics/Search/$IT1?source=LyricsOvh' -H '$APIH' >/dev/null"

echo "== summary: $pass passed, $fail failed =="
exit $fail
