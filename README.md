# BookMarker

A standalone wrapper around [marker](https://github.com/datalab-to/marker) (Datalab's
PDF → Markdown engine). BookMarker **vendors** the pinned marker source and publishes a
small, sharp HTTP service around it:

- **One conversion at a time, by design** — a request that arrives while a conversion is
  running is refused immediately with `503` + `Retry-After` (deliberate backpressure —
  callers requeue; nothing is queued or held).
- **Tri-state LLM policy per request** — `nollm` (default), `llmonly`, `llmfallback`.
  The service never silently degrades: `llmonly` fails when the LLM path is unavailable;
  `llmfallback` re-runs without the LLM and says so.
- **No data retention** — uploaded PDFs are spooled into the service's own scratch and
  deleted when the job ends, success or failure alike (`in → process → delete` /
  `in → fail → delete`). Failed conversions are handled upstream; nothing is kept for
  reprocessing.
- **Read-only status page** at `/` — free/busy state, live progress of the current
  conversion, and recent job history.
- **CUDA GPU assumed** — BookMarker targets GPU hosts only (it makes no sense in other
  contexts). Tuning knobs (workers, timeouts, memory sizing, LLM settings) are all env
  vars for per-deployment control.

## Quick start

```bash
# build + run (assumes docker with an NVIDIA GPU; see build-and-run.sh for the test runner)
docker compose up -d --build

# first run downloads the marker models into named volumes — keep them warm
# status page:   http://localhost:8080/
# health:        http://localhost:8080/health
```

Convert a PDF:

```bash
# default: no LLM
curl -fsS -X POST http://localhost:8080/convert \
  -F "file=@book.pdf" -o book.md

# a 3-page cursor of a bigger book
curl -fsS -X POST http://localhost:8080/convert \
  -F "file=@book.pdf" -F "page_range=0-2" -o book-p0-2.md

# ask for the LLM path (see "LLM modes" below)
curl -fsS -X POST http://localhost:8080/convert \
  -F "file=@book.pdf" -F "llm=llmfallback" -o book.md
```

`book.sh` wraps this for local use: `./book.sh llmfallback book.pdf 0-2`.

## HTTP surface

| Endpoint | Purpose |
| --- | --- |
| `POST /convert` | Convert a PDF. Multipart (`file`, optional `page_range`, optional `llm`) or a raw PDF body with `page_range`/`llm` as query params. Returns `text/markdown` on success. |
| `GET /health` | Liveness — always `200`, including while a conversion is running. |
| `GET /api/status` | Machine-readable status (state, totals, current job, recent jobs, effective config). |
| `GET /` | Read-only status page (free/busy + live processing details). |
| `GET /openapi/v1.json` | OpenAPI document for the service. |

Success responses carry `X-Trace-Id`, `X-Pages`, `X-Elapsed-Ms`, `X-Sec-Per-Page`,
`X-Llm-Mode`, `X-Llm-Used`, `X-Llm-Fallback`. Errors are JSON
(`{"success":false,"errors":[...],"traceId":...}`, with a `logTail` on conversion
failures). Status codes: `400` bad request · `413` too large · `415` not a PDF ·
`500` conversion failed / `llm_unavailable` · `503` busy (with `Retry-After`).

See [SPEC.md](SPEC.md) for the full contract.

## LLM modes (per request)

| `llm=` | Behaviour |
| --- | --- |
| `nollm` *(default)* | Convert without any LLM backend. |
| `llmonly` | Convert **with** the LLM backend. If the LLM path is unavailable — not configured, or LLM calls failed during the run — the request **fails** (`500`, `llm_unavailable`). No silent degradation. |
| `llmfallback` | Try the LLM backend; if it is not configured or fails during the run, **re-run without the LLM** and return that result. `X-Llm-Fallback: true` tells you it happened. |

LLM requests use the OpenAI-compatible backend configured with `OPENAI_BASE_URL`,
`OPENAI_MODEL`, `OPENAI_API_KEY` (plus `LLM_TIMEOUT_SECONDS`, `LLM_MAX_RETRIES`,
`LLM_RETRY_WAIT_SECONDS`). LLM calls go through the shim in `llm_shim/`
(`marker_openai_service.OpenAICompatibleService`) — a marker `OpenAIService` with a
structured-output fallback for backends that reject `json_schema`.

A run counts as "LLM unavailable" when the shim reports terminal call failures
(`giving up`, `call failed`, `could not parse JSON from response`) in the marker log.

## Configuration

All settings are environment variables (defaults shown):

| Variable | Default | Purpose |
| --- | --- | --- |
| `PORT` | `8080` | HTTP listen port |
| `LOG_LEVEL` | `Information` | .NET log level |
| `BUSY_RETRY_AFTER_SECONDS` | `15` | `Retry-After` value sent with a `503` refusal |
| `WORKERS` | `1` | marker `--workers` (pool size) — tunable |
| `DISABLE_MULTIPROCESSING` | `1` | marker `--disable_multiprocessing` |
| `DISABLE_IMAGE_EXTRACTION` | `1` | marker `--disable_image_extraction` (markdown only) |
| `MAX_TIME_MS` | `3600000` | hard timeout per conversion attempt (0 = unlimited) |
| `MAX_UPLOAD_BYTES` | `536870912` | maximum accepted upload (512 MiB) |
| `OPENAI_BASE_URL` / `OPENAI_MODEL` / `OPENAI_API_KEY` | *(empty)* | LLM backend for `llmonly` / `llmfallback` |
| `LLM_TIMEOUT_SECONDS` / `LLM_MAX_RETRIES` / `LLM_RETRY_WAIT_SECONDS` | `600` / `4` / `5` | LLM client tuning |
| `SCRATCH_ROOT` | `/tmp/bookmarker` | per-job scratch root (service-owned) |
| `HF_HUB_OFFLINE` | unset | set `1` once model caches are warm to guarantee offline runs |

## Layout

```text
BookMarker/
├── src/BookMarker.Core/     # options, marker process runner, job registry, concurrency-1 coordinator
├── src/BookMarker.Web/      # ASP.NET Core host: /convert, /health, /api/status, status page
├── llm_shim/                # marker LLM service with json_schema→json_object fallback
├── vendor/marker-pdf/       # vendored marker source (pinned release artifact; see VENDOR.md)
├── docker/healthcheck.sh    # container healthcheck (/dev/tcp, no curl needed)
├── Dockerfile               # multi-stage: .NET build → CUDA python + ASP.NET runtime
├── docker-compose.yml       # service definition (GPU reservation, memory caps, caches)
├── build-and-run.sh         # local test build + run (port 8888)
├── book.sh                  # small client helper
├── tests/assets/            # sample-3page.pdf for smoke tests
└── scripts/acceptance.sh    # end-to-end acceptance checks against a running container
```

## Operational notes

- **GPU required.** The container is built on `pytorch/pytorch:2.7.1-cuda12.6-cudnn9-runtime`
  and expects an NVIDIA GPU (compose reserves one). There is no CPU mode.
- **Memory.** The layout stage holds roughly **13 MB per page** of the document in
  memory. Size the container cap for your largest expected document (compose defaults to
  `BOOKMARKER_MEMORY=9g`); for very large books, callers should use `page_range` splits
  and merge the markdown afterwards.
- **Model caches** live in named volumes (`/root/.cache/huggingface`,
  `/root/.cache/datalab`). Keep them warm across restarts; set `HF_HUB_OFFLINE=1` once warm.
- **`shm_size: 2g`** is required — the docker default 64 MB can dead-lock the marker
  worker; a shm change requires the container to be **recreated**, not restarted.
- **Memory+swap are capped together** (`memswap_limit == mem_limit`): the container can
  never borrow host swap; a spike OOM-kills just this container (and `restart:
  unless-stopped` brings it back).

## Development

```bash
./build-and-run.sh          # build image + run test container on :8888
./scripts/acceptance.sh     # end-to-end acceptance checks against the running container
```

## License

The wrapper (this repository, outside `vendor/`) is MIT — see [LICENSE](LICENSE).
The vendored engine keeps its upstream license (GPL-3.0-or-later) — see
`vendor/marker-pdf/LICENSE` and `vendor/marker-pdf/VENDOR.md` for provenance.
