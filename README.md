# BookMarker

[![ci](https://github.com/Hyper-Dragon/BookMarker/actions/workflows/ci.yml/badge.svg)](https://github.com/Hyper-Dragon/BookMarker/actions/workflows/ci.yml)
[![release](https://github.com/Hyper-Dragon/BookMarker/actions/workflows/release.yml/badge.svg)](https://github.com/Hyper-Dragon/BookMarker/actions/workflows/release.yml)
[![image](https://img.shields.io/badge/ghcr.io-hyper--dragon%2Fbookmarker-2496ed?logo=docker&logoColor=white)](https://github.com/Hyper-Dragon/BookMarker/pkgs/container/bookmarker)
[![licence](https://img.shields.io/badge/licence-GPL--3.0--or--later-3da639)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512bd4?logo=dotnet&logoColor=white)](BookMarker.slnx)
[![engine](https://img.shields.io/badge/engine-marker--pdf%201.10.2-ff6f00)](UPSTREAM.md)

An unofficial container and HTTP API for [marker](https://github.com/datalab-to/marker), Datalab's
PDF → Markdown engine.

BookMarker doesn't convert anything. marker does that. This project pins one marker release,
installs it into a CUDA image, and puts a small HTTP API in front of it that refuses work it
can't take.

**Not affiliated with Datalab.** marker is their work. The engine is downloaded from its published
release when the image is built and checked against a sha256, and no upstream file is patched. If
the markdown comes out wrong, that's marker's behaviour and it belongs upstream — see
[where to report what](#where-to-report-what).

## What you get

- **One conversion at a time.** If something is already running, you get a `503` with `Retry-After`
  immediately. Nothing queues and nothing waits.
- **An LLM decision per request.** `nollm` (the default), `llmonly`, `llmfallback`. `llmonly` fails
  loudly when the LLM isn't available instead of quietly doing something worse.
- **Nothing kept.** Your PDF goes into the service's own scratch directory and is deleted when the
  job ends, whether it worked or not.
- **A status page** at `/`. Free or busy, what's processing right now, what happened recently.- **Nothing fetched at run time.** The model weights are baked into the image, so an
  unattended run doesn't hang on a download that half-succeeded.- **A GPU.** The image is built on a CUDA base that publishes linux/amd64 only. There's no CPU
  mode and no arm64 image.

## What this is, and what it is not

| BookMarker provides | marker provides |
| --- | --- |
| the container image: CUDA base, pinned engine install, healthcheck, compose file | parsing, layout detection, reading order, OCR, tables, equations |
| the HTTP API: `POST /convert`, status codes, `X-*` headers, `503` + `Retry-After` | the `marker` CLI and what `--page_range`, `--workers`, `--use_llm` mean |
| the LLM policy and the `llm_shim` OpenAI-compatible service | the `OpenAIService` base class that shim extends |
| scratch lifecycle, backpressure, memory caps, status page | conversion quality, speed, and the model weights |

Pinned to `marker-pdf` **1.10.2**, unmodified. One difference from the dependency set upstream
declares: the image installs `pdftext==0.7.1` and `pypdfium2==5.10.1`, past the `~0.6.3` that
marker asks for, because 0.7.1 fixes a link-extraction hang on some PDFs. Upstream doesn't support that
combination, so say so if you file a bug there. The checksum and the bump steps are in
[UPSTREAM.md](UPSTREAM.md).

## Quick start

```bash
# pull and run (needs docker and an NVIDIA GPU)
docker compose up -d

# or build this tree and run it under the same compose file
docker build -t bookmarker:latest .
BOOKMARKER_IMAGE=bookmarker:latest docker compose up -d

# the model weights are in the image — nothing is downloaded at run time
# status page:   http://localhost:8080/
# health:        http://localhost:8080/health
```

Convert a PDF:

```bash
# default: no LLM
curl -fsS -X POST http://localhost:8080/convert \
  -F "file=@book.pdf" -o book.md

# a 3-page slice of a bigger book
curl -fsS -X POST http://localhost:8080/convert \
  -F "file=@book.pdf" -F "page_range=0-2" -o book-p0-2.md

# use the LLM path (see "LLM modes" below)
curl -fsS -X POST http://localhost:8080/convert \
  -F "file=@book.pdf" -F "llm=llmfallback" -o book.md
```

`book.sh` wraps this for local use: `./book.sh llmfallback book.pdf 0-2`.

## HTTP surface

| Endpoint | Purpose |
| --- | --- |
| `POST /convert` | Convert a PDF. Multipart (`file`, optional `page_range`, optional `llm`) or a raw PDF body with `page_range`/`llm` as query params. Returns `text/markdown` on success. |
| `GET /health` | Liveness. Always `200`, including mid-conversion. |
| `GET /api/status` | Machine-readable status: state, totals, current job, recent jobs, effective config. |
| `GET /` | The status page. Read-only. |
| `GET /openapi/v1.json` | OpenAPI document. |

Successful responses carry `X-Trace-Id`, `X-Pages`, `X-Elapsed-Ms`, `X-Sec-Per-Page`,
`X-Llm-Mode`, `X-Llm-Used` and `X-Llm-Fallback`. Errors are JSON
(`{"success":false,"errors":[...],"traceId":...}`, with a `logTail` when a conversion fails).
Status codes: `400` bad request · `413` too large · `415` not a PDF · `500` conversion failed or
`llm_unavailable` · `503` busy, with `Retry-After`.

[SPEC.md](SPEC.md) has the full contract.

## LLM modes

| `llm=` | Behaviour |
| --- | --- |
| `nollm` *(default)* | Convert without an LLM. |
| `llmonly` | Convert with the LLM. If it isn't configured, or the calls fail during the run, the request **fails** (`500`, `llm_unavailable`). It doesn't fall back to something worse. |
| `llmfallback` | Try the LLM. If it isn't configured or fails, re-run without it and return that. `X-Llm-Fallback: true` tells you it happened. |

LLM requests need `OPENAI_BASE_URL`, `OPENAI_MODEL` and `OPENAI_API_KEY`; `LLM_TIMEOUT_SECONDS`,
`LLM_MAX_RETRIES` and `LLM_RETRY_WAIT_SECONDS` tune the client. Calls go through the shim in
`llm_shim/` (`marker_openai_service.OpenAICompatibleService`), which is a marker `OpenAIService`
that falls back to `json_object` for backends that reject `json_schema`.

A run counts as "LLM unavailable" if the marker log shows the shim giving up — `giving up`,
`call failed`, or `could not parse JSON from response`.

With `llmonly` or `llmfallback`, page content is sent to the endpoint you configured. With `nollm`
nothing leaves the container except the first-run model download.

## Configuration

All environment variables. Defaults shown.

| Variable | Default | Purpose |
| --- | --- | --- |
| `PORT` | `8080` | HTTP listen port |
| `LOG_LEVEL` | `Information` | .NET log level |
| `BUSY_RETRY_AFTER_SECONDS` | `15` | `Retry-After` sent with a `503` |
| `WORKERS` | `1` | marker `--workers` |
| `DISABLE_MULTIPROCESSING` | `1` | marker `--disable_multiprocessing` |
| `DISABLE_IMAGE_EXTRACTION` | `1` | marker `--disable_image_extraction` (markdown only) |
| `MAX_TIME_MS` | `3600000` | timeout per conversion attempt; `0` means none |
| `MAX_UPLOAD_BYTES` | `536870912` | largest upload accepted (512 MiB) |
| `OPENAI_BASE_URL` / `OPENAI_MODEL` / `OPENAI_API_KEY` | *(empty)* | LLM backend for `llmonly` and `llmfallback` |
| `LLM_TIMEOUT_SECONDS` / `LLM_MAX_RETRIES` / `LLM_RETRY_WAIT_SECONDS` | `600` / `4` / `5` | LLM client tuning |
| `SCRATCH_ROOT` | `/tmp/bookmarker` | where per-job scratch lives |

Compose passes `PORT`, `WORKERS`, `MAX_TIME_MS`, `MAX_UPLOAD_BYTES`, `BUSY_RETRY_AFTER_SECONDS`,
`LOG_LEVEL`, `OPENAI_*` and `LLM_*` through. `DISABLE_MULTIPROCESSING`,
`DISABLE_IMAGE_EXTRACTION`, `SCRATCH_ROOT`, `MARKER_BINARY` and `SHIM_DIR` are set in the image and
aren't passed through — add them to `docker-compose.yml` if you need to change them. `PORT` is the
port inside the container; compose maps `${BOOKMARKER_PORT}` onto it.

Compose reads these itself:

| Variable | Default | Purpose |
| --- | --- | --- |
| `BOOKMARKER_IMAGE` | `ghcr.io/hyper-dragon/bookmarker:latest` | image to run |
| `BOOKMARKER_PORT` | `8080` | host port, mapped to the container's `PORT` |
| `BOOKMARKER_MEMORY` | `9g` | memory limit, and the swap limit too |

## Layout

```text
BookMarker/
├── src/BookMarker.Core/     # options, marker process runner, job registry, the single conversion slot
├── src/BookMarker.Web/      # ASP.NET Core host: /convert, /health, /api/status, status page
├── tests/                   # xUnit suite + a marker stand-in (no GPU needed)
├── llm_shim/                # marker LLM service with json_schema→json_object fallback
├── docker/healthcheck.sh    # container healthcheck (/dev/tcp, no curl needed)
├── Dockerfile               # multi-stage: .NET build → CUDA python + ASP.NET runtime
├── docker-compose.yml       # service definition (GPU reservation, memory caps, caches)
├── build-and-run.sh         # local test build + run (port 8888)
├── book.sh                  # small client helper
├── tests/assets/            # sample-3page.pdf for the acceptance script
├── scripts/acceptance.sh    # end-to-end checks against a running container
├── UPSTREAM.md              # what's pinned, what's patched (nothing), where bugs go
├── NOTICE                   # third-party statement and GPL notice
└── version.txt              # the only place a release version is written
```

## Where to report what

| Symptom | Where |
| --- | --- |
| Wrong headings, reading order, tables, OCR or equations | [upstream marker](https://github.com/datalab-to/marker/issues), mentioning the `pdftext` override |
| A marker flag not doing what its docs say | upstream |
| `503` when you wanted a conversion, a status code you didn't expect, a missing `X-*` header | here — that's the contract in [SPEC.md](SPEC.md) |
| Container won't start, health never passes, GPU not picked up | here |
| Your OpenAI-compatible backend being rejected | here if it's the shim's `json_schema` fallback, upstream if not |

## Operational notes

- **GPU, linux/amd64.** Built on `pytorch/pytorch:2.7.1-cuda12.6-cudnn9-runtime`, which publishes
  amd64 only. Compose reserves an NVIDIA device.
- **Memory.** Layout holds roughly 13 MB per page in memory. That's a rough figure from my own
  runs, not a benchmark, so check it against your documents. Set `BOOKMARKER_MEMORY` for the
  largest document you expect; compose defaults to `9g`. For very large books, split with
  `page_range` and join the markdown afterwards.
- **The image is about 10.7 GB uncompressed** (`docker images`), most of it the CUDA base and
  the model weights. It's a one-time pull, and the weights only change when the engine pin does.
- **Model artifacts are inside the image**, at `/root/.cache/datalab/models`. They're
  downloaded at build time from the checkpoint paths the installed surya release names, and the
  build then loads every model with the network off to prove the set is complete. There is no
  cache volume to seed and no first run that can fail on a download. Newer weights mean a new
  image.
- **`shm_size: 2g`** isn't optional. Docker's 64 MB default can deadlock marker's worker, and
  changing it means recreating the container, not restarting it.
- **Memory and swap are capped at the same value**, so the container can't borrow host swap. A
  spike kills this container and `restart: unless-stopped` brings it back.

## Development

```bash
dotnet build BookMarker.slnx            # compile
dotnet test BookMarker.slnx -c Release  # contract tests against a marker stand-in (no GPU)
dotnet format BookMarker.slnx --verify-no-changes

./build-and-run.sh                      # build the image and run the test container on :8888
./scripts/acceptance.sh                 # end-to-end checks against that container
```

The tests run the real pipeline against `tests/MarkerStub`, a stand-in for the `marker` CLI chosen
with `MARKER_BINARY`. That's how the contract, LLM policy, backpressure and scratch cleanup get
tested without a GPU or model weights. Conversion quality and speed can't be automated, so those
stay on the manual list.

## Saying thank you

This was written by me, initially for me. The tedious part is the work that makes it usable by
someone who isn't me. If it saves you an afternoon, you can
[sponsor it on GitHub](https://github.com/sponsors/Hyper-Dragon) or
[bung a few quid in the hat](https://www.paypal.com/donate/?hosted_button_id=QNKADKV5BAM5C).
No pressure, and nothing here is gated behind either.

## Software support

Report defects on the [issues page](https://github.com/Hyper-Dragon/BookMarker/issues/new/choose).

Two things save us both a round trip: the `version` and `engine` strings from `GET /health`, and
how you're running it (compose or `docker run`, GPU model, driver version). If one particular PDF
misbehaves, say what's in it — scans, two columns, tables, equations, sheer size — because that's
usually the whole diagnosis. "It doesn't work" is hard to action.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

Same licence as marker, for the obvious reason: the image ships marker, so it was going to be GPL
whatever I put at the top of this file. Datalab also sell marker commercially — that's between you
and them, and this project can't grant anything under it.

Model weights download on first run from Hugging Face and carry their own licences.
