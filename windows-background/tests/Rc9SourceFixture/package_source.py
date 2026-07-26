#!/usr/bin/env python3
"""Build the clean wx411_recover source directory and ZIP."""

from __future__ import annotations

import fnmatch
import hashlib
import shutil
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

PROJECT = Path(__file__).resolve().parent
OUTPUTS = PROJECT.parent
TARGET = OUTPUTS / "wx411_recover_source"
ZIP_PATH = OUTPUTS / "wx411_recover_source.zip"
ZIP_SHA_PATH = OUTPUTS / "wx411_recover_source.zip.sha256"

EXCLUDED_DIRECTORIES = {
    ".git",
    ".pytest_cache",
    "__pycache__",
    "bin",
    "obj",
    "dist",
    "TestResults",
}
EXCLUDED_FILES = {".DS_Store"}
EXCLUDED_PATTERNS = (
    "*.pyc",
    "*.pyo",
    "*.exe",
    "*.dll",
    "*.pdb",
    "*.zip",
    "*.dec.sqlite",
    "*.readable*.sqlite",
    "*.db-wal",
    "*.db-shm",
    "Wx411Easy-report-*.json",
)


def is_excluded(relative: Path) -> bool:
    if any(part in EXCLUDED_DIRECTORIES for part in relative.parts[:-1]):
        return True
    if relative.name in EXCLUDED_FILES:
        return True
    return any(fnmatch.fnmatch(relative.name, pattern) for pattern in EXCLUDED_PATTERNS)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_package_readme() -> None:
    text = """# wx411_recover 1.5-dev clean source package

This directory contains the Python and .NET source, tests, SQLCipher fixture,
build configuration, engineering documents, and Windows instructions.

Version 1.5-dev adds a single precise-capture workflow, dual module profiles,
a 4.1.12.24 clear-key INT3 profile, and default four-hook continuous capture
to the existing database-authentication pipeline. The
2026-07-24 revision also rechecks the complete file snapshot, treats sampled
pages as pending candidates only, detaches before full-page authentication,
retries failed candidates against a checksum-validated committed WAL view,
persists failed pending captures only as Windows CurrentUser DPAPI ciphertext,
cleans temporary SQLite sidecars, and counts only fully authenticated keys as
matches during a multi-database capture session. It also follows SQLCipher's
preallocation semantics: page 1 remains HMAC-gated, while all-zero pages after
page 1 are treated as uninitialized and remain zero in the plaintext output.
On 2026-07-24 this exact build completed Windows real-machine message database
recovery: 14 of 18 discovered databases passed full-page HMAC, decryption, and
SQLite integrity checks. The outputs include message_0 with 1,295 rows across
16 Msg_* tables. Four databases remain unmatched, so the evidence does not
imply universal database or version coverage.

The 2026-07-25 semantic export v2 revision also merges message_0 and
biz_message_0 using source-database-aware message keys, verifies main-file/WAL
source generations, closes sender identity gaps with deterministic contact
precedence, and rolls back paired SQLite/JSON publication failures. The real
recovered dataset exports 1,344 messages across 36 source conversations.

Excluded generated content:

- `.DS_Store`, `.pytest_cache/`, `__pycache__/`
- `bin/`, `obj/`, `dist/`, `TestResults/`
- compiled EXE/DLL/PDB files, release ZIP files, generated SQLite outputs,
  diagnostic JSON, WAL and SHM files

Verification from this directory:

```powershell
py -m pytest tests -q
dotnet restore .\\windows-easy\\Wx411Easy.sln
dotnet test .\\windows-easy\\Wx411Easy.sln -c Release --no-restore -p:TreatWarningsAsErrors=true
```

`SOURCE-MANIFEST.sha256` covers every file except the manifest itself.
The release executable is distributed separately under the main project's
`windows-easy/dist/` directory.
"""
    (TARGET / "SOURCE_PACKAGE.md").write_text(text, encoding="utf-8")


def build() -> None:
    if TARGET.exists():
        shutil.rmtree(TARGET)
    if ZIP_PATH.exists():
        ZIP_PATH.unlink()
    TARGET.mkdir(parents=True)

    copied = 0
    for source in sorted(PROJECT.rglob("*")):
        if not source.is_file():
            continue
        relative = source.relative_to(PROJECT)
        if is_excluded(relative):
            continue
        destination = TARGET / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
        copied += 1

    write_package_readme()

    manifest_path = TARGET / "SOURCE-MANIFEST.sha256"
    manifest_lines = []
    for path in sorted(TARGET.rglob("*")):
        if path.is_file() and path != manifest_path:
            relative = path.relative_to(TARGET).as_posix()
            manifest_lines.append(f"{sha256(path)}  {relative}")
    manifest_path.write_text("\n".join(manifest_lines) + "\n", encoding="utf-8")

    top = TARGET.name
    with ZipFile(ZIP_PATH, "w", compression=ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(TARGET.rglob("*")):
            if path.is_file():
                archive.write(path, f"{top}/{path.relative_to(TARGET).as_posix()}")

    zip_hash = sha256(ZIP_PATH)
    ZIP_SHA_PATH.write_text(
        f"{zip_hash}  {ZIP_PATH.name}\n",
        encoding="utf-8",
    )

    print(f"source_files={copied + 2}")
    print(f"source_dir={TARGET}")
    print(f"source_zip={ZIP_PATH}")
    print(f"source_zip_sha256={zip_hash}")


if __name__ == "__main__":
    build()
