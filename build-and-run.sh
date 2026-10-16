#!/usr/bin/env bash
set -euo pipefail

# Local test build + run for BookMarker (assumes WSL/desktop docker with an NVIDIA GPU).
# Usage: ./build-and-run.sh [--no-build]
# Serves on http://localhost:8888 (container port 8080).

LOG=/tmp/bookmarker-build.log
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NAME=bookmarker-test

echo "[$(date)] BookMarker build-and-run — log: $LOG"
{
  cd "$REPO_DIR"

  if [ "${1:-}" != "--no-build" ]; then
    echo "[$(date)] docker build (this pulls the CUDA base image on first run)..."
    docker build -t "$NAME:latest" --build-arg BOOKMARKER_VERSION="$(cat "$REPO_DIR/version.txt")" .
  fi

  echo "[$(date)] recreating container..."
  docker rm -f "$NAME" 2>/dev/null || true

  docker run -d \
    --name "$NAME" \
    --gpus all \
    --restart unless-stopped \
    --shm-size 2g \
    --health-cmd "bash /usr/local/bin/healthcheck.sh" \
    --health-interval 30s \
    --health-timeout 10s \
    --health-retries 3 \
    --health-start-period 20s \
    --memory 6g \
    --memory-swap 6g \
    -p 127.0.0.1:8888:8080 \
    -e LOG_LEVEL=Information \
    -e WORKERS=1 \
    -e MAX_TIME_MS="${MAX_TIME_MS:-3600000}" \
    -e MAX_UPLOAD_BYTES=536870912 \
    -e BUSY_RETRY_AFTER_SECONDS=15 \
    -e OPENAI_BASE_URL="${OPENAI_BASE_URL:-}" \
    -e OPENAI_MODEL="${OPENAI_MODEL:-}" \
    -e OPENAI_API_KEY="${OPENAI_API_KEY:-}" \
    -e HF_HUB_OFFLINE="${HF_HUB_OFFLINE:-0}" \
    -v bookmarker-test-hf:/root/.cache/huggingface \
    -v bookmarker-test-datalab:/root/.cache/datalab \
    "$NAME:latest"

  echo "[$(date)] waiting for healthcheck..."
  for i in $(seq 1 60); do
    STATUS=$(docker inspect --format='{{.State.Health.Status}}' "$NAME" 2>/dev/null || echo unknown)
    echo "[$(date)] health $i/60: $STATUS"
    if [ "$STATUS" = "healthy" ]; then
      echo "[$(date)] READY — status page: http://localhost:8888/"
      break
    fi
    sleep 5
  done
} 2>&1 | tee -a "$LOG"
