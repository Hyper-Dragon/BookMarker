"""Fetch every model artifact the engine needs, at image build time.

The service is meant to run unattended. If the weights are fetched on first use, what the
container produces depends on the network at run time rather than on the image, and two runs
of the same image can differ. Downloading them here means the image carries exactly the
artifacts this build fetched.

The engine checks a per-checkpoint `manifest.json` and returns immediately when it is
complete, so a warm cache means no network at run time. The Dockerfile proves that with a
`RUN --network=none` step immediately after this one.

Checkpoint paths are `s3://<bucket>/<version>` and the local layout is
`<MODEL_CACHE_DIR>/<bucket>/<version>`, matching what the loader computes for itself.
"""

import os
import sys

from marker.settings import settings as marker_settings
from marker.util import download_font
from surya.common.s3 import check_manifest, download_directory
from surya.settings import settings as surya_settings

CHECKPOINT_ATTRIBUTES = (
    "DETECTOR_MODEL_CHECKPOINT",
    "FOUNDATION_MODEL_CHECKPOINT",
    "RECOGNITION_MODEL_CHECKPOINT",
    "LAYOUT_MODEL_CHECKPOINT",
    "TABLE_REC_MODEL_CHECKPOINT",
    "OCR_ERROR_MODEL_CHECKPOINT",
)


def local_path_for(checkpoint: str) -> str:
    return os.path.join(surya_settings.MODEL_CACHE_DIR, checkpoint.replace("s3://", ""))


def remote_path_for(checkpoint: str) -> str:
    # The loader joins S3_BASE_URL with a path that has already lost its `s3://` prefix;
    # passing the prefixed form builds https://models.datalab.to/s3://… and 404s.
    return checkpoint.replace("s3://", "")


def main() -> int:
    checkpoints = sorted({getattr(surya_settings, name) for name in CHECKPOINT_ATTRIBUTES})

    for checkpoint in checkpoints:
        target = local_path_for(checkpoint)
        print(f"{checkpoint} -> {target}", flush=True)
        # download_directory moves the downloaded files into local_dir, so it has to exist.
        os.makedirs(target, exist_ok=True)
        download_directory(remote_path_for(checkpoint), target)
        if not check_manifest(target):
            print(f"manifest still incomplete after download: {target}", file=sys.stderr)
            return 1

    print(f"font -> {marker_settings.FONT_PATH}", flush=True)
    download_font()
    if not os.path.exists(marker_settings.FONT_PATH):
        print("font missing after download", file=sys.stderr)
        return 1

    print(f"model artifacts complete in {surya_settings.MODEL_CACHE_DIR}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
