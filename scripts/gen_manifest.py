#!/usr/bin/env python3
"""Generates the Jellyfin plugin repository manifest for a release.

Replaces the hand-maintained placeholder ``manifest.json`` with one that points at
real release assets and carries their MD5 checksums (the catalog spec uses MD5, which
is what ``jprm`` and every public repository emit).

Usage:
    python scripts/gen_manifest.py \
        --version 1.0.0.0 \
        --base-url https://github.com/sabinM1/Jellyfin-MultiLyrics-Plugin/releases/download/v1.0.0.0 \
        --output manifest.json \
        artifacts/multi-lyrics_1.0.0.0_jf10.zip=10.11.0.0 \
        artifacts/multi-lyrics_1.0.0.0_jf12.zip=12.0.0.0

Each positional argument is ``<zip path>`` or ``<zip path>=<targetAbi>``. When the ABI
is omitted it is inferred from the ``_jf10`` / ``_jf12`` file name suffix.

``--merge-existing`` keeps the version entries of a previously published manifest, so the
catalog accumulates a release history instead of showing only the newest version.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover - CI installs pyyaml explicitly
    sys.exit("pyyaml is required: pip install pyyaml")

# Mirrors the two build targets declared in build.yaml / the csproj.
TARGET_ABI_BY_SUFFIX = {"jf10": "10.11.0.0", "jf12": "12.0.0.0"}


def md5(path: Path) -> str:
    digest = hashlib.md5()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 16), b""):
            digest.update(chunk)
    return digest.hexdigest()


def infer_target_abi(path: Path) -> str:
    stem = path.stem.lower()
    for suffix, abi in TARGET_ABI_BY_SUFFIX.items():
        if suffix in stem:
            return abi
    raise SystemExit(f"error: cannot infer targetAbi for {path}; pass 'path=abi'")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifacts", nargs="+", help="zip path, optionally suffixed with '=<targetAbi>'")
    parser.add_argument("--version", required=True, help="Plugin version, e.g. 1.0.0.0")
    parser.add_argument("--base-url", required=True, help="Release download URL without a trailing slash")
    parser.add_argument("--changelog", default="", help="Release notes shown in the catalog")
    parser.add_argument("--output", type=Path, default=Path("manifest.json"))
    parser.add_argument(
        "--merge-existing",
        type=Path,
        default=None,
        help="Existing manifest whose version entries are kept, so the catalog keeps its release history.",
    )
    parser.add_argument(
        "--plugin-dir",
        type=Path,
        default=Path(__file__).resolve().parent.parent / "Jellyfin.Plugin.MultiLyrics",
        help="Folder holding build.yaml (plugin metadata).",
    )
    args = parser.parse_args()

    meta = yaml.safe_load((args.plugin_dir / "build.yaml").read_text(encoding="utf-8")) or {}
    base_url = args.base_url.rstrip("/")
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    versions = []
    for spec in args.artifacts:
        raw_path, _, abi = spec.partition("=")
        zip_path = Path(raw_path)
        if not zip_path.is_file():
            print(f"error: artifact not found: {zip_path}", file=sys.stderr)
            return 1

        with zipfile.ZipFile(zip_path) as archive:
            names = archive.namelist()
        if "Jellyfin.Plugin.MultiLyrics.dll" not in names:
            print(f"error: {zip_path} does not contain the plugin DLL", file=sys.stderr)
            return 1

        versions.append(
            {
                "version": args.version,
                "changelog": args.changelog or f"Release {args.version}",
                "targetAbi": abi or infer_target_abi(zip_path),
                "sourceUrl": f"{base_url}/{zip_path.name}",
                "checksum": md5(zip_path),
                "timestamp": timestamp,
            }
        )

    manifest = [
        {
            "guid": meta.get("guid", ""),
            "name": meta.get("name", ""),
            "description": " ".join(str(meta.get("description", "")).split()),
            "overview": " ".join(str(meta.get("overview", "")).split()),
            "owner": meta.get("owner", ""),
            "category": meta.get("category", "Metadata"),
            "imageUrl": "",
            "versions": versions,
        }
    ]

    if args.merge_existing is not None:
        versions.extend(load_previous_versions(args.merge_existing, str(meta.get("guid", "")), versions))

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {args.output} with {len(versions)} version entry(ies)")
    for entry in versions:
        print(f"  {entry['version']} (ABI {entry['targetAbi']}): {entry['sourceUrl']}")
    return 0


def load_previous_versions(path: Path, guid: str, new_entries: list[dict]) -> list[dict]:
    """Return the version entries of an existing manifest, minus the ones just generated.

    Keeping them is what makes the catalog show a version history instead of only the newest
    release; Jellyfin itself picks the highest version matching the server's ABI.
    """
    if not path.is_file():
        return []

    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError) as exc:
        print(f"warning: ignoring unreadable merge manifest {path}: {exc}", file=sys.stderr)
        return []

    entries = data if isinstance(data, list) else [data]
    plugin = next(
        (e for e in entries if str(e.get("guid", "")).lower() == guid.lower()),
        None,
    )
    if plugin is None:
        print(f"warning: {path} does not list plugin {guid}; history not merged", file=sys.stderr)
        return []

    replaced = {(str(v.get("version", "")), str(v.get("targetAbi", ""))) for v in new_entries}
    kept = []
    for entry in plugin.get("versions") or []:
        key = (str(entry.get("version", "")), str(entry.get("targetAbi", "")))
        if key in replaced or not entry.get("version") or not entry.get("sourceUrl"):
            continue
        kept.append(entry)

    if kept:
        print(f"kept {len(kept)} previous version entry(ies) from {path}")
    return kept


if __name__ == "__main__":
    raise SystemExit(main())
