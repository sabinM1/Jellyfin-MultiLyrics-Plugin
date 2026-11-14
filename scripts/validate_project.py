#!/usr/bin/env python3
"""Static validation for the Multi-Lyrics plugin repository.

Runs the checks CI can perform without a Jellyfin server: build metadata
consistency, packaging inputs, legal defaults and a placeholder/secret scan.

Usage:
    python scripts/validate_project.py [--allow-placeholder-manifest] [--publish-dir DIR]

Exit code 0 when every check passes, 1 otherwise.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover - CI installs pyyaml explicitly
    sys.exit("pyyaml is required: pip install pyyaml")

GUID_RE = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+(\.\d+)?$")
PLACEHOLDER_GUIDS = {"00000000-0000-0000-0000-000000000000"}
PLUGIN_DIR = "Jellyfin.Plugin.MultiLyrics"
TESTS_DIR = "Jellyfin.Plugin.MultiLyrics.Tests"

# Assemblies a plugin folder must never ship: the Jellyfin host and the shared
# framework already provide them, and a local copy can win over the host's.
# The plugin's own assembly is excluded explicitly (it starts with "Jellyfin.").
HOST_ASSEMBLY_PREFIXES = ("Jellyfin.Common", "Jellyfin.Controller", "Jellyfin.Model", "Jellyfin.Api")
SHARED_ASSEMBLY_PREFIXES = ("MediaBrowser.", "Microsoft.", "System.", "netstandard")
PLUGIN_ASSEMBLY = "Jellyfin.Plugin.MultiLyrics.dll"

SECRET_PATTERNS = [
    (re.compile(r"\bAIza[0-9A-Za-z_\-]{35}\b"), "Google API key"),
    (re.compile(r"\bghp_[0-9A-Za-z]{36}\b"), "GitHub personal access token"),
    (re.compile(r"\bgithub_pat_[0-9A-Za-z_]{40,}\b"), "GitHub fine-grained token"),
    (re.compile(r"\bxox[baprs]-[0-9A-Za-z\-]{10,}\b"), "Slack token"),
    (re.compile(r"\bsk-[A-Za-z0-9]{32,}\b"), "OpenAI-style secret key"),
    (re.compile(r"(?i)\b(?:token|api_?key|secret)\s*[=:]\s*\"[A-Za-z0-9_\-]{24,}\""), "hard-coded credential"),
]


class Report:
    """Collects check outcomes and renders them."""

    def __init__(self) -> None:
        self.rows: list[tuple[str, str, str]] = []

    def ok(self, check: str, detail: str) -> None:
        self.rows.append(("PASS", check, detail))

    def fail(self, check: str, detail: str) -> None:
        self.rows.append(("FAIL", check, detail))

    def warn(self, check: str, detail: str) -> None:
        self.rows.append(("WARN", check, detail))

    def check(self, condition: bool, check: str, detail: str, *, warning_only: bool = False) -> bool:
        if condition:
            self.ok(check, detail)
        elif warning_only:
            self.warn(check, detail)
        else:
            self.fail(check, detail)
        return condition

    @property
    def failed(self) -> bool:
        return any(status == "FAIL" for status, _, _ in self.rows)

    def render(self) -> str:
        width = max((len(c) for _, c, _ in self.rows), default=10)
        lines = [f"{s:<4}  {c:<{width}}  {d}" for s, c, d in self.rows]
        counts = {k: sum(1 for r in self.rows if r[0] == k) for k in ("PASS", "WARN", "FAIL")}
        lines.append("")
        lines.append(f"summary: {counts['PASS']} passed, {counts['WARN']} warnings, {counts['FAIL']} failed")
        return "\n".join(lines)

    def render_markdown(self) -> str:
        lines = ["| Status | Check | Detail |", "| --- | --- | --- |"]
        lines += [f"| {s} | {c} | {d} |" for s, c, d in self.rows]
        return "\n".join(lines)


def load_yaml(path: Path) -> dict:
    with path.open(encoding="utf-8") as handle:
        return yaml.safe_load(handle) or {}


def check_build_metadata(root: Path, report: Report) -> dict:
    """A1/A6/A7: build.yaml shape, GUIDs and version metadata."""
    build_yaml_path = root / PLUGIN_DIR / "build.yaml"
    if not report.check(build_yaml_path.is_file(), "build.yaml exists", str(build_yaml_path)):
        return {}

    meta = load_yaml(build_yaml_path)
    for key in ("name", "guid", "version", "targetAbi", "framework", "artifacts"):
        report.check(key in meta and meta[key] not in (None, "", []), f"build.yaml has {key}", "present")

    guid = str(meta.get("guid", ""))
    report.check(bool(GUID_RE.match(guid)), "build.yaml GUID is a valid UUID", guid or "<missing>")
    report.check(guid not in PLACEHOLDER_GUIDS, "build.yaml GUID is not a placeholder", guid or "<missing>")

    version = str(meta.get("version", ""))
    report.check(bool(VERSION_RE.match(version)), "build.yaml version is numeric", version or "<missing>")

    abi = str(meta.get("targetAbi", ""))
    report.check(bool(VERSION_RE.match(abi)), "build.yaml targetAbi is numeric", abi or "<missing>")

    framework = str(meta.get("framework", ""))
    report.check(
        framework in {"net8.0", "net9.0", "net10.0"},
        "build.yaml framework is a supported TFM",
        framework or "<missing>",
    )

    artifacts = meta.get("artifacts") or []
    report.check(bool(artifacts), "build.yaml lists artifacts", ", ".join(map(str, artifacts)) or "<none>")

    # The plugin GUID is duplicated in Constants.cs; a mismatch means the host and
    # the repository catalog disagree about the plugin identity.
    constants = (root / PLUGIN_DIR / "Constants.cs").read_text(encoding="utf-8")
    match = re.search(r'PluginGuid\s*=\s*"([^"]+)"', constants)
    report.check(
        match is not None and match.group(1).lower() == guid.lower(),
        "Constants.PluginGuid matches build.yaml",
        match.group(1) if match else "<missing>",
    )

    return meta


def check_project(root: Path, meta: dict, report: Report) -> None:
    """A2/A3/A5: csproj target, Jellyfin references, embedded config page."""
    csproj_path = root / PLUGIN_DIR / f"{PLUGIN_DIR}.csproj"
    if not report.check(csproj_path.is_file(), "plugin csproj exists", str(csproj_path)):
        return

    csproj = csproj_path.read_text(encoding="utf-8")

    # Default target (no property override) must match build.yaml's framework.
    default_tfm = re.search(r"<TargetFramework>(net[0-9.]+)</TargetFramework>", csproj)
    report.check(
        default_tfm is not None and default_tfm.group(1) == meta.get("framework"),
        "default TargetFramework matches build.yaml framework",
        default_tfm.group(1) if default_tfm else "<missing>",
    )

    # The plugin logs its own assembly version at startup. Jellyfin logs the installed
    # catalog version, so a drift here shows up as two different versions in one log.
    csproj_version = re.search(r"<Version>([^<]+)</Version>", csproj)
    report.check(
        csproj_version is not None and csproj_version.group(1).strip() == str(meta.get("version")),
        "csproj Version matches build.yaml version",
        csproj_version.group(1).strip() if csproj_version else "<missing>",
    )

    for package in ("Jellyfin.Common", "Jellyfin.Controller", "Jellyfin.Model"):
        report.check(f'Include="{package}"' in csproj, f"{package} referenced", package)

    report.check(
        "ExcludeAssets>runtime<" in csproj,
        "Jellyfin assemblies excluded from runtime output",
        "the server provides them",
    )

    report.check(
        "EmbeddedResource" in csproj and "configPage.html" in csproj,
        "configPage.html embedded as a resource",
        "Configuration/configPage.html",
    )

    plugin_cs = (root / PLUGIN_DIR / "Plugin.cs").read_text(encoding="utf-8")
    report.check(
        "Configuration.configPage.html" in plugin_cs,
        "GetPages references the embedded page",
        "Configuration.configPage.html",
    )


def check_defaults(root: Path, meta: dict, report: Report) -> None:
    """B5/I1: Genius off by default, ToS gate present."""
    config_path = root / PLUGIN_DIR / "Configuration" / "PluginConfiguration.cs"
    if not report.check(config_path.is_file(), "PluginConfiguration exists", str(config_path)):
        return

    source = config_path.read_text(encoding="utf-8")
    for prop in ("EnableGenius",):
        match = re.search(rf"{prop}\s*\{{\s*get;\s*set;\s*\}}\s*=\s*(\w+);", source)
        report.check(
            match is not None and match.group(1).lower() == "false",
            f"{prop} defaults to false",
            match.group(1) if match else "<missing>",
        )

    report.check("GeniusToSAcknowledged" in source, "Genius ToS acknowledgement flag exists", "GeniusToSAcknowledged")

    page = root / PLUGIN_DIR / "Configuration" / "configPage.html"
    if report.check(page.is_file(), "configPage.html exists", str(page)):
        html = page.read_text(encoding="utf-8")
        report.check(
            "Terms of Service" in html,
            "config page shows the Genius ToS warning",
            "Terms of Service text present",
        )
        report.check(
            'id="mlGeniusAck"' in html,
            "config page has the ToS acknowledgement checkbox",
            'id="mlGeniusAck"',
        )

        # The priority table is the only ordering UI: every source needs a slot and a toggle.
        for source in ("LrcLib", "LyricsOvh", "Tekstowo", "Genius"):
            report.check(
                f'id="mlPriority_{source}"' in html,
                f"config page exposes the priority selector for {source}",
                f'id="mlPriority_{source}"',
            )
            report.check(
                f'id="mlEnabled_{source}"' in html,
                f"config page exposes the enable toggle for {source}",
                f'id="mlEnabled_{source}"',
            )

        # Fields that were deliberately removed from the page must not creep back in.
        for removed in ("mlAlignUrl", "mlWritePolicy", "mlMaxTracks", "mlRetryDays", "mlStatusItemId"):
            report.check(
                removed not in html,
                f"removed setting stays out of the config page ({removed})",
                f"{removed} absent",
            )

        # The page is embedded in the assembly, so the build it prints must be the assembly
        # version: a mismatch would tell the user the wrong thing about the loaded plugin.
        stamp = re.search(r'data-page-version="([^"]+)"', html)
        expected = str(meta.get("version", ""))
        report.check(
            stamp is not None and bool(expected) and stamp.group(1) == expected,
            "config page build stamp matches build.yaml version",
            stamp.group(1) if stamp else "<missing>",
        )


def check_manifest(root: Path, meta: dict, report: Report, *, allow_placeholder: bool) -> None:
    """A7: repository catalog manifest is consistent with the plugin metadata."""
    manifest_path = root / "manifest.json"
    if not report.check(manifest_path.is_file(), "manifest.json exists", str(manifest_path)):
        return

    try:
        data = json.loads(manifest_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        report.fail("manifest.json is valid JSON", str(exc))
        return
    report.ok("manifest.json is valid JSON", "parsed")

    entries = data if isinstance(data, list) else [data]
    plugin = next((e for e in entries if str(e.get("guid", "")).lower() == str(meta.get("guid", "")).lower()), None)
    if not report.check(plugin is not None, "manifest lists the plugin GUID", str(meta.get("guid"))):
        return

    versions = plugin.get("versions") or []
    report.check(bool(versions), "manifest has at least one version", f"{len(versions)} entry(ies)")

    for entry in versions:
        version = str(entry.get("version", ""))
        source_url = str(entry.get("sourceUrl", ""))
        checksum = str(entry.get("checksum", ""))
        report.check(bool(VERSION_RE.match(version)), "manifest version is numeric", version or "<missing>")
        report.check(
            bool(VERSION_RE.match(str(entry.get("targetAbi", "")))),
            "manifest targetAbi is numeric",
            str(entry.get("targetAbi", "<missing>")),
        )
        report.check(
            source_url.startswith("https://"),
            "manifest sourceUrl is https",
            source_url or "<missing>",
            warning_only=allow_placeholder,
        )
        report.check(
            "example.invalid" not in source_url,
            "manifest sourceUrl is not a placeholder",
            source_url or "<missing>",
            warning_only=allow_placeholder,
        )
        report.check(
            bool(re.match(r"^[0-9a-f]{32}$", checksum)),
            "manifest checksum is an md5 hex digest",
            checksum or "<empty>",
            warning_only=allow_placeholder,
        )


def check_artifacts(publish_dir: Path | None, meta: dict, report: Report) -> None:
    """Packaging inputs: every listed artifact exists and no host assemblies ship."""
    if publish_dir is None:
        report.warn("publish output inspected", "skipped (no --publish-dir given)")
        return

    if not report.check(publish_dir.is_dir(), "publish output exists", str(publish_dir)):
        return

    present = {path.name for path in publish_dir.iterdir() if path.is_file()}
    for artifact in meta.get("artifacts") or []:
        report.check(str(artifact) in present, f"artifact present: {artifact}", str(publish_dir / str(artifact)))

    leaked = sorted(
        name
        for name in present
        if name.endswith(".dll")
        and name != PLUGIN_ASSEMBLY
        and name.startswith(HOST_ASSEMBLY_PREFIXES + SHARED_ASSEMBLY_PREFIXES)
    )
    report.check(
        not leaked,
        "no host/shared-framework assemblies in the plugin output",
        ", ".join(leaked) if leaked else "clean",
    )


def check_sources(root: Path, report: Report) -> None:
    """O1/O2/H7: no secrets, no unresolved verification TODOs."""
    scan_roots = [root / PLUGIN_DIR, root / "alignment-service"]
    scan_suffixes = {".cs", ".py", ".html", ".json", ".yaml", ".yml", ".xml"}
    skip_parts = {"bin", "obj", "__pycache__", ".pytest_cache", "artifacts", ".git"}

    todos: list[str] = []
    secrets: list[str] = []
    for scan_root in scan_roots:
        for path in scan_root.rglob("*"):
            if not path.is_file() or path.suffix not in scan_suffixes:
                continue
            if any(part in skip_parts for part in path.parts):
                continue
            text = path.read_text(encoding="utf-8", errors="replace")
            if "TODO(verify)" in text:
                todos.append(str(path.relative_to(root)))
            for pattern, label in SECRET_PATTERNS:
                for match in pattern.finditer(text):
                    secrets.append(f"{path.relative_to(root)}: {label} ({match.group(0)[:12]}...)")

    report.check(not todos, "no TODO(verify) markers left", ", ".join(todos) if todos else "none")
    report.check(not secrets, "no hard-coded credentials", "; ".join(secrets) if secrets else "none")


def check_docs(root: Path, report: Report) -> None:
    """I3/M1/M5: README legal section, install docs and known limitations."""
    readme_path = root / "README.md"
    if not report.check(readme_path.is_file(), "README exists", str(readme_path)):
        return

    readme = readme_path.read_text(encoding="utf-8")
    for heading, label in (
        ("## Legal", "legal section"),
        ("## Install", "install section"),
        ("## Configuration", "configuration reference"),
        ("## Troubleshooting", "troubleshooting section"),
    ):
        report.check(heading.lower() in readme.lower(), f"README has a {label}", heading)

    report.check("known limitation" in readme.lower(), "README documents known limitations", "Known limitations")
    report.check("terms of service" in readme.lower(), "README states the Genius ToS risk", "Terms of Service")


def check_tests(root: Path, report: Report) -> None:
    """L1/L2: unit test project exists and is wired for CI."""
    tests_csproj = root / TESTS_DIR / f"{TESTS_DIR}.csproj"
    if not report.check(tests_csproj.is_file(), "test project exists", str(tests_csproj)):
        return

    csproj = tests_csproj.read_text(encoding="utf-8")
    report.check('Include="xunit"' in csproj, "test project uses xunit", "xunit")
    report.check(
        'Include="Microsoft.NET.Test.Sdk"' in csproj,
        "test project uses the .NET test SDK",
        "Microsoft.NET.Test.Sdk",
    )

    test_files = sorted((root / TESTS_DIR).glob("*.cs"))
    report.check(len(test_files) >= 4, "test suite has several files", f"{len(test_files)} file(s)")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--publish-dir", type=Path, default=None, help="dotnet publish output to inspect")
    parser.add_argument(
        "--allow-placeholder-manifest",
        action="store_true",
        help="downgrade manifest sourceUrl/checksum problems to warnings (pre-release builds)",
    )
    args = parser.parse_args()

    root: Path = args.root.resolve()
    report = Report()

    meta = check_build_metadata(root, report)
    if meta:
        check_project(root, meta, report)
        check_manifest(root, meta, report, allow_placeholder=args.allow_placeholder_manifest)
        check_artifacts(args.publish_dir.resolve() if args.publish_dir else None, meta, report)

    check_defaults(root, meta, report)
    check_sources(root, report)
    check_docs(root, report)
    check_tests(root, report)

    print(report.render())

    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as handle:
            handle.write("### Plugin repository validation\n\n")
            handle.write(report.render_markdown())
            handle.write("\n")

    return 1 if report.failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
