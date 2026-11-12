"""Fetch the Android build dependencies, pinned by Maven coordinate and SHA-256."""

from concurrent.futures import ThreadPoolExecutor
import hashlib
from pathlib import Path
import tempfile
from urllib.error import URLError
from urllib.request import urlopen


ROOT = Path(__file__).resolve().parent
CACHE = ROOT / ".deps"
REPOSITORIES = (
    "https://repo.maven.apache.org/maven2/",
    "https://maven-central.storage-download.googleapis.com/maven2/",
)


def download(path):
    for repository in REPOSITORIES:
        try:
            with urlopen(repository + path, timeout=30) as response:
                return response.read()
        except (URLError, TimeoutError) as error:
            last_error = error
    raise RuntimeError(f"Unable to fetch {path}") from last_error


def fetch(row):
    coordinate, digest = row.split()
    group, artifact, version = coordinate.split(":")
    name = f"{artifact}-{version}.jar"
    target = CACHE / name
    if target.is_file() and hashlib.sha256(target.read_bytes()).hexdigest() == digest:
        return name

    payload = download(f"{group.replace('.', '/')}/{artifact}/{version}/{name}")
    if hashlib.sha256(payload).hexdigest() != digest:
        raise RuntimeError(f"SHA-256 mismatch for {coordinate}")
    with tempfile.NamedTemporaryFile(dir=CACHE, delete=False) as output:
        output.write(payload)
    temporary = Path(output.name)
    try:
        temporary.replace(target)
    finally:
        temporary.unlink(missing_ok=True)
    return name


if __name__ == "__main__":
    CACHE.mkdir(exist_ok=True)
    locked = (ROOT / "dependencies.lock").read_text(encoding="ascii").splitlines()
    with ThreadPoolExecutor(max_workers=8) as executor:
        for name in executor.map(fetch, locked):
            print(name)
