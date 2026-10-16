# Upstream: marker

BookMarker is packaging around marker, not a fork. This file is the record of what's pinned, what's
changed, and where each kind of bug belongs.

| | |
| --- | --- |
| Upstream project | marker — <https://github.com/datalab-to/marker> |
| Upstream package | `marker-pdf` |
| Pinned version | **1.10.2** |
| Licence | GPL-3.0-or-later, same as this repository |
| Patch status | unmodified — no upstream file is edited |
| Affiliation | none |

## What lives here

- the container image: CUDA base, engine install, healthcheck, compose file;
- the HTTP API: `POST /convert`, `GET /health`, `GET /api/status`, the status page, the status
  codes and `X-*` headers, and the `503` + `Retry-After` refusal;
- the LLM policy (`nollm` / `llmonly` / `llmfallback`);
- `llm_shim/marker_openai_service.py`, a marker LLM service that keeps the strict `json_schema`
  call and falls back to `json_object` for backends that reject it;
- scratch cleanup, memory caps, and the rest of the operational bits.

## What lives upstream

PDF parsing, layout detection, reading order, OCR, tables, equations, image extraction, the
`marker` CLI and its flags (`--page_range`, `--workers`, `--disable_multiprocessing`,
`--disable_image_extraction`, `--use_llm`, `--llm_service`, `--config_json`), the quality of the
output, the speed, and the model weights. None of it starts here.

## The pinned engine

| | |
| --- | --- |
| Artifact | <https://files.pythonhosted.org/packages/04/bc/a9e72fd015f562b1f86c9dbc201c80365e7baf43d5aa616ba1c89453537f/marker_pdf-1.10.2.tar.gz> |
| SHA-256 | `ce0fc839e11ad7519a576d254ca9d51a0f9454b9d7da02211f722b141317f9f1` |
| Checked by | the `Dockerfile` — `ADD --checksum=sha256:…` fails the build on a mismatch |
| Pinned on | 2026-10-16, re-checked 2026-11-04 |
| Source of record | the artifact URL above |

The engine source isn't committed here. The build downloads it from that URL, so the image can
only differ from upstream by the dependency override below.

## The one change: a dependency override

After installing marker, the image installs:

```text
pdftext==0.7.1
pypdfium2==5.10.1
```

marker 1.10.2 asks for `pdftext ~0.6.3`. The override fixes an infinite link-extraction hang on
some PDFs. It's outside what upstream declares and tests, and they don't have to care about it.

So if you report a conversion bug upstream, mention it:

> BookMarker 1.10.2 container; marker-pdf 1.10.2 unmodified; `pdftext` 0.7.1 and `pypdfium2` 5.10.1
> installed over the declared range (link-extraction hang workaround). Reproduces outside
> BookMarker: [yes/no]

## Where to report what

| Symptom | Where |
| --- | --- |
| Wrong heading levels, reading order, tables, OCR or equations | [upstream marker](https://github.com/datalab-to/marker/issues), mentioning the `pdftext` override |
| A marker CLI flag not behaving as its docs say | upstream |
| `503` when you wanted a conversion, a wrong status code, a missing `X-*` header | here — that's the contract in [SPEC.md](SPEC.md) |
| Container won't start, health never passes, GPU not picked up | here |
| Your OpenAI-compatible backend being rejected | here if it's the shim's `json_schema` → `json_object` fallback, upstream if not |
| Running out of memory on a large PDF | either, but the answer is usually `page_range` or a bigger memory limit — see the README |

## Reproducibility

Pinned: the engine artifact (sha256, checked by the build), the base image the acceptance run was
done against, and the app's own version (`version.txt`, plus the commit when the build supplies
one — `/health` reports both).

Not pinned: the Python dependency set. `pip install /opt/marker-pdf` resolves marker's ranges at
build time, so two builds months apart can differ in transitive packages. A
`pip-compile --generate-hashes` constraints file in the Dockerfile would fix it and isn't done.
Until then, the image digest is the authority on what a build contains, and the version and commit
the service reports identify it.

## Bumping the pin

1. Read upstream's release notes. Anything that touches the flags BookMarker passes is a change
   here too.
2. Download the new sdist. Record its URL and SHA-256 here and in the `Dockerfile`, in both
   `ADD --checksum` and the `MARKER_VERSION` argument.
3. Check whether the `pdftext` override is still needed. If it isn't, drop it. Don't keep it out of
   habit.
4. `./build-and-run.sh`, then `./scripts/acceptance.sh`. The acceptance script compares the
   service's output against a direct `marker` run inside the container, which is the real
   regression test for a bump.
5. Put the engine version in `CHANGELOG.md` for that release.
