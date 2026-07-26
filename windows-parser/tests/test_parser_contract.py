import json
import os
import signal
from pathlib import Path

import pytest

from conftest import (
    PARSER_ROOT,
    create_message_database,
    run_parser,
    sha256,
    write_job,
)
from parser_contract import (
    MAXIMUM_RESULT_BYTES,
    CancellationState,
    ParserContractError,
    write_result_atomic,
)


def test_parser_rejects_input_outside_controlled_root(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    outside = tmp_path / "outside.sqlite"
    create_message_database(outside)
    job = write_job(job_root, [("message/message_0.db", outside)])

    result = run_parser(job)

    assert result.returncode == 2
    assert result.stdout == ""
    assert not (job_root / "output" / "result.json").exists()


def test_parser_rejects_schema_mismatch(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    database = job_root / "input" / "message" / "message_0.db"
    create_message_database(database)
    job = write_job(job_root, [("message/message_0.db", database)], schema_version=2)

    result = run_parser(job)

    assert result.returncode == 2
    assert result.stdout == ""


def test_parser_rejects_hash_mismatch(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    database = job_root / "input" / "message" / "message_0.db"
    create_message_database(database)
    job = write_job(job_root, [("message/message_0.db", database)])
    payload = json.loads(job.read_text(encoding="utf-8"))
    payload["databases"][0]["sha256"] = "0" * 64
    job.write_text(json.dumps(payload), encoding="utf-8")

    result = run_parser(job)

    assert result.returncode == 2
    assert result.stdout == ""


def test_parser_rejects_duplicate_relative_path(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    database = job_root / "input" / "message" / "message_0.db"
    create_message_database(database)
    job = write_job(
        job_root,
        [
            ("message/message_0.db", database),
            ("message/message_0.db", database),
        ],
    )

    result = run_parser(job)

    assert result.returncode == 2
    assert result.stdout == ""


def test_parser_truncates_to_newest_5000_messages_deterministically(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    database = job_root / "input" / "message" / "message_0.db"
    create_message_database(database, count=5005)
    job = write_job(job_root, [("message/message_0.db", database)])

    first = run_parser(job)
    first_document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))
    os.remove(job_root / "output" / "result.json")
    second = run_parser(job)
    second_document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))

    assert first.returncode == second.returncode == 0
    assert first_document == second_document
    assert len(first_document["messages"]) == 5000
    assert first_document["messages"][0]["local_id"] == 5
    assert first_document["messages"][-1]["local_id"] == 5004


def test_malformed_sqlite_is_isolated_as_notice(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    message = job_root / "input" / "message" / "message_0.db"
    malformed = job_root / "input" / "favorite" / "favorite.db"
    create_message_database(message)
    malformed.parent.mkdir(parents=True)
    malformed.write_bytes(b"not-sqlite")
    job = write_job(
        job_root,
        [
            ("favorite/favorite.db", malformed),
            ("message/message_0.db", message),
        ],
    )

    result = run_parser(job)
    document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))

    assert result.returncode == 0
    assert len(document["messages"]) == 3
    assert document["favorites"] == []
    assert document["notices"] == [
        {
            "code": "database_read_failed",
            "database": "favorite/favorite.db",
            "detail": "sqlite_error",
        }
    ]


def test_result_above_32_mib_is_rejected_without_publish(tmp_path: Path) -> None:
    output_root = tmp_path / "output"
    document = {
        "schemaVersion": 1,
        "jobId": "job",
        "sourceSetId": "source",
        "messages": [{"content": "x" * MAXIMUM_RESULT_BYTES}],
        "contacts": [],
        "favorites": [],
        "notices": [],
    }

    with pytest.raises(ParserContractError):
        write_result_atomic(document, output_root)

    assert not (output_root / "result.json").exists()


def test_cancellation_signal_changes_progress_handler_state(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    state = CancellationState()
    handlers: dict[int, object] = {}

    def register(signum: int, handler: object) -> None:
        handlers[signum] = handler

    monkeypatch.setattr(signal, "signal", register)
    state.install_signal_handlers()
    handler = handlers[signal.SIGINT]
    assert callable(handler)
    handler(signal.SIGINT, None)

    assert state.cancelled is True
    assert state.progress_handler() == 1


def test_parser_source_contains_no_forbidden_runtime_ownership() -> None:
    source = "\n".join(
        path.read_text(encoding="utf-8")
        for path in (
            PARSER_ROOT / "parser_contract.py",
            PARSER_ROOT / "parser_core.py",
            PARSER_ROOT / "wx_parser.py",
        )
    )
    for forbidden in (
        "psutil",
        "requests",
        "subprocess",
        "socket",
        "urllib",
        "OpenProcess",
        "extract_database_keys_windows",
        "decrypt_database_tree",
        "WECHAT_MONITOR_SERVER_TOKEN",
    ):
        assert forbidden not in source
