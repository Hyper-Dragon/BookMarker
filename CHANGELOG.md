# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The release workflow takes the notes for a tag from the matching section below, so a version is
not released until it has a section here.

## [Unreleased]

## [0.1.1] - 2026-11-05

### Added

- `GET /health` and `GET /api/status` report the commit the image was built from. The version
  string carries it as build metadata (`0.1.1+091a8ea`) and a separate `commit` field makes it
  readable without parsing. The image build takes it as `SOURCE_REVISION`; the workflows fill it
  from `git rev-parse --short HEAD`, and an image built by hand reports `local`.

## [0.1.0] - 2026-11-04

First public release: an unofficial container and HTTP service for
[marker](https://github.com/datalab-to/marker).

### Added

- `POST /convert` — one PDF to Markdown, multipart or raw body, optional `page_range`, optional
  per-request `llm` policy (`nollm` / `llmonly` / `llmfallback`), Markdown streamed back from
  memory with `X-Trace-Id`, `X-Pages`, `X-Elapsed-Ms`, `X-Sec-Per-Page`, `X-Llm-Mode`,
  `X-Llm-Used` and `X-Llm-Fallback`.
- Concurrency of exactly one, with immediate `503` + `Retry-After` refusal instead of a queue.
- `GET /health`, `GET /api/status` and a read-only status page; the health answer names the
  service version and the engine version this build packages.
- `llm_shim/` — a marker LLM service that keeps the strict `json_schema` call and falls back to
  `json_object` for backends that reject it.
- Per-job scratch that is deleted when the job ends, success or failure alike.
- A container healthcheck that needs no `curl` and no shell utilities beyond bash's `/dev/tcp`.
- A test suite that runs the real pipeline against a marker stand-in, so the contract, the LLM
  policy, backpressure and scratch cleanup are covered without a GPU.

### Changed

- **Licence is GPL-3.0-or-later**, matching the engine it packages, instead of an MIT wrapper
  around GPL software. See [NOTICE](NOTICE).
- **The engine is no longer committed to the tree.** `marker-pdf` 1.10.2 is fetched from its
  published release artifact and verified by sha256 during the build, so this repository is
  packaging rather than a fork. Provenance: [UPSTREAM.md](UPSTREAM.md).
- **One version file.** `version.txt` feeds the assembly, and the running service reports it.
  The previous hard-coded version constant is gone.
- One deliberate dependency deviation from upstream: `pdftext==0.7.1` and `pypdfium2==5.10.1`
  are installed over the range marker declares, to fix a link-extraction hang.

### Known limitations

- **amd64 with an NVIDIA GPU only.** There is no CPU image and no arm64 image yet.
- **No authentication.** Intended for a trusted network; do not publish it.
- **Roughly 13 MB of memory per document page** during layout. Very large books should be split
  with `page_range`.
- Model weights are downloaded on first run and are several gigabytes; set `HF_HUB_OFFLINE=1`
  once the caches are warm.
