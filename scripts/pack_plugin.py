#!/usr/bin/env python3
"""Packages a Jellyfin plugin from a dotnet publish output.

The zip layout is flat (files at the archive root), which is what Jellyfin
extracts into ``config/plugins/<name>_<version>/``.

Usage:
    python scripts/pack_plugin.py --publish-dir artifacts/publish-jf10 \
        --output artifacts/multi-lyrics_1.0.0.0_jf10.zip
"""

from __future__ import annotations

import argparse
import hashlib
import sys
import zipfile
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover - CI installs pyyaml explicitly
    sys.exit("pyyaml is required: pip install pyyaml")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 16), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-dir", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--plugin-dir",
        type=Path,
        default=Path(__file__).resolve().parent.parent / "Jellyfin.Plugin.MultiLyrics",
        help="Folder holding build.yaml (artifact list).",
    )
    args = parser.parse_args()

    build_yaml = args.plugin_dir / "build.yaml"
    meta = yaml.safe_load(build_yaml.read_text(encoding="utf-8")) or {}
    artifacts = [str(name) for name in (meta.get("artifacts") or [])]
    if not artifacts:
        print(f"error: {build_yaml} lists no artifacts", file=sys.stderr)
        return 1

    publish_dir: Path = args.publish_dir
    if not publish_dir.is_dir():
        print(f"error: publish dir not found: {publish_dir}", file=sys.stderr)
        return 1

    files: list[Path] = []
    missing: list[str] = []
    for name in artifacts:
        candidate = publish_dir / Path(name).name
        if candidate.is_file():
            files.append(candidate)
        else:
            missing.append(name)

    if missing:
        print(f"error: missing artifacts in {publish_dir}: {', '.join(missing)}", file=sys.stderr)
        return 1

    args.output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.output, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in files:
            archive.write(path, arcname=path.name)

    with zipfile.ZipFile(args.output) as archive:
        names = archive.namelist()

    print(f"packed {len(names)} file(s) into {args.output}")
    for name in names:
        print(f"  {name}")
    print(f"sha256 {sha256(args.output)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
