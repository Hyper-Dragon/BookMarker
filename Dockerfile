# syntax=docker/dockerfile:1.7
#
# BookMarker - an UNOFFICIAL container for marker (https://github.com/datalab-to/marker).
# marker does the converting; this image packages a pinned upstream release and adds an
# HTTP contract around it. No upstream file is patched. Provenance: UPSTREAM.md.
#
# The engine is fetched from its release artifact and verified by sha256 (see ADD
# --checksum below), so the code in the image is the code the pin names, or the build
# fails. The only deliberate deviation is the pdftext/pypdfium2 override further down.

# =========================
# Stage 1: build the .NET service
# =========================
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH=amd64
# Stamped into the assembly; surfaced at /health and /api/status.
ARG MARKER_VERSION=1.10.2
# The commit, so a running container can name the build it came from. Workflows pass the
# short sha; anything else reports "local".
ARG SOURCE_REVISION=local
WORKDIR /src

COPY version.txt Directory.Build.props ./
COPY BookMarker.slnx ./
COPY src/BookMarker.Core/BookMarker.Core.csproj src/BookMarker.Core/
COPY src/BookMarker.Web/BookMarker.Web.csproj src/BookMarker.Web/
RUN dotnet restore src/BookMarker.Web/BookMarker.Web.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/BookMarker.Web/BookMarker.Web.csproj \
    -c Release -a $TARGETARCH -o /app --no-restore \
    -p:MarkerVersion=$MARKER_VERSION \
    -p:SourceRevisionId=$SOURCE_REVISION

# =========================
# Stage 2: runtime — CUDA Python (marker) + ASP.NET Core (wrapper)
# =========================
# Pinned by digest as well as tag. The tag alone is a promise the publisher can break; the
# digest is the actual bytes, and it's what pins torch, torchvision and the rest of the base
# image's 115 packages — which is why docker/requirements.lock only lists what we add.
# Bump the tag and digest together, and re-lock: see "Bumping the pin" in UPSTREAM.md.
FROM pytorch/pytorch:2.7.1-cuda12.6-cudnn9-runtime@sha256:2b59b1b91885677814f78be1f8df48a25d5dc952eb6580eaecfefca510f9afd3 AS final

ENV DEBIAN_FRONTEND=noninteractive

# System deps for marker (OpenCV/Pillow -> libgl/libglib; Poppler + Tesseract as shipped defaults)
RUN apt-get update && apt-get install -y --no-install-recommends \
        libgl1-mesa-glx \
        libglib2.0-0 \
        libgomp1 \
        poppler-utils \
        tesseract-ocr \
        tini \
        curl \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# .NET runtime — the C# wrapper supervises the python marker CLI
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && rm /tmp/dotnet-install.sh

# Already in the base image, so this is a no-op install and an assertion: if a future base
# image moves off torch 2.7.1/cu126, this prints the mismatch here rather than surfacing as a
# strange runtime failure halfway through a book.
RUN pip install --no-cache-dir --root-user-action=ignore "torchvision==0.22.1" \
    && python -c "import torch, torchvision; print('torch', torch.__version__, '| cuda', torch.version.cuda, '| torchvision', torchvision.__version__)"

# The upstream engine: marker-pdf, unmodified, from its pinned release artifact. BuildKit
# verifies the checksum before the tarball is used, so a swapped or truncated artifact
# fails the build instead of shipping. Bump the version, URL and checksum together and
# update UPSTREAM.md in the same commit.
ARG MARKER_VERSION=1.10.2
ADD --checksum=sha256:ce0fc839e11ad7519a576d254ca9d51a0f9454b9d7da02211f722b141317f9f1 \
    https://files.pythonhosted.org/packages/04/bc/a9e72fd015f562b1f86c9dbc201c80365e7baf43d5aa616ba1c89453537f/marker_pdf-1.10.2.tar.gz \
    /tmp/marker_pdf.tar.gz
# --no-deps: the sdist's own dependency metadata is deliberately not consulted. Every
# dependency comes from docker/requirements.lock below, so what gets installed is a decision
# recorded in this repository rather than whatever marker's ranges resolve to today.
RUN mkdir -p /opt/marker-pdf \
    && tar -xzf /tmp/marker_pdf.tar.gz --strip-components=1 -C /opt/marker-pdf \
    && rm /tmp/marker_pdf.tar.gz \
    && pip install --no-cache-dir --root-user-action=ignore --no-deps /opt/marker-pdf

# The dependency set, with a hash per artifact. --require-hashes means PyPI has to serve exactly
# these files; a substituted or re-uploaded artifact fails the build instead of changing what
# the container produces. It pins the two deliberate deviations too: surya-ocr 0.17.1, which
# names the model checkpoint paths, and pdftext 0.7.1 with pypdfium2 5.10.1.
#
# --no-deps because the file already is the whole closure. Asking pip to resolve it would fail:
# surya declares pypdfium2==4.30.0 and this image needs 5.10.1, a contradiction that can only be
# expressed by installing a list rather than by satisfying metadata. The completeness check is
# the two steps below — marker's CLI, then every model loaded with the network off — which
# import the entire stack and fail loudly if something is missing.
#
# Packages the base image already provides — torch, torchvision, numpy — are absent from the
# lock and pinned by the base digest instead.
COPY docker/requirements.lock /usr/local/share/bookmarker/requirements.lock
RUN pip install --no-cache-dir --root-user-action=ignore --require-hashes --no-deps \
        -r /usr/local/share/bookmarker/requirements.lock \
    && marker --help > /dev/null && echo "marker CLI ok (marker-pdf ${MARKER_VERSION})"

# Bake the model artifacts into the image. Unattended runs must not depend on the network
# for what the output looks like: the weights in the container are the weights this build
# downloaded, and they're checked against the hashes recorded in the repository.
COPY docker/model-artifacts.lock /usr/local/share/bookmarker/model-artifacts.lock
COPY docker/preload_models.py /usr/local/bin/preload_models.py
RUN python /usr/local/bin/preload_models.py

# Prove it, rather than assuming it: load every model with the network off. If an artifact
# is missing this fails at build time instead of during a pipeline run.
RUN --network=none python -c "\
from marker.models import create_model_dict; \
models = create_model_dict(device='cpu'); \
print('offline model load ok:', len(models), 'models')"

# LLM shim — selected via --llm_service marker_openai_service.OpenAICompatibleService
COPY llm_shim /app/shim

# The wrapper service
COPY --from=build /app /app

# The licence texts travel with the image: it distributes GPL-3.0-or-later software.
COPY LICENSE NOTICE /app/licenses/

COPY docker/healthcheck.sh /usr/local/bin/healthcheck.sh
RUN chmod +x /usr/local/bin/healthcheck.sh

WORKDIR /app

# Runtime defaults — every knob is overridable per deployment (see README + SPEC)
ENV PORT=8080 \
    LOG_LEVEL=Information \
    BUSY_RETRY_AFTER_SECONDS=15 \
    MAX_TIME_MS=3600000 \
    MAX_UPLOAD_BYTES=536870912 \
    WORKERS=1 \
    DISABLE_MULTIPROCESSING=1 \
    DISABLE_IMAGE_EXTRACTION=1 \
    SCRATCH_ROOT=/tmp/bookmarker \
    MARKER_BINARY=marker \
    SHIM_DIR=/app/shim \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONPATH=/app/shim \
    # The artifacts are in the image, so anything still trying to reach the internet is a
    # mistake. Fail loudly instead of downloading.
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1

ARG BOOKMARKER_VERSION=dev
LABEL org.opencontainers.image.title="BookMarker" \
      org.opencontainers.image.description="Unofficial container and HTTP service for marker-pdf. Not affiliated with Datalab." \
      org.opencontainers.image.version="${BOOKMARKER_VERSION}" \
      org.opencontainers.image.licenses="GPL-3.0-or-later" \
      org.opencontainers.image.source="https://github.com/Hyper-Dragon/BookMarker" \
      org.opencontainers.image.documentation="https://github.com/Hyper-Dragon/BookMarker/blob/main/README.md" \
      org.opencontainers.image.base.name="pytorch/pytorch:2.7.1-cuda12.6-cudnn9-runtime" \
      io.github.hyper-dragon.bookmarker.marker-version="${MARKER_VERSION}"

EXPOSE 8080

ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["dotnet", "/app/BookMarker.Web.dll"]
