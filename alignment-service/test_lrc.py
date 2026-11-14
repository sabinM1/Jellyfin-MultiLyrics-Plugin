"""Logic tests for the alignment service (no ML dependencies required).

Run: cd alignment-service && python -m pytest test_lrc.py -q
The ML pipeline itself (whisperx/demucs) is exercised only in the docker integration run.
"""

from __future__ import annotations

import importlib.util
import re
import sys
from pathlib import Path

import pytest

_APP = Path(__file__).parent / "app.py"


def _load_pure_functions():
    """Loads only the pure LRC-building parts of app.py (no fastapi/pydantic imports)."""
    import ast
    import types

    src = _APP.read_text(encoding="utf-8")
    tree = ast.parse(src)
    keep = []
    for node in tree.body:
        if isinstance(node, ast.Assign) and getattr(node.targets[0], "id", "") == "_WORD_RE":
            keep.append(ast.get_source_segment(src, node))
        if isinstance(node, ast.FunctionDef) and node.name in {"_norm", "build_word_lrc"}:
            keep.append(ast.get_source_segment(src, node))
    ns: dict = {"re": re}
    exec(compile("\n".join(keep), str(_APP), "exec"), ns)  # noqa: S102 - trusted local file
    return ns


@pytest.fixture(scope="module")
def lrc():
    return _load_pure_functions()


WORDS = [
    {"word": "First", "start": 10.0, "end": 10.3},
    {"word": "synthetic", "start": 10.4, "end": 11.0},
    {"word": "line", "start": 11.1, "end": 11.6},
    {"word": "Second", "start": 20.0, "end": 20.3},
    {"word": "line", "start": 20.4, "end": 20.7},
    {"word": "here", "start": 20.8, "end": 21.3},
]


def test_word_level_format(lrc):
    out = lrc["build_word_lrc"]("First synthetic line\nSecond line here", WORDS)
    first, second = out.split("\n")[:2]
    assert first.startswith("[00:10.00]<00:10.00>First <00:10.40>synthetic <00:11.10>line")
    assert second.startswith("[00:20.00]<00:20.00>Second <00:20.40>line <00:20.80>here")
    assert re.search(r"<00:11\.60>\s*$", first), "line closes with the end timestamp"


def test_empty_lines_raise(lrc):
    with pytest.raises(ValueError):
        lrc["build_word_lrc"]("", WORDS)


def test_unmatched_line_keeps_marker(lrc):
    # No words at all -> no crash, marker line emitted.
    out = lrc["build_word_lrc"]("Only line", [])
    assert out.startswith("[00:00.00]Only line") or "Only line" in out
