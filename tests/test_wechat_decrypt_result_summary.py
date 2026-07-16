import ast
import os
import re
import tempfile
import time
from pathlib import Path


SOURCE = Path(__file__).parents[1] / "windows" / "wechat_decrypt_engine.py"


def load_function(function_name: str, namespace: dict) -> object:
    tree = ast.parse(SOURCE.read_text(encoding="utf-8"))
    function = next(
        node
        for node in tree.body
        if isinstance(node, ast.FunctionDef) and node.name == function_name
    )
    module = ast.Module(body=[function], type_ignores=[])
    exec(compile(ast.fix_missing_locations(module), str(SOURCE), "exec"), namespace)
    return namespace[function_name]


def test_session_and_message_databases_are_required() -> None:
    required = load_function("is_required_sync_database", {"re": re})

    assert required("message/message_0.db") is True
    assert required("session/session.db") is True
    assert required("contact/contact.db") is False


def test_optional_failure_allows_message_export() -> None:
    summarize = load_function("build_decrypt_tree_result", {"time": time})
    result = summarize(
        [
            {"db_rel": "message/message_0.db", "required": True, "result": "success", "reason": ""},
            {"db_rel": "session/session.db", "required": True, "result": "success", "reason": ""},
            {"db_rel": "contact/contact.db", "required": False, "result": "failed", "reason": "integrity_failed"},
        ],
        {"attempts": 1},
        0.0,
    )

    assert result["success"] is True
    assert result["can_export_messages"] is True
    assert result["optional_failure_count"] == 1
    assert result["optional_failures"] == [
        {"db_rel": "contact/contact.db", "reason": "integrity_failed", "required": False}
    ]


def test_required_failure_blocks_message_export() -> None:
    summarize = load_function("build_decrypt_tree_result", {"time": time})
    result = summarize(
        [
            {"db_rel": "message/message_0.db", "required": True, "result": "failed", "reason": "key_unmatched"},
            {"db_rel": "session/session.db", "required": True, "result": "success", "reason": ""},
        ],
        {"attempts": 1},
        0.0,
    )

    assert result["success"] is False
    assert result["can_export_messages"] is False
    assert result["required_failure_count"] == 1
    assert result["failure_reason"] == "key_unmatched"


def test_required_database_without_key_blocks_message_export() -> None:
    summarize = load_function("build_decrypt_tree_result", {"time": time})
    result = summarize(
        [
            {"db_rel": "message/message_0.db", "required": True, "result": "skipped", "reason": "key_unmatched"},
            {"db_rel": "session/session.db", "required": True, "result": "success", "reason": ""},
        ],
        {"attempts": 1},
        0.0,
    )

    assert result["can_export_messages"] is False
    assert result["failure_reason"] == "key_unmatched"


def test_snapshot_collection_keeps_database_without_a_matching_key() -> None:
    collect = load_function(
        "collect_snapshot_source_database_files",
        {"os": os, "is_export_relevant_db": lambda _rel: True},
    )
    with tempfile.TemporaryDirectory() as db_dir:
        for rel in ("message/message_0.db", "session/session.db", "contact/contact.db"):
            path = Path(db_dir) / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"test")

        collected = collect(db_dir, {"session/session.db": {"enc_key": "x"}})

    assert [rel for rel, _path in collected] == [
        "contact/contact.db",
        "message/message_0.db",
        "session/session.db",
    ]


if __name__ == "__main__":
    test_session_and_message_databases_are_required()
    test_optional_failure_allows_message_export()
    test_required_failure_blocks_message_export()
    test_required_database_without_key_blocks_message_export()
    test_snapshot_collection_keeps_database_without_a_matching_key()
    print("WeChat decrypt result summary checks passed.")
