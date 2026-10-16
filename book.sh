#!/usr/bin/env bash
set -euo pipefail

# Convert a PDF through a running BookMarker service.
# Usage: ./book.sh [nollm|llmonly|llmfallback] <input.pdf> [page_range] [output.md]
#   page_range: marker page range (0-based), e.g. "0-2" or "0,5-10"
#   env: BOOKMARKER_URL (default http://localhost:8888)

URL="${BOOKMARKER_URL:-http://localhost:8888}"
MODE="${1:-nollm}"
IN="${2:?usage: book.sh [nollm|llmonly|llmfallback] <input.pdf> [page_range] [output.md]}"
RANGE="${3:-}"
OUT="${4:-${IN%.*}.md}"

curl -fsS --max-time 7200 -X POST "$URL/convert" \
  -H "X-Trace-Id: $(date +%s)-$$" \
  -F "llm=$MODE" \
  ${RANGE:+-F "page_range=$RANGE"} \
  -F "file=@$IN" \
  -D /tmp/bookmarker-headers.txt \
  -o "$OUT"

echo "wrote $OUT ($(wc -c < "$OUT") bytes)"
grep -iE '^x-(pages|sec-per-page|llm|elapsed|trace)' /tmp/bookmarker-headers.txt || true
