# Security policy

## Reporting a vulnerability

Use GitHub's private reporting: **Security → Report a vulnerability** on this repository. Please
don't open a public issue for something exploitable.

Tell me the `version` and `engine` strings from `GET /health`, what you sent, what happened, and
whether it still does it on a fresh container.

## In scope

- the service: `POST /convert`, `GET /health`, `GET /api/status`, the status page;
- the image, `Dockerfile`, `docker/healthcheck.sh`, the compose file;
- `llm_shim/` and the scripts here.

## Out of scope

- **marker itself** — parsing, layout, OCR, tables, models. That's upstream:
  <https://github.com/datalab-to/marker>, and [UPSTREAM.md](UPSTREAM.md) says where things go.
- **The service having no authentication.** It's a converter for a trusted network. Pointing it at
  a public address is a deployment mistake, not a vulnerability.
- **Memory exhaustion from a big document.** Uploads are capped (`MAX_UPLOAD_BYTES`) and only one
  conversion runs at a time, but a high page count genuinely needs the memory the README talks
  about. Use `page_range`.

## Supported versions

The latest release. There's one pinned engine, fixes go out as a patch release, and older images
don't get patched separately.

## If a secret ends up in this repository

It gets rotated, not deleted. Removing a commit doesn't un-publish a key.
