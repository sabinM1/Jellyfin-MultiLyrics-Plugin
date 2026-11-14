"""Multi-Lyrics companion alignment service.

Demucs (optional vocal isolation) + WhisperX (forced alignment) turn a plain-text lyric
file and an audio file into word-level LRC.

Endpoints:
  GET  /health                 -> service/model status
  POST /align                  -> synchronous alignment; 429 if a job is already running
  POST /align/submit           -> enqueue a job, returns {"jobId": ...} (202)
  GET  /align/status/{jobId}   -> {"state": queued|running|done|failed, "lrc": ..., "error": ...}

Security model:
  - audioPath must resolve inside MUSIC_ROOT (mounted read-only in Docker);
  - text is capped at MAX_TEXT_CHARS;
  - jobs are serialized; the sync endpoint refuses with 429 while busy.

Run: uvicorn app:app --host 0.0.0.0 --port 8787
"""

from __future__ import annotations

import os
import re
import subprocess
import tempfile
import threading
import time
import uuid
from dataclasses import dataclass, field
from typing import Optional

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

# ---------------------------------------------------------------- configuration

MUSIC_ROOT = os.path.realpath(os.environ.get("MUSIC_ROOT", "/music"))
WHISPER_MODEL = os.environ.get("WHISPER_MODEL", "large-v3")
WHISPER_BATCH_SIZE = int(os.environ.get("WHISPER_BATCH_SIZE", "16"))
DEVICE = os.environ.get("DEVICE", "cpu")  # or "cuda"
COMPUTE_TYPE = os.environ.get("COMPUTE_TYPE", "")  # empty = whisperx default
DEMUCS_MODEL = os.environ.get("DEMUCS_MODEL", "htdemucs")
MAX_TEXT_CHARS = int(os.environ.get("MAX_TEXT_CHARS", str(32 * 1024)))
MAX_AUDIO_BYTES = int(os.environ.get("MAX_AUDIO_BYTES", str(200 * 1024 * 1024)))

app = FastAPI(title="Multi-Lyrics alignment service", version="1.0.0")

# ---------------------------------------------------------------- job manager


@dataclass
class Job:
    job_id: str
    audio_path: str
    text: str
    language: Optional[str]
    separate_vocals: bool
    state: str = "queued"  # queued -> running -> done | failed
    lrc: Optional[str] = None
    error: Optional[str] = None
    created: float = field(default_factory=time.time)


class JobManager:
    """Serializes alignment jobs on one worker thread."""

    def __init__(self) -> None:
        self._jobs: dict[str, Job] = {}
        self._lock = threading.Lock()
        self._busy = threading.Event()
        self._worker = threading.Thread(target=self._run, daemon=True)
        self._worker.start()

    def submit(self, job: Job) -> Job:
        with self._lock:
            self._jobs[job.job_id] = job
        return job

    def get(self, job_id: str) -> Optional[Job]:
        with self._lock:
            return self._jobs.get(job_id)

    def busy(self) -> bool:
        return self._busy.is_set()

    def _run(self) -> None:
        while True:
            with self._lock:
                queued = [j for j in self._jobs.values() if j.state == "queued"]
                queued.sort(key=lambda j: j.created)
                job = queued[0] if queued else None
                if job is not None:
                    job.state = "running"
            if job is None:
                time.sleep(0.5)
                continue
            self._busy.set()
            try:
                job.lrc = align(job.audio_path, job.text, job.language, job.separate_vocals)
                job.state = "done"
            except Exception as exc:  # noqa: BLE001 - report any failure to the poller
                job.error = f"{type(exc).__name__}: {exc}"
                job.state = "failed"
            finally:
                self._busy.clear()


_manager = JobManager()


# ---------------------------------------------------------------- path safety


def validate_audio_path(audio_path: str) -> str:
    """Refuse anything outside MUSIC_ROOT (the mounted, read-only music library)."""
    if not audio_path or not os.path.isabs(audio_path):
        raise HTTPException(status_code=400, detail="audioPath must be an absolute path")
    real = os.path.realpath(audio_path)
    if real != MUSIC_ROOT and not real.startswith(MUSIC_ROOT + os.sep):
        raise HTTPException(status_code=400, detail="audioPath outside MUSIC_ROOT refused")
    if not os.path.isfile(real):
        raise HTTPException(status_code=404, detail="audio file not found")
    if os.path.getsize(real) > MAX_AUDIO_BYTES:
        raise HTTPException(status_code=413, detail="audio file too large")
    return real


# ---------------------------------------------------------------- models


class AlignRequest(BaseModel):
    audioPath: str
    text: str = Field(min_length=1)
    language: Optional[str] = None
    separateVocals: bool = True


def check_text_size(text: str) -> None:
    """Reject oversized payloads with 413 (instead of Pydantic's generic 422)."""
    if len(text.encode("utf-8")) > MAX_TEXT_CHARS:
        raise HTTPException(status_code=413, detail="text exceeds MAX_TEXT_CHARS")


class JobResponse(BaseModel):
    jobId: str


class JobStatusResponse(BaseModel):
    state: str
    lrc: Optional[str] = None
    error: Optional[str] = None


# ---------------------------------------------------------------- ML pipeline

_whisper_model = None
_align_model_cache: dict = {}


def _load_whisper():
    global _whisper_model
    if _whisper_model is None:
        import torch  # noqa: PLC0415 - heavy import deferred
        import whisperx  # noqa: PLC0415

        compute = COMPUTE_TYPE or ("float16" if DEVICE == "cuda" else "int8")
        _whisper_model = whisperx.load_model(WHISPER_MODEL, DEVICE, compute_type=compute)
        _ = torch  # keep reference
    return _whisper_model


def _load_align_model(language: str):
    import whisperx  # noqa: PLC0415

    if language not in _align_model_cache:
        _align_model_cache.clear()  # keep at most one alignment model resident
        _align_model_cache[language] = whisperx.load_align_model(
            language_code=language, device=DEVICE
        )
    return _align_model_cache[language]


def _maybe_separate_vocals(audio_path: str, enabled: bool) -> str:
    """Runs Demucs into a temp dir and returns the isolated vocals stem, if enabled."""
    if not enabled:
        return audio_path
    out_dir = tempfile.mkdtemp(prefix="demucs-")
    subprocess.run(
        [
            "demucs",
            "--two-stems",
            "vocals",
            "-n",
            DEMUCS_MODEL,
            "-o",
            out_dir,
            audio_path,
        ],
        check=True,
        capture_output=True,
        timeout=3600,
    )
    stem = os.path.join(
        out_dir, DEMUCS_MODEL, os.path.splitext(os.path.basename(audio_path))[0], "vocals.wav"
    )
    if not os.path.isfile(stem):
        raise RuntimeError("demucs produced no vocals stem")
    return stem


# ---------------------------------------------------------------- LRC building

_WORD_RE = re.compile(r"[^\W_]+", re.UNICODE)


def _norm(token: str) -> str:
    return re.sub(r"[^\w]", "", token.lower(), flags=re.UNICODE)


def build_word_lrc(plain_text: str, words: list[dict]) -> str:
    """Greedy line-to-word alignment producing enhanced LRC:

    [00:12.34]<00:12.34>Word <00:12.80>by word
    """
    lines = [ln.strip() for ln in plain_text.replace("\r\n", "\n").split("\n") if ln.strip()]
    if not lines:
        raise ValueError("plain text has no lines")

    line_tokens = [[_norm(t) for t in _WORD_RE.findall(line)] for line in lines]
    consumed = [0] * len(lines)
    out: list[str] = []
    wi = 0

    def next_word():
        nonlocal wi
        if wi < len(words):
            w = words[wi]
            wi += 1
            return w
        return None

    for li, tokens in enumerate(line_tokens):
        matched = []
        while len(matched) < len(tokens):
            w = next_word()
            if w is None:
                break
            matched.append(w)

        if matched:
            def fmt(t: float) -> str:
                t = max(0.0, t)
                minutes = int(t // 60)
                seconds = t - minutes * 60
                return f"{minutes:02d}:{seconds:05.2f}"

            pieces = [f"[{fmt(matched[0]['start'])}]"]
            for k, w in enumerate(matched):
                pieces.append(f"<{fmt(w['start'])}>{w.get('word', '').strip()} ")
            # Trim the trailing space of the last piece and close with end marker.
            body = "".join(pieces).rstrip()
            out.append(f"{body} <{fmt(matched[-1]['end'])}>" if matched[-1].get("end") else body)
            consumed[li] = len(matched)
        else:
            out.append("[00:00.00]" + (lines[li] if li == 0 else ""))

    # Any unmatched words at the end are dropped; unmatched lines keep plain text.
    return "\n".join(out) + "\n"


def align(audio_path: str, text: str, language: Optional[str], separate_vocals: bool) -> str:
    import whisperx  # noqa: PLC0415

    audio = whisperx.load_audio(audio_path)
    work_audio = _maybe_separate_vocals(audio_path, separate_vocals)
    if work_audio != audio_path:
        audio = whisperx.load_audio(work_audio)

    model = _load_whisper()
    result = model.transcribe(audio, batch_size=WHISPER_BATCH_SIZE, language=language)
    detected = language or result.get("language") or "en"

    align_model, metadata = _load_align_model(detected)
    aligned = whisperx.align(
        result["segments"],
        align_model,
        metadata,
        audio,
        DEVICE,
        return_char_alignments=False,
    )
    words = [w for seg in aligned.get("segments", []) for w in seg.get("words", []) if w.get("start") is not None]
    if not words:
        raise ValueError("whisperx produced no word timings")
    return build_word_lrc(text, words)


# ---------------------------------------------------------------- endpoints


@app.get("/health")
def health() -> dict:
    return {
        "status": "ok",
        "model": WHISPER_MODEL,
        "device": DEVICE,
        "musicRoot": MUSIC_ROOT,
        "busy": _manager.busy(),
    }


@app.post("/align")
def align_sync(req: AlignRequest) -> dict:
    """Synchronous alignment. Serialized: returns 429 while another job runs."""
    check_text_size(req.text)
    if _manager.busy():
        raise HTTPException(status_code=429, detail="alignment service busy")
    path = validate_audio_path(req.audioPath)
    if _manager.busy():  # re-check after validation to close the race window
        raise HTTPException(status_code=429, detail="alignment service busy")
    try:
        lrc = align(path, req.text[:MAX_TEXT_CHARS], req.language, req.separateVocals)
    except HTTPException:
        raise
    except Exception as exc:  # noqa: BLE001
        raise HTTPException(status_code=500, detail=f"{type(exc).__name__}: {exc}") from exc
    return {"lrc": lrc, "error": None}


@app.post("/align/submit", status_code=202)
def align_submit(req: AlignRequest) -> JobResponse:
    check_text_size(req.text)
    path = validate_audio_path(req.audioPath)
    job = Job(
        job_id=uuid.uuid4().hex,
        audio_path=path,
        text=req.text[:MAX_TEXT_CHARS],
        language=req.language,
        separate_vocals=req.separateVocals,
    )
    _manager.submit(job)
    return JobResponse(jobId=job.job_id)


@app.get("/align/status/{job_id}", response_model=JobStatusResponse)
def align_status(job_id: str) -> JobStatusResponse:
    job = _manager.get(job_id)
    if job is None:
        raise HTTPException(status_code=404, detail="unknown jobId")
    return JobStatusResponse(state=job.state, lrc=job.lrc, error=job.error)
