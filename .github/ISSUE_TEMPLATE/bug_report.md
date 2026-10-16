---
name: Bug report
about: Something in the service, the image or the scripts is wrong
title: ''
labels: bug
assignees: ''
---

<!--
  Wrong headings, reading order, OCR or tables in the produced markdown are marker's
  behaviour, not this service's. See UPSTREAM.md for where that belongs.
-->

**What happened**

**What you expected**

**Build identity** — paste the JSON from `GET /health`. Without `version` and `engine` I can't
tell which build you're on.

```json
{ "status": "healthy", "version": "...", "engine": "..." }
```

**How you run it** — compose or `docker run`, GPU model and driver, Docker version

**Reproduction** — the smallest PDF that shows it, the exact request, and the response body

**Logs** — `docker logs <container> --tail 100`

**Checklist**

- [ ] This is about the service, not the converted markdown (see `UPSTREAM.md`)
- [ ] `version` and `engine` from `/health` are above
