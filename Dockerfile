# syntax=docker/dockerfile:1.7

# =========================
# Stage 1: build the .NET service
# =========================
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH=amd64
WORKDIR /src

COPY BookMarker.slnx ./
COPY src/BookMarker.Core/BookMarker.Core.csproj src/BookMarker.Core/
COPY src/BookMarker.Web/BookMarker.Web.csproj src/BookMarker.Web/
RUN dotnet restore src/BookMarker.Web/BookMarker.Web.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/BookMarker.Web/BookMarker.Web.csproj \
    -c Release -a $TARGETARCH -o /app --no-restore

# =========================
# Stage 2: runtime — CUDA Python (marker) + ASP.NET Core (wrapper)
# =========================
FROM pytorch/pytorch:2.7.1-cuda12.6-cudnn9-runtime AS final

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

# torchvision matched to the base image's torch 2.7.1+cu126 (0.22.1 PyPI wheels are cu126)
RUN pip install --no-cache-dir --root-user-action=ignore "torchvision==0.22.1" \
    && python -c "import torch, torchvision; print('torch', torch.__version__, '| cuda', torch.version.cuda, '| torchvision', torchvision.__version__)"

# Vendored marker-pdf — installed from the vendored source tree, never fetched from PyPI
COPY vendor/marker-pdf /opt/marker-pdf
RUN pip install --no-cache-dir --root-user-action=ignore /opt/marker-pdf \
    && marker --help > /dev/null && echo "marker CLI ok"

# Deliberate dependency override: fixes an infinite link-extraction hang on some PDFs.
# (The vendored pyproject declares pdftext ~0.6.3; 0.7.1 is the fix, so it intentionally
# moves beyond that range and is installed as a second step.)
RUN pip install --no-cache-dir --root-user-action=ignore "pdftext==0.7.1" "pypdfium2==5.10.1"

# LLM shim — selected via --llm_service marker_openai_service.OpenAICompatibleService
COPY llm_shim /app/shim

# The wrapper service
COPY --from=build /app /app
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
    PYTHONPATH=/app/shim

ARG BOOKMARKER_VERSION=dev
LABEL org.opencontainers.image.title="BookMarker" \
      org.opencontainers.image.description="Standalone marker-pdf wrapper - concurrency-1 PDF to Markdown HTTP service" \
      org.opencontainers.image.version="${BOOKMARKER_VERSION}"

EXPOSE 8080

ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["dotnet", "/app/BookMarker.Web.dll"]
