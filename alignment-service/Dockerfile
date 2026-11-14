# Multi-Lyrics companion alignment service (Demucs + WhisperX).
# CPU default build. For a CUDA build, uncomment the torch override below and set
# DEVICE=cuda / COMPUTE_TYPE=float16 in docker-compose.yml.
FROM python:3.11-slim

ENV PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    HF_HOME=/models/hf \
    TORCH_HOME=/models/torch \
    MUSIC_ROOT=/music \
    WHISPER_MODEL=large-v3 \
    DEVICE=cpu \
    COMPUTE_TYPE=int8 \
    DEMUCS_MODEL=htdemucs

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg git ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /srv

COPY requirements.txt ./
# CPU torch wheels; a CUDA-enabled build overrides this line.
RUN pip install torch==2.5.1 torchaudio==2.5.1 --index-url https://download.pytorch.org/whl/cpu \
    && pip install -r requirements.txt

COPY app.py ./

# Models are downloaded on first use into /models (mount a volume for caching).
VOLUME /models
EXPOSE 8787

HEALTHCHECK --interval=30s --timeout=10s --start-period=120s --retries=5 \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8787/health', timeout=5).status==200 else 1)"

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8787"]
