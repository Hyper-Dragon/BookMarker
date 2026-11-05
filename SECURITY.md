# Security policy

## Reporting a vulnerability

Use GitHub's private reporting: **Security → Report a vulnerability** on this repository. Please
do not open a public issue for something exploitable.

Include the two identity strings from `GET /health` (`version` and `engine`), what you sent, what
happened, and whether it reproduces on a fresh container. Those two fields are there so this
conversation starts with the right build.

## What is in scope

- the service: `POST /convert`, `GET /health`, `GET /api/status`, the status page;
- the container image, `Dockerfile`, `docker/healthcheck.sh`, the compose file;
- `llm_shim/` and the scripts in this repository.

## What is out of scope

- **marker itself** — parsing, layout, OCR, tables, models. That is upstream:
  <https://github.com/datalab-to/marker>. See [UPSTREAM.md](UPSTREAM.md).
- **That the service has no authentication.** It is a single-purpose converter for a trusted
  network, and that is a stated design decision, not a vulnerability. Putting it on a public
  address is a misconfiguration of the deployment.
- **Memory exhaustion from an oversized document.** The service caps uploads
  (`MAX_UPLOAD_BYTES`) and refuses concurrent work, but a large *page count* legitimately needs
  the memory the README describes. Use `page_range`.

## Supported versions

The most recent release. This is a wrapper around one pinned engine version; fixes ship as a
patch release, and older images are not patched separately.

## If a secret ever lands in this repository

It is treated as compromised and rotated, not deleted. Removing a commit does not un-publish a
key.
