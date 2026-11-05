"""Fetch every model artifact the engine needs, at image build time, then check it's the right set.

The service is meant to run unattended. If the weights are fetched on first use, what the
container produces depends on the network at run time rather than on the image, and two runs
of the same image can differ. Downloading them here means the image carries exactly the
artifacts this build fetched.

The engine checks a per-checkpoint `manifest.json` and returns immediately when it is
complete, so a warm cache means no network at run time. The Dockerfile proves that with a
`RUN --network=none` step immediately after this one.

`docker/model-artifacts.lock` records the hash of the whole artifact tree and of the render
font. If the host ever serves different bytes under the same versioned path, the build fails
here instead of quietly changing what the container produces. Regenerate it on purpose:

    docker run --rm --network none --entrypoint python <image> \\
        /usr/local/bin/preload_models.py --print

Checkpoint paths are `s3://<bucket>/<version>` and the local layout is
`<MODEL_CACHE_DIR>/<bucket>/<version>`, matching what the loader computes for itself.
"""

import hashlib
import os
import sys
from pathlib import Path

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

LOCK_PATH = Path("/usr/local/share/bookmarker/model-artifacts.lock")
CHUNK = 1024 * 1024


def local_path_for(checkpoint: str) -> str:
    return os.path.join(surya_settings.MODEL_CACHE_DIR, checkpoint.replace("s3://", ""))


def remote_path_for(checkpoint: str) -> str:
    # The loader joins S3_BASE_URL with a path that has already lost its `s3://` prefix;
    # passing the prefixed form builds https://models.datalab.to/s3://… and 404s.
    return checkpoint.replace("s3://", "")


def file_digest(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(CHUNK), b""):
            digest.update(chunk)
    return digest.hexdigest()


def tree_digest(root: Path) -> str:
    """Hash of every file under root, ordered by relative path. Contents only, no metadata."""
    digest = hashlib.sha256()
    for path in sorted(item for item in root.rglob("*") if item.is_file()):
        digest.update(path.relative_to(root).as_posix().encode())
        digest.update(b"\0")
        with path.open("rb") as handle:
            for chunk in iter(lambda: handle.read(CHUNK), b""):
                digest.update(chunk)
        digest.update(b"\0")
    return digest.hexdigest()


def read_lock() -> dict[str, str]:
    values: dict[str, str] = {}
    if not LOCK_PATH.exists():
        return values
    for line in LOCK_PATH.read_text().splitlines():
        line = line.split("#", 1)[0].strip()
        if not line or "=" not in line:
            continue
        key, value = line.split("=", 1)
        values[key.strip()] = value.strip()
    return values


def main(argv: list[str]) -> int:
    print_only = "--print" in argv

    if not print_only:
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

    artifacts = tree_digest(Path(surya_settings.MODEL_CACHE_DIR))
    font = file_digest(Path(marker_settings.FONT_PATH))
    print(f"artifacts = {artifacts}", flush=True)
    print(f"font = {font}", flush=True)

    if print_only:
        return 0

    expected = read_lock()
    if not expected:
        print(f"no lock file at {LOCK_PATH}", file=sys.stderr)
        return 1

    failed = False
    for key, actual in (("artifacts", artifacts), ("font", font)):
        wanted = expected.get(key)
        if wanted == actual:
            print(f"{key}: matches lock", flush=True)
        else:
            failed = True
            print(f"{key}: MISMATCH\nexpected {wanted}\nactual   {actual}", file=sys.stderr)

    if failed:
        print(
            "The model artifacts differ from docker/model-artifacts.lock. Either the host "
            "changed the bytes behind a versioned path, or the pinned engine/surya version "
            "changed. Decide deliberately, then regenerate the lock with --print.",
            file=sys.stderr,
        )
        return 1

    print("model artifacts match the lock", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
