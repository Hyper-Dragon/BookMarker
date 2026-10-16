# Vendored: marker-pdf 1.10.2

Upstream project: marker — "Convert documents to markdown with high speed and accuracy."
Upstream repository (per pyproject metadata): https://github.com/VikParuchuri/marker
(project now lives at https://github.com/datalab-to/marker)
Upstream license: GPL-3.0-or-later (see `LICENSE` in this directory)

Version: 1.10.2 (PyPI package `marker-pdf`)
Vendored from (exact release artifact): https://files.pythonhosted.org/packages/04/bc/a9e72fd015f562b1f86c9dbc201c80365e7baf43d5aa616ba1c89453537f/marker_pdf-1.10.2.tar.gz
Artifact sha256: `ce0fc839e11ad7519a576d254ca9d51a0f9454b9d7da02211f722b141317f9f1`
Vendored on: 2026-10-16

This directory is the source distribution (`marker_pdf-1.10.2.tar.gz`) published on
PyPI for release 1.10.2, extracted as-is. The container image installs this tree
(`pip install ./vendor/marker-pdf`) rather than fetching the package at build time.

Notes:

- Added file: this `VENDOR.md` only. No upstream file is modified.
- After installing the vendored tree, the image applies a deliberate dependency
  override: `pdftext==0.7.1` and `pypdfium2==5.10.1` are installed as a second step
  (the override fixes an infinite link-extraction hang on some PDFs; note the vendored
  `pdftext` requirement is `~0.6.3`, so the override intentionally moves beyond it).
