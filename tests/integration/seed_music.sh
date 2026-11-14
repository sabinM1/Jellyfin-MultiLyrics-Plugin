#!/usr/bin/env bash
# Seeds a small test library of 5 tracks with synthetic audio (public-domain sine tones)
# and real-world metadata. No copyrighted audio is used; the metadata strings are only
# sent to lyric sources as search keys.
#
# Track matrix:
#  1. Rick Astley - Never Gonna Give You Up   (on LRCLIB, synced)  -> expect .lrc
#  2. New Order - Blue Monday                 (on LRCLIB, synced)  -> expect .lrc
#  3. Zxqw Blorptastic - Nonexistent Song XYZ (not on LRCLIB; lyrics.ovh miss) -> expect miss/retry
#  4. Coldplay - Yellow                       (plain on lyrics.ovh) -> expect .txt (or .lrc if aligned)
#  5. Rick Astley - Together Forever          (on LRCLIB)          -> expect .lrc
set -euo pipefail

OUT_DIR="$(cd "$(dirname "$0")" && pwd)/music"
mkdir -p "$OUT_DIR"

seed() {
  local file="$1" artist="$2" title="$3" album="$4" year="$5" dur="$6"
  ffmpeg -y -hide_banner -loglevel error \
    -f lavfi -i "sine=frequency=440:duration=${dur}" \
    -metadata "artist=${artist}" \
    -metadata "title=${title}" \
    -metadata "album=${album}" \
    -metadata "date=${year}" \
    -c:a flac "${OUT_DIR}/${file}"
  echo "seeded ${file} (${artist} - ${title})"
}

seed "01_never_gonna_give_you_up.flac" "Rick Astley" "Never Gonna Give You Up" "Whenever You Need Somebody" 1987 213
seed "02_blue_monday.flac" "New Order" "Blue Monday" "Power, Corruption & Lies" 1983 446
seed "03_zxqw_blorptastic.flac" "Zxqw Blorptastic" "Nonexistent Song XYZ" "Nothing" 2026 180
seed "04_yellow.flac" "Coldplay" "Yellow" "Parachutes" 2000 269
seed "05_together_forever.flac" "Rick Astley" "Together Forever" "Whenever You Need Somebody" 1987 210

echo "Library seeded at ${OUT_DIR}"
