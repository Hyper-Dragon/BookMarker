# BookMarker

**An unofficial container and HTTP service for [marker](https://github.com/datalab-to/marker)**,
Datalab's PDF → Markdown engine. BookMarker contains no conversion logic of its own: marker does
the converting, and this project pins one marker release, installs it unmodified into a CUDA
image, and puts a small, deliberate HTTP contract in front of it.

> **Not affiliated with, endorsed by or supported by Datalab.** This is packaging, not a fork:
> the engine is fetched unmodified from its pinned release artifact and verified by sha256, and
> no upstream file is patched — see [UPSTREAM.md](UPSTREAM.md). If the markdown itself is wrong,
> that is marker's behaviour, not this service's: [where to report what](#where-to-report-what).

What it adds:

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

## What this is, and what it is not

| BookMarker provides | marker provides |
| --- | --- |
| the container image: CUDA base, pinned engine install, healthcheck, compose file | parsing, layout detection, reading order, OCR, tables, equations |
| the HTTP contract: `POST /convert`, status codes, `X-*` headers, `503` + `Retry-After` | the `marker` CLI and the meaning of `--page_range`, `--workers`, `--use_llm` |
| the tri-state LLM policy and the `llm_shim` OpenAI-compatible service | the `OpenAIService` base class that shim extends |
| scratch lifecycle, backpressure, memory caps, status page | conversion quality, speed, and the model weights |

**Pinned:** `marker-pdf` **1.10.2**, unmodified. There is exactly one deliberate deviation: the
image installs `pdftext==0.7.1` and `pypdfium2==5.10.1` over the range marker declares, to fix a
link-extraction hang. That deviation is BookMarker's, not upstream's — disclose it if you report
a bug there. The pin, the checksum and the bump recipe live in [UPSTREAM.md](UPSTREAM.md).

## Quick start

```bash
# pull and run (assumes docker with an NVIDIA GPU)
docker compose up -d

# or build this tree first, then run it under the same compose file
docker build -t bookmarker:latest .
BOOKMARKER_IMAGE=bookmarker:latest docker compose up -d

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

**With `llmonly` or `llmfallback`, the document's content is sent to the OpenAI-compatible
endpoint you configure.** With `nollm` (the default) nothing leaves the container except the
first-run model download.

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
| `HF_HUB_OFFLINE` | `0` (compose), unset (image) | set `1` once model caches are warm to guarantee offline runs |

The example compose file passes `PORT`, `WORKERS`, `MAX_TIME_MS`, `MAX_UPLOAD_BYTES`,
`BUSY_RETRY_AFTER_SECONDS`, `LOG_LEVEL`, `OPENAI_*`, `LLM_*` and `HF_HUB_OFFLINE` through to the
container. `DISABLE_MULTIPROCESSING`, `DISABLE_IMAGE_EXTRACTION`, `SCRATCH_ROOT`,
`MARKER_BINARY` and `SHIM_DIR` are **image defaults** and are not passed through — add them to
`docker-compose.yml` if you need to change them. `PORT` is the in-container port; compose maps
`${BOOKMARKER_PORT}` onto it.

Compose-level variables (read by the compose file, not by the service):

| Variable | Default | Purpose |
| --- | --- | --- |
| `BOOKMARKER_IMAGE` | `ghcr.io/hyper-dragon/bookmarker:latest` | image the compose file runs |
| `BOOKMARKER_PORT` | `8080` | host port mapped to the container's `PORT` |
| `BOOKMARKER_MEMORY` | `9g` | container memory limit **and** swap limit (equal on purpose) |

## Layout

```text
BookMarker/
├── src/BookMarker.Core/     # options, marker process runner, job registry, concurrency-1 coordinator
├── src/BookMarker.Web/      # ASP.NET Core host: /convert, /health, /api/status, status page
├── tests/                   # xUnit suite + a marker stand-in (needs no GPU)
├── llm_shim/                # marker LLM service with json_schema→json_object fallback
├── docker/healthcheck.sh    # container healthcheck (/dev/tcp, no curl needed)
├── Dockerfile               # multi-stage: .NET build → CUDA python + ASP.NET runtime
├── docker-compose.yml       # service definition (GPU reservation, memory caps, caches)
├── build-and-run.sh         # local test build + run (port 8888)
├── book.sh                  # small client helper
├── tests/assets/            # sample-3page.pdf for smoke tests
├── scripts/acceptance.sh    # end-to-end acceptance checks against a running container
├── UPSTREAM.md              # what is pinned, what is patched (nothing), where bugs go
├── NOTICE                   # third-party statement and GPL notice
└── version.txt              # the only place a release version is written
```

## Where to report what

| Symptom | Where |
| --- | --- |
| Wrong headings, reading order, tables, OCR or equations in the markdown | [upstream marker](https://github.com/datalab-to/marker/issues) — with the `pdftext` override disclosed |
| A marker flag does not behave as its own docs say | upstream |
| `503` when you expected a conversion, an unexpected status code, a missing `X-*` header | here — that is the contract in [SPEC.md](SPEC.md) |
| Container will not start, health never passes, GPU not picked up | here |
| Your OpenAI-compatible backend is rejected | here if it is the shim's `json_schema` fallback; upstream otherwise |

## Operational notes

- **GPU required.** The container is built on `pytorch/pytorch:2.7.1-cuda12.6-cudnn9-runtime`
  and expects an NVIDIA GPU (compose reserves one). There is no CPU mode.
- **Memory.** The layout stage holds roughly **13 MB per page** of the document in memory.
  That is a sizing estimate taken from this deployment's own runs, not a benchmark — treat it as
  a planning number and check it against your own documents. Size the container cap for your
  largest expected document (compose defaults to `BOOKMARKER_MEMORY=9g`); for very large books,
  callers should use `page_range` splits and merge the markdown afterwards.
- **Model caches** live in named volumes (`/root/.cache/huggingface`,
  `/root/.cache/datalab`). Keep them warm across restarts; set `HF_HUB_OFFLINE=1` once warm.
- **`shm_size: 2g`** is required — the docker default 64 MB can dead-lock the marker
  worker; a shm change requires the container to be **recreated**, not restarted.
- **Memory+swap are capped together** (`memswap_limit == mem_limit`): the container can
  never borrow host swap; a spike OOM-kills just this container (and `restart:
  unless-stopped` brings it back).

## Development

```bash
dotnet build BookMarker.slnx            # compile
dotnet test BookMarker.slnx -c Release  # contract tests against a marker stand-in (no GPU)
dotnet format BookMarker.slnx --verify-no-changes

./build-and-run.sh                      # build image + run test container on :8888
./scripts/acceptance.sh                 # end-to-end checks against the running container
```

The test suite replaces the engine with a stub (`tests/MarkerStub`, selected with
`MARKER_BINARY`) so the HTTP contract, LLM policy, backpressure and scratch cleanup are all
covered on a machine with no GPU and no model weights. What a real GPU run adds — conversion
quality and timing — is on the manual checklist, not in the suite.

## Saying thank you

This was written by me, initially for me. The tedious part is the extra work that makes it
usable by someone who isn't me, and that is what this repository spends its time on. If it saves
you an afternoon, you can [sponsor it on GitHub](https://github.com/sponsors/Hyper-Dragon) or
[bung a few quid in the hat](https://www.paypal.com/donate/?hosted_button_id=QNKADKV5BAM5C).
No pressure — and nothing here is gated behind either.

## Software support

Report defects on the [issues page](https://github.com/Hyper-Dragon/BookMarker/issues/new/choose).
Two things make a report actionable, and without them it is usually three round trips: the
`version` and `engine` strings from `GET /health`, and how you run it (compose or `docker run`,
GPU model and driver version). If one particular PDF misbehaves, describe it — scanned,
two-column, tables, equations, huge — because that is usually the whole diagnosis.
"It doesn't work" is a hard report to action.

## License

**GPL-3.0-or-later** — see [LICENSE](LICENSE), and [NOTICE](NOTICE) for the third-party statement.

The licence matches upstream marker's deliberately. An image that runs marker is a GPL combined
work whatever the wrapper is licensed as, so one licence in the tree is honest and two that
appear to disagree are not. It costs nothing either: marker cannot be used in a proprietary
product under its own licence, so nothing was available to give away here.

Datalab also sells marker under a **commercial licence**. This project is not a party to that
agreement and cannot grant rights under it — ask Datalab. Model weights are downloaded at first
run from Hugging Face and carry their own licences.
