#!/usr/bin/env bash
# Builds the plugin and stages it in ./plugin, which docker-compose.yml mounts into the
# Jellyfin container at /config/plugins/MultiLyrics.
#
#   ./install_plugin.sh                 # Jellyfin 10.11 / net9.0
#   JELLYFIN_TARGET=jf12 ./install_plugin.sh
#
# Prereqs: .NET SDK matching the target line (9.x for jf10, 10.x for jf12).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
target="${JELLYFIN_TARGET:-jf10}"

publish_dir="$here/publish"
plugin_dir="$here/plugin"

echo "== publishing plugin ($target) =="
dotnet publish "$root/Jellyfin.Plugin.MultiLyrics" -c Release -p:JellyfinTarget="$target" -o "$publish_dir"

rm -rf "$plugin_dir"
mkdir -p "$plugin_dir"

# Only what the plugin zip would contain: the plugin assembly and its third-party
# dependency. Everything else in the publish output belongs to the Jellyfin host.
cp "$publish_dir/Jellyfin.Plugin.MultiLyrics.dll" "$plugin_dir/"
cp "$publish_dir/HtmlAgilityPack.dll" "$plugin_dir/"

echo "== staged plugin files =="
ls -l "$plugin_dir"
