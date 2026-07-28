import hashlib
import json
import os
import signal
import sqlite3
from pathlib import Path

import pytest
import zstandard as zstd

from conftest import (
    PARSER_ROOT,
    create_message_database,
    create_contact_database,
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
from parser_core import (
    MAXIMUM_DECOMPRESSED_MESSAGE_BYTES,
    _decode_cursor,
    _encode_cursor,
    _sanitize_value,
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


def test_db_storage_message_path_matches_normalized_message_path(tmp_path: Path) -> None:
    regular_root = tmp_path / "regular"
    legacy_root = tmp_path / "legacy"
    regular_database = regular_root / "input" / "message" / "message_0.db"
    legacy_database = legacy_root / "input" / "db_storage" / "message" / "message_0.db"
    create_message_database(regular_database)
    create_message_database(legacy_database)
    regular_job = write_job(regular_root, [("message/message_0.db", regular_database)])
    legacy_job = write_job(
        legacy_root,
        [("db_storage/message/message_0.db", legacy_database)],
    )

    regular_result = run_parser(regular_job)
    legacy_result = run_parser(legacy_job)

    assert regular_result.returncode == legacy_result.returncode == 0
    regular_document = json.loads(
        (regular_root / "output" / "result.json").read_text(encoding="utf-8")
    )
    legacy_document = json.loads(
        (legacy_root / "output" / "result.json").read_text(encoding="utf-8")
    )
    assert legacy_document["messages"] == regular_document["messages"]


@pytest.mark.parametrize(
    ("packed_type", "expected_type", "expected_sub_type"),
    [
        (1, 1, 0),
        (0x600000031, 49, 6),
        (0x7D000000031, 49, 2000),
    ],
)
def test_packed_weixin_message_type_is_split_into_int32_fields(
    tmp_path: Path,
    packed_type: int,
    expected_type: int,
    expected_sub_type: int,
) -> None:
    job_root = tmp_path / f"job-{packed_type}"
    database = job_root / "input" / "message" / "message_0.db"
    create_message_database(database, count=1)
    with sqlite3.connect(database) as connection:
        table = connection.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Msg_%'"
        ).fetchone()[0]
        connection.execute(f'UPDATE "{table}" SET local_type = ?', (packed_type,))
    job = write_job(job_root, [("message/message_0.db", database)])

    result = run_parser(job)
    document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))

    assert result.returncode == 0
    message = document["messages"][0]
    assert message["msg_type"] == expected_type
    assert message["msg_sub_type"] == expected_sub_type


def test_zstd_message_content_flag_is_decoded_to_utf8(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    database = job_root / "input" / "message" / "message_0.db"
    create_message_database(database, count=1)
    compressed = zstd.ZstdCompressor().compress("zstd-original".encode("utf-8"))
    with sqlite3.connect(database) as connection:
        table_name = connection.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Msg_%'"
        ).fetchone()[0]
        connection.execute(
            f'UPDATE "{table_name}" SET message_content = ?, '
            "WCDB_CT_message_content = 4 WHERE local_id = 0",
            (compressed,),
        )
    job = write_job(job_root, [("message/message_0.db", database)])

    result = run_parser(job)
    document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))

    assert result.returncode == 0
    assert document["messages"][0]["content"] == "zstd-original"


@pytest.mark.parametrize(
    ("compressed", "detail"),
    [
        (b"not-a-zstd-frame", "zstd_invalid"),
        (
            zstd.ZstdCompressor().compress(
                b"x" * (MAXIMUM_DECOMPRESSED_MESSAGE_BYTES + 1)
            ),
            "zstd_output_too_large",
        ),
    ],
)
def test_zstd_decode_failure_is_isolated_and_noticed(
    tmp_path: Path,
    compressed: bytes,
    detail: str,
) -> None:
    job_root = tmp_path / "job"
    database = job_root / "input" / "message" / "message_0.db"
    create_message_database(database, count=1)
    with sqlite3.connect(database) as connection:
        table_name = connection.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Msg_%'"
        ).fetchone()[0]
        connection.execute(
            f'UPDATE "{table_name}" SET message_content = ?, '
            "WCDB_CT_message_content = 4 WHERE local_id = 0",
            (compressed,),
        )
    job = write_job(job_root, [("message/message_0.db", database)])

    result = run_parser(job)
    document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))

    assert result.returncode == 0
    assert document["messages"][0]["content"] == ""
    assert document["notices"] == [
        {
            "code": "message_decode_failed",
            "database": "message/message_0.db",
            "detail": detail,
        }
    ]


def test_parser_paginates_messages_with_a_deterministic_next_cursor(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    database = job_root / "input" / "message" / "message_0.db"
    create_message_database(database, count=5005)
    job = write_job(job_root, [("message/message_0.db", database)])

    first = run_parser(job)
    first_document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))
    first_ids = [message["local_id"] for message in first_document["messages"]]
    assert first.returncode == 0
    assert first_ids == list(range(5, 5005))
    assert isinstance(first_document.get("nextCursor"), str)

    payload = json.loads(job.read_text(encoding="utf-8"))
    payload["cursor"] = first_document["nextCursor"]
    job.write_text(json.dumps(payload), encoding="utf-8")
    os.remove(job_root / "output" / "result.json")
    second = run_parser(job)
    second_document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))
    second_ids = [message["local_id"] for message in second_document["messages"]]

    assert second.returncode == 0
    assert second_ids == list(range(5))
    assert set(first_ids).isdisjoint(second_ids)
    assert sorted(first_ids + second_ids) == list(range(5005))
    assert "nextCursor" not in second_document


def test_message_table_ties_do_not_duplicate_or_skip_across_pages(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    database = job_root / "input" / "message" / "message_0.db"
    create_message_database(database, count=3000)
    second_table = "Msg_" + "f" * 32
    with sqlite3.connect(database) as connection:
        connection.execute(
            f'''CREATE TABLE "{second_table}"(
                local_id INTEGER,
                local_type INTEGER,
                create_time INTEGER,
                real_sender_id INTEGER,
                message_content BLOB,
                WCDB_CT_message_content INTEGER
            )'''
        )
        connection.executemany(
            f'INSERT INTO "{second_table}" VALUES(?, 1, ?, 1, ?, 0)',
            ((index, index, f"other-{index}") for index in range(3000)),
        )
    job = write_job(job_root, [("message/message_0.db", database)])

    first = run_parser(job)
    first_document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))
    payload = json.loads(job.read_text(encoding="utf-8"))
    payload["cursor"] = first_document["nextCursor"]
    job.write_text(json.dumps(payload), encoding="utf-8")
    os.remove(job_root / "output" / "result.json")
    second = run_parser(job)
    second_document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))

    first_keys = {
        (item["wxid"], item["create_time"], item["local_id"])
        for item in first_document["messages"]
    }
    second_keys = {
        (item["wxid"], item["create_time"], item["local_id"])
        for item in second_document["messages"]
    }
    assert first.returncode == second.returncode == 0
    assert len(first_keys) == 5000
    assert len(second_keys) == 1000
    assert first_keys.isdisjoint(second_keys)
    assert len(first_keys | second_keys) == 6000
    assert "nextCursor" not in second_document


def test_duplicate_messages_across_database_shards_are_emitted_once(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    first_database = job_root / "input" / "message" / "message_0.db"
    second_database = job_root / "input" / "message" / "message_1.db"
    create_message_database(first_database, count=3)
    create_message_database(second_database, count=3)
    job = write_job(
        job_root,
        [
            ("message/message_0.db", first_database),
            ("message/message_1.db", second_database),
        ],
        maximum_messages=4,
    )

    identities: set[tuple[object, ...]] = set()
    page_count = 0
    while page_count < 10:
        result = run_parser(job)
        document = json.loads(
            (job_root / "output" / "result.json").read_text(encoding="utf-8")
        )
        assert result.returncode == 0
        page_count += 1
        identities.update(
            (
                message["wxid"],
                message["local_id"],
                message["create_time"],
                message["is_sender"],
                message["sender"],
                message["content"],
                message["msg_type"],
                message["msg_sub_type"],
                message["media_sha256"],
            )
            for message in document["messages"]
        )
        cursor = document.get("nextCursor")
        if cursor is None:
            break
        payload = json.loads(job.read_text(encoding="utf-8"))
        payload["cursor"] = cursor
        job.write_text(json.dumps(payload), encoding="utf-8")
        os.remove(job_root / "output" / "result.json")

    assert page_count == 2
    assert len(identities) == 3


def test_duplicate_messages_are_deduplicated_after_sender_name_resolution(
    tmp_path: Path,
) -> None:
    job_root = tmp_path / "job"
    first_database = job_root / "input" / "message" / "message_0.db"
    second_database = job_root / "input" / "message" / "message_1.db"
    contact_database = job_root / "input" / "contact" / "contact.db"
    chat_username = "shared@chatroom"
    chat_hash = hashlib.md5(chat_username.encode("utf-8")).hexdigest()

    for database, sender in ((first_database, "alice"), (second_database, "bob")):
        database.parent.mkdir(parents=True, exist_ok=True)
        with sqlite3.connect(database) as connection:
            connection.execute("CREATE TABLE Name2Id(user_name TEXT)")
            connection.execute(
                "INSERT INTO Name2Id(rowid, user_name) VALUES(1, ?), (2, ?)",
                (chat_username, sender),
            )
            connection.execute(
                f'''CREATE TABLE "Msg_{chat_hash}"(
                    local_id INTEGER,
                    local_type INTEGER,
                    create_time INTEGER,
                    real_sender_id INTEGER,
                    message_content BLOB,
                    WCDB_CT_message_content INTEGER
                )'''
            )
            connection.execute(
                f'INSERT INTO "Msg_{chat_hash}" VALUES(7, 1, 100, 2, "same", 0)'
            )

    contact_database.parent.mkdir(parents=True, exist_ok=True)
    with sqlite3.connect(contact_database) as connection:
        connection.execute(
            "CREATE TABLE contact(username TEXT, alias TEXT, remark TEXT, nick_name TEXT)"
        )
        connection.executemany(
            "INSERT INTO contact VALUES(?, '', 'Same Person', '')",
            (("alice",), ("bob",), (chat_username,)),
        )
    job = write_job(
        job_root,
        [
            ("contact/contact.db", contact_database),
            ("message/message_0.db", first_database),
            ("message/message_1.db", second_database),
        ],
    )

    result = run_parser(job)
    document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))

    assert result.returncode == 0
    assert len(document["messages"]) == 1
    assert document["messages"][0]["sender"] == "Same Person"


def test_contact_and_favorite_boundaries_advance_independently(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    contact = job_root / "input" / "contact" / "contact.db"
    favorite = job_root / "input" / "favorite" / "favorite.db"
    contact.parent.mkdir(parents=True)
    favorite.parent.mkdir(parents=True)
    with sqlite3.connect(contact) as connection:
        connection.execute(
            "CREATE TABLE contact(username TEXT, alias TEXT, remark TEXT, nick_name TEXT)"
        )
        connection.executemany(
            "INSERT INTO contact VALUES(?, '', '', ?)",
            ((f"user-{index:04d}", f"User {index:04d}") for index in range(5001)),
        )
    with sqlite3.connect(favorite) as connection:
        connection.execute(
            "CREATE TABLE Favorites(id INTEGER, title TEXT, update_time INTEGER)"
        )
        connection.executemany(
            "INSERT INTO Favorites VALUES(?, ?, ?)",
            ((index, f"Favorite {index}", index) for index in range(1001)),
        )
    job = write_job(
        job_root,
        [
            ("contact/contact.db", contact),
            ("favorite/favorite.db", favorite),
        ],
    )

    first = run_parser(job)
    first_document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))
    payload = json.loads(job.read_text(encoding="utf-8"))
    payload["cursor"] = first_document["nextCursor"]
    job.write_text(json.dumps(payload), encoding="utf-8")
    os.remove(job_root / "output" / "result.json")
    second = run_parser(job)
    second_document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))

    assert first.returncode == second.returncode == 0
    assert len(first_document["contacts"]) == 5000
    assert len(first_document["favorites"]) == 1000
    assert len(second_document["contacts"]) == 1
    assert len(second_document["favorites"]) == 1
    assert len({item["wxid"] for item in first_document["contacts"] + second_document["contacts"]}) == 5001
    assert len(
        {
            (item["source_table"], item["source_id"])
            for item in first_document["favorites"] + second_document["favorites"]
        }
    ) == 1001
    assert "nextCursor" not in second_document


def test_message_display_names_remain_stable_across_pages(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    message = job_root / "input" / "message" / "message_0.db"
    contact = job_root / "input" / "contact" / "contact.db"
    create_message_database(message, count=5001)
    create_contact_database(contact)
    job = write_job(
        job_root,
        [
            ("contact/contact.db", contact),
            ("message/message_0.db", message),
        ],
    )

    first = run_parser(job)
    first_document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))
    payload = json.loads(job.read_text(encoding="utf-8"))
    payload["cursor"] = first_document["nextCursor"]
    job.write_text(json.dumps(payload), encoding="utf-8")
    os.remove(job_root / "output" / "result.json")
    second = run_parser(job)
    second_document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))

    assert first.returncode == second.returncode == 0
    assert first_document["messages"][0]["nickname"] == "Alice Remark"
    assert first_document["messages"][0]["sender"] == "Alice Remark"
    assert second_document["messages"][0]["nickname"] == "Alice Remark"
    assert second_document["messages"][0]["sender"] == "Alice Remark"


def test_contact_small_avatar_url_is_exported_to_direct_messages(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    message = job_root / "input" / "message" / "message_0.db"
    contact = job_root / "input" / "contact" / "contact.db"
    create_message_database(message, count=1)
    contact.parent.mkdir(parents=True)
    with sqlite3.connect(contact) as connection:
        connection.execute(
            "CREATE TABLE contact("
            "username TEXT, alias TEXT, remark TEXT, nick_name TEXT, "
            "small_head_url TEXT, big_head_url TEXT)"
        )
        connection.execute(
            "INSERT INTO contact VALUES(?, '', '', ?, ?, ?)",
            ("alice", "Alice", "https://wx.qlogo.cn/small-alice", "https://wx.qlogo.cn/big-alice"),
        )
    job = write_job(
        job_root,
        [("contact/contact.db", contact), ("message/message_0.db", message)],
    )

    result = run_parser(job)
    document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))

    assert result.returncode == 0
    assert document["contacts"][0]["avatar"] == "https://wx.qlogo.cn/small-alice"
    assert document["messages"][0]["avatar"] == "https://wx.qlogo.cn/small-alice"


def test_group_message_uses_sender_big_avatar_when_small_is_empty(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    message = job_root / "input" / "message" / "message_0.db"
    contact = job_root / "input" / "contact" / "contact.db"
    room_username = "friends@chatroom"
    room_hash = hashlib.md5(room_username.encode("utf-8")).hexdigest()
    message.parent.mkdir(parents=True)
    with sqlite3.connect(message) as connection:
        connection.execute("CREATE TABLE Name2Id(user_name TEXT)")
        connection.execute(
            "INSERT INTO Name2Id(rowid, user_name) VALUES(1, ?), (2, 'bob')",
            (room_username,),
        )
        connection.execute(
            f'''CREATE TABLE "Msg_{room_hash}"(
                local_id INTEGER,
                local_type INTEGER,
                create_time INTEGER,
                real_sender_id INTEGER,
                message_content BLOB,
                WCDB_CT_message_content INTEGER
            )'''
        )
        connection.execute(
            f'INSERT INTO "Msg_{room_hash}" VALUES(1, 1, 100, 2, "hello", 0)'
        )
    contact.parent.mkdir(parents=True)
    with sqlite3.connect(contact) as connection:
        connection.execute(
            "CREATE TABLE contact("
            "username TEXT, alias TEXT, remark TEXT, nick_name TEXT, "
            "small_head_url TEXT, big_head_url TEXT)"
        )
        connection.executemany(
            "INSERT INTO contact VALUES(?, '', '', ?, ?, ?)",
            (
                (room_username, "Friends", "https://wx.qlogo.cn/room", ""),
                ("bob", "Bob", "   ", "https://wework.qpic.cn/big-bob"),
            ),
        )
    job = write_job(
        job_root,
        [("contact/contact.db", contact), ("message/message_0.db", message)],
    )

    result = run_parser(job)
    document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))

    assert result.returncode == 0
    assert document["messages"][0]["sender"] == "Bob"
    assert document["messages"][0]["avatar"] == "https://wework.qpic.cn/big-bob"


def test_legacy_contact_schema_keeps_empty_avatar_without_notice(tmp_path: Path) -> None:
    job_root = tmp_path / "job"
    contact = job_root / "input" / "contact" / "contact.db"
    create_contact_database(contact)
    job = write_job(job_root, [("contact/contact.db", contact)])

    result = run_parser(job)
    document = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))

    assert result.returncode == 0
    assert document["contacts"][0]["avatar"] == ""
    assert document["notices"] == []


def test_large_table_cursor_is_compact_and_round_trips() -> None:
    state = {
        "m": {
            ("message/message_0.db", f"Msg_{index:032x}"): (index, index)
            for index in range(2000)
        },
        "c": {"contact/contact.db": 5000},
        "f": {("favorite/favorite.db", "Favorites"): 1000},
    }

    encoded = _encode_cursor(state)

    assert len(encoded) < 64 * 1024
    assert _decode_cursor(encoded) == state


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


def test_non_finite_database_numbers_are_normalized_for_strict_json() -> None:
    assert _sanitize_value(float("nan")) is None
    assert _sanitize_value(float("inf")) is None
    assert _sanitize_value(float("-inf")) is None
    assert _sanitize_value(1.5) == 1.5


def test_result_writer_rejects_non_finite_json_without_publish(tmp_path: Path) -> None:
    output_root = tmp_path / "output"
    document = {
        "schemaVersion": 1,
        "jobId": "job",
        "sourceSetId": "source",
        "messages": [],
        "contacts": [],
        "favorites": [{"data_json": {"score": float("inf")}}],
        "notices": [],
    }

    with pytest.raises(ParserContractError, match="result_non_finite_number"):
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
