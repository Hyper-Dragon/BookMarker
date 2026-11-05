# BookMarker Service Specification v1.0

## Purpose

BookMarker is a synchronous HTTP service that converts one PDF to Markdown using the pinned
marker engine (see `UPSTREAM.md`). It handles one conversion at a time, refuses work it can't
take instead of queueing it, and keeps no copy of anything it was given.

It's not a queue, a scheduler or a batch processor.

---

## Core design principles

- **Concurrency is exactly 1, always.** There is no configuration knob for it.
- **No internal queuing.** If the service is busy, a conversion request is refused with
  `503` + `Retry-After` at once — never held. Callers requeue.
- **Deterministic failure.** If something cannot be done, the error names the reason.
- **Nothing is retained.** A submitted file is deleted when its job ends, success or
  failure alike. Failure handling is the caller's concern; the service keeps no
  reprocessing copies.
- **The service owns no caller-side lifecycle.** It never touches caller files; it
  copies uploads into its own scratch and disposes of them.

---

## Technology stack

- Language/runtime: C# / .NET 10 (ASP.NET Core Minimal APIs)
- Engine: `marker-pdf` **1.10.2**, unmodified, fetched from its pinned release artifact
  and verified by sha256 at build time (see `UPSTREAM.md`). BookMarker adds no
  conversion logic of its own.
- GPU: CUDA 12.6 (PyTorch 2.7.1 base image); no CPU mode
- LLM: any OpenAI-compatible endpoint, consumed through the shim
  `llm_shim/marker_openai_service.py` (`OpenAICompatibleService`)

---

## HTTP API

### `GET /health`

Always `200`, including while a conversion is running.

```json
{
  "status": "healthy",
  "timestamp": "2026-10-16T16:00:00Z",
  "service": "BookMarker",
  "version": "0.1.1+091a8ea",
  "commit": "091a8ea",
  "engine": "marker-pdf 1.10.2",
  "busy": false,
  "concurrency": { "max": 1, "available": 1 }
}

`version` is the build's own version with its commit as build metadata; `commit` is that
commit on its own, so "which build is this?" is answered by the endpoint a support question
would ask anyway. An image built outside CI reports `+local`. `engine` names the pinned
upstream release the image packages.
```

### `POST /convert`

Convert one PDF. Two input styles:

- **Multipart** (`multipart/form-data`): `file` (the PDF), optional `page_range`,
  optional `llm`.
- **Raw body**: the PDF itself (`Content-Type` anything), with `page_range` and `llm`
  as query parameters.

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `file` | file | required (multipart) | The PDF. Must carry a `%PDF-` header in the first 1024 bytes. |
| `page_range` | string | whole document | marker page range, **0-based**, comma/range list — e.g. `0`, `0-2`, `0,5-10,20`. Passed through to marker. |
| `llm` | string | `nollm` | LLM policy: `nollm` \| `llmonly` \| `llmfallback` (see below). Case/`-`/`_` insensitive. |

#### Responses

- **`200 text/markdown; charset=utf-8`** — the converted Markdown.
  Headers: `X-Trace-Id`, `X-Pages`, `X-Elapsed-Ms`, `X-Sec-Per-Page`,
  `X-Llm-Mode` (requested), `X-Llm-Used` (`llm` \| `none`), `X-Llm-Fallback` (`true` \| `false`).
- **`400`** — malformed request (missing/empty file, invalid `llm` value, bad multipart).
- **`413`** — upload exceeds `MAX_UPLOAD_BYTES`.
- **`415`** — content is not a PDF.
- **`500`** — conversion failed (`errors[]` + `logTail` = tail of the marker log), or
  `llm_unavailable` (an `llmonly` request whose LLM path was unavailable).
- **`503`** — service at capacity. Always sent with `Retry-After: <BUSY_RETRY_AFTER_SECONDS>`
  and a body that says so. This is the *only* meaning of a 503 from `/convert`.

Error bodies are JSON:

```json
{ "success": false, "errors": ["marker exited with code 1"], "traceId": "…", "logTail": "…" }
```

An `X-Trace-Id` request header is honoured (and echoed); otherwise a trace id is generated.

### `GET /api/status`

Machine-readable status: `state` (`free` \| `busy`), uptime, totals, effective
configuration, the current job (if any) and the last 25 jobs. Served from memory;
contains no document content.

### `GET /`

Read-only HTML status page: free/busy pill, live progress for the current conversion
(file, phase, page progress, recent engine output), recent-conversion table, and the
effective service configuration. It refreshes itself; it cannot start conversions.

### `GET /openapi/v1.json`

OpenAPI document describing the API above.

---

## LLM policy (tri-state, per request)

marker can run with or without an LLM backend; BookMarker makes that an explicit,
per-request decision. The server only supplies LLM *configuration* (env); the request
selects the *policy*.

| `llm=` | Not configured | LLM healthy | LLM fails during run |
| --- | --- | --- | --- |
| `nollm` (default) | runs without LLM | runs without LLM | runs without LLM |
| `llmonly` | **fails** (`500 llm_unavailable`, before running) | returns LLM-quality result | **fails** (`500 llm_unavailable`; the degraded run is discarded) |
| `llmfallback` | runs without LLM (`X-Llm-Fallback: true`) | returns LLM-quality result | **re-runs without LLM**, returns that (`X-Llm-Fallback: true`) |

"LLM fails during run" means the marker log contains a terminal shim failure —
`giving up`, `call failed`, or `could not parse JSON from response` — i.e. the LLM path
was not usable for at least one call. Per-call retries/backoff happen inside the shim
and are not failures by themselves.

A fallback re-run starts from a clean output directory; the per-doc summary and the
status page record which policy was used and whether a fallback happened.

---

## Job lifecycle and logging

1. Upload is spooled to `<SCRATCH_ROOT>/<traceId>/in/<filename>` (service scratch only).
2. The conversion slot is taken; `marker <in-folder> --output_dir <out> …` runs with the
   proven invocation (folder mode, `--workers` from env, `--disable_multiprocessing`,
   `--disable_image_extraction`, optional `--page_range`, and for LLM runs
   `--use_llm --llm_service marker_openai_service.OpenAICompatibleService --config_json <cfg>`).
3. Engine output is streamed into the service log (per-page progress lines) and into the
   job's ring buffer for the status page.
4. On completion the markdown is read into memory, a per-document summary is logged
   (`job succeeded|failed: file=… llm=… pages=… wall=…s s/page=…`), and the **entire
   scratch directory is deleted** — regardless of outcome.
5. The response is written from memory. Nothing from the job is persisted.

Notes: `--debug` is never used (marker 1.10.2 crashes at the end of every PDF with it);
`--disable_table_detection` does not exist in 1.10.2 and is never passed.

---

## Operational envelope

- **GPU is assumed** (CUDA). The compose file reserves an NVIDIA device; builds use the
  `pytorch/pytorch:2.7.1-cuda12.6-cudnn9-runtime` base. That base publishes linux/amd64 only,
  so the service is amd64-only; there is no CPU or arm64 image.
- **Memory sizing:** the layout stage holds ≈ 13 MB per *document page* in memory.
  Size `BOOKMARKER_MEMORY` for the largest expected document; for very large books,
  callers should split with `page_range` and merge the markdown.
- **`shm_size: 2g`** (docker default 64 MB can dead-lock the engine's worker).
  Changing shm requires a container recreate, not a restart.
- **`memswap_limit == mem_limit`**: the container can never borrow host swap; an OOM
  kills only this container, and `restart: unless-stopped` recovers it.
- **Model artifacts ship in the image**, at `/root/.cache/datalab/models`. They're fetched at
  build time, and the build loads every model with the network off before the image is
  published, so a conversion never downloads anything.
- A `kill -9` of the service process is recovered by the restart policy; conversions are
  synchronous and stateless across restarts.

---

## Versioning

This document describes **v1.0 of the BookMarker interface** — the contract above.

The software's own release version is recorded in `version.txt` and reported by `GET /health`,
together with the version of the upstream engine it packages. The two numbers are independent:
the interface version changes only when this file changes.
