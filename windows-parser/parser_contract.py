import hashlib
import json
import os
import re
import signal
import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any


SCHEMA_VERSION = 1
MAXIMUM_JOB_BYTES = 1024 * 1024
MAXIMUM_RESULT_BYTES = 32 * 1024 * 1024
MAXIMUM_DATABASES = 256
MAXIMUM_MESSAGES = 5000
MAXIMUM_CONTACTS = 5000
MAXIMUM_FAVORITES = 1000
MAXIMUM_NOTICES = 1000
MAXIMUM_MEDIA_BYTES = 5 * 1024 * 1024
MAXIMUM_CURSOR_CHARACTERS = 64 * 1024
PARSER_SOFT_TIMEOUT_SECONDS = 120
_SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")


class ParserContractError(Exception):
    pass


class ParserCancelled(Exception):
    pass


@dataclass(frozen=True)
class ParserDatabaseInput:
    generation_id: str
    relative_path: str
    path: Path
    sha256: str


@dataclass(frozen=True)
class ParserJob:
    schema_version: int
    job_id: str
    source_set_id: str
    input_root: Path
    output_root: Path
    databases: tuple[ParserDatabaseInput, ...]
    maximum_messages: int
    cursor: str | None = None


class CancellationState:
    def __init__(self, timeout_seconds: float = PARSER_SOFT_TIMEOUT_SECONDS) -> None:
        self.cancelled = False
        self.deadline = time.monotonic() + timeout_seconds
        self.cancellation_path: Path | None = None

    def set_cancellation_path(self, path: Path) -> None:
        self.cancellation_path = path

    def install_signal_handlers(self) -> None:
        def mark_cancelled(_signum: int, _frame: Any) -> None:
            self.cancelled = True

        signal.signal(signal.SIGINT, mark_cancelled)
        signal.signal(signal.SIGTERM, mark_cancelled)

    def progress_handler(self) -> int:
        if (
            self.cancelled
            or time.monotonic() >= self.deadline
            or (self.cancellation_path is not None and self.cancellation_path.exists())
        ):
            self.cancelled = True
            return 1
        return 0

    def throw_if_cancelled(self) -> None:
        if self.progress_handler():
            raise ParserCancelled("parser_cancelled")


def load_job(job_path: Path) -> ParserJob:
    resolved_job = job_path.resolve(strict=True)
    if resolved_job.stat().st_size > MAXIMUM_JOB_BYTES:
        raise ParserContractError("job_too_large")
    try:
        document = json.loads(
            resolved_job.read_text(encoding="utf-8"),
            object_pairs_hook=_reject_duplicate_members,
        )
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ParserContractError("job_json_invalid") from exc
    _require_object_members(
        document,
        {
            "schemaVersion",
            "jobId",
            "sourceSetId",
            "inputRoot",
            "outputRoot",
            "databases",
            "maximumMessages",
        },
        "job_members_invalid",
        optional={"cursor"},
    )
    if document["schemaVersion"] != SCHEMA_VERSION:
        raise ParserContractError("job_schema_unsupported")
    job_id = _bounded_text(document["jobId"], "job_id_invalid")
    source_set_id = _bounded_text(document["sourceSetId"], "source_set_id_invalid")
    input_root = _absolute_path(document["inputRoot"], "input_root_invalid").resolve(strict=True)
    if not input_root.is_dir():
        raise ParserContractError("input_root_invalid")
    output_root = _absolute_path(document["outputRoot"], "output_root_invalid")
    job_root = input_root.parent.resolve(strict=True)
    if not _is_below(resolved_job, job_root):
        raise ParserContractError("job_path_outside_job_root")
    if not _is_below(output_root.resolve(strict=False), job_root):
        raise ParserContractError("output_root_outside_job_root")
    if _is_below(output_root.resolve(strict=False), input_root):
        raise ParserContractError("output_root_inside_input_root")

    maximum_messages = document["maximumMessages"]
    if (
        isinstance(maximum_messages, bool)
        or not isinstance(maximum_messages, int)
        or not 1 <= maximum_messages <= MAXIMUM_MESSAGES
    ):
        raise ParserContractError("maximum_messages_invalid")
    cursor = document.get("cursor")
    if cursor is not None:
        cursor = _bounded_cursor(cursor, "cursor_invalid")
    raw_databases = document["databases"]
    if not isinstance(raw_databases, list) or not 1 <= len(raw_databases) <= MAXIMUM_DATABASES:
        raise ParserContractError("database_count_invalid")

    databases: list[ParserDatabaseInput] = []
    seen_paths: set[str] = set()
    for raw in raw_databases:
        _require_object_members(
            raw,
            {"generationId", "relativePath", "path", "sha256"},
            "database_members_invalid",
        )
        generation_id = _sha256_text(raw["generationId"], "generation_id_invalid")
        expected_sha256 = _sha256_text(raw["sha256"], "database_sha256_invalid")
        relative_path = _portable_relative_path(raw["relativePath"])
        if relative_path in seen_paths:
            raise ParserContractError("duplicate_relative_path")
        seen_paths.add(relative_path)
        database_path = _absolute_path(raw["path"], "database_path_invalid").resolve(strict=True)
        expected_path = (input_root / Path(*relative_path.split("/"))).resolve(strict=True)
        if database_path != expected_path or not _is_below(database_path, input_root):
            raise ParserContractError("database_path_outside_input_root")
        if not database_path.is_file():
            raise ParserContractError("database_path_invalid")
        actual_sha256 = file_sha256(database_path)
        if actual_sha256 != expected_sha256:
            raise ParserContractError("database_hash_mismatch")
        databases.append(
            ParserDatabaseInput(
                generation_id,
                relative_path,
                database_path,
                expected_sha256,
            )
        )

    databases.sort(key=lambda item: (item.relative_path, item.generation_id))
    return ParserJob(
        SCHEMA_VERSION,
        job_id,
        source_set_id,
        input_root,
        output_root,
        tuple(databases),
        maximum_messages,
        cursor,
    )


def write_result_atomic(document: dict[str, Any], output_root: Path) -> Path:
    _validate_result_counts(document)
    encoded = json.dumps(
        document,
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    if len(encoded) > MAXIMUM_RESULT_BYTES:
        raise ParserContractError("result_too_large")
    output_root.mkdir(parents=True, exist_ok=True)
    destination = output_root / "result.json"
    temporary = output_root / f".result.{uuid.uuid4().hex}.tmp"
    try:
        with temporary.open("xb") as handle:
            handle.write(encoded)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, destination)
        _flush_directory(output_root)
        return destination
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(128 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _reject_duplicate_members(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ParserContractError("duplicate_json_member")
        result[key] = value
    return result


def _require_object_members(
    value: Any,
    expected: set[str],
    code: str,
    *,
    optional: set[str] | None = None,
) -> None:
    optional = optional or set()
    if (
        not isinstance(value, dict)
        or not expected.issubset(value)
        or set(value) - expected - optional
    ):
        raise ParserContractError(code)


def _bounded_text(value: Any, code: str) -> str:
    if not isinstance(value, str) or not value.strip() or len(value) > 256:
        raise ParserContractError(code)
    return value


def _bounded_cursor(value: Any, code: str) -> str:
    if not isinstance(value, str) or not value.strip() or len(value) > MAXIMUM_CURSOR_CHARACTERS:
        raise ParserContractError(code)
    return value


def _sha256_text(value: Any, code: str) -> str:
    if not isinstance(value, str) or _SHA256_PATTERN.fullmatch(value) is None:
        raise ParserContractError(code)
    return value


def _absolute_path(value: Any, code: str) -> Path:
    if not isinstance(value, str) or not value or len(value) > 32767:
        raise ParserContractError(code)
    path = Path(value)
    if not path.is_absolute() or value.startswith(("\\\\", "//")):
        raise ParserContractError(code)
    return path


def _portable_relative_path(value: Any) -> str:
    if not isinstance(value, str) or not value or len(value) > 1024:
        raise ParserContractError("relative_path_invalid")
    normalized = value.replace("\\", "/")
    parts = normalized.split("/")
    if (
        normalized.startswith("/")
        or normalized.startswith("\\")
        or (len(normalized) >= 2 and normalized[0].isalpha() and normalized[1] == ":")
        or any(part in ("", ".", "..") for part in parts)
    ):
        raise ParserContractError("relative_path_invalid")
    return "/".join(parts)


def _is_below(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return path != root
    except ValueError:
        return False


def _validate_result_counts(document: dict[str, Any]) -> None:
    expected = {
        "schemaVersion",
        "jobId",
        "sourceSetId",
        "messages",
        "contacts",
        "favorites",
        "notices",
    }
    _require_object_members(document, expected, "result_members_invalid", optional={"nextCursor"})
    if "nextCursor" in document and document["nextCursor"] is not None:
        _bounded_cursor(document["nextCursor"], "next_cursor_invalid")
    limits = {
        "messages": MAXIMUM_MESSAGES,
        "contacts": MAXIMUM_CONTACTS,
        "favorites": MAXIMUM_FAVORITES,
        "notices": MAXIMUM_NOTICES,
    }
    for name, limit in limits.items():
        values = document[name]
        if not isinstance(values, list) or len(values) > limit:
            raise ParserContractError(f"{name}_limit_exceeded")


def _flush_directory(path: Path) -> None:
    flags = getattr(os, "O_DIRECTORY", 0)
    try:
        descriptor = os.open(path, os.O_RDONLY | flags)
    except OSError:
        return
    try:
        os.fsync(descriptor)
    except OSError:
        pass
    finally:
        os.close(descriptor)
