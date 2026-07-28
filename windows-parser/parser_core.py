import base64
import binascii
import hashlib
import json
import math
import re
import sqlite3
import zlib
from contextlib import contextmanager
from pathlib import Path
from typing import Any

import zstandard as zstd

from parser_contract import (
    MAXIMUM_CONTACTS,
    MAXIMUM_FAVORITES,
    MAXIMUM_MEDIA_BYTES,
    MAXIMUM_NOTICES,
    CancellationState,
    ParserCancelled,
    ParserContractError,
    ParserDatabaseInput,
    ParserJob,
)


MAXIMUM_DECOMPRESSED_MESSAGE_BYTES = 1024 * 1024
MAXIMUM_CURSOR_JSON_BYTES = 512 * 1024


def _decode_cursor(value: str | None) -> dict[str, dict[Any, Any]]:
    state: dict[str, dict[Any, Any]] = {"m": {}, "c": {}, "f": {}}
    if value is None:
        return state
    try:
        mode = value[:1]
        payload = value[1:] if mode in ("j", "z") else value
        encoded = payload.encode("ascii")
        encoded += b"=" * (-len(encoded) % 4)
        compressed = base64.b64decode(encoded, altchars=b"-_", validate=True)
        if mode == "z":
            decompressor = zlib.decompressobj()
            raw = decompressor.decompress(compressed, MAXIMUM_CURSOR_JSON_BYTES + 1)
            if (
                len(raw) > MAXIMUM_CURSOR_JSON_BYTES
                or not decompressor.eof
                or decompressor.unused_data
                or decompressor.unconsumed_tail
            ):
                raise ValueError("cursor_decompression_limit")
        else:
            raw = compressed
            if len(raw) > MAXIMUM_CURSOR_JSON_BYTES:
                raise ValueError("cursor_json_too_large")
        document = json.loads(raw.decode("utf-8"))
    except (UnicodeError, ValueError, binascii.Error, json.JSONDecodeError) as exc:
        raise ParserContractError("cursor_invalid") from exc
    if not isinstance(document, dict) or set(document) != {"v", "m", "c", "f"}:
        raise ParserContractError("cursor_invalid")
    if document["v"] != 1:
        raise ParserContractError("cursor_invalid")
    for name in ("m", "c", "f"):
        if not isinstance(document[name], list) or len(document[name]) > 16384:
            raise ParserContractError("cursor_invalid")

    for entry in document["m"]:
        if not isinstance(entry, list) or len(entry) != 4:
            raise ParserContractError("cursor_invalid")
        relative_path = _cursor_text(entry[0], 1024)
        table_name = _cursor_text(entry[1], 256)
        boundary = (_cursor_integer(entry[2]), _cursor_integer(entry[3]))
        if (relative_path, table_name) in state["m"]:
            raise ParserContractError("cursor_invalid")
        state["m"][(relative_path, table_name)] = boundary

    for entry in document["c"]:
        if not isinstance(entry, list) or len(entry) != 2:
            raise ParserContractError("cursor_invalid")
        relative_path = _cursor_text(entry[0], 1024)
        rowid = _cursor_integer(entry[1], positive=True)
        if relative_path in state["c"]:
            raise ParserContractError("cursor_invalid")
        state["c"][relative_path] = rowid

    for entry in document["f"]:
        if not isinstance(entry, list) or len(entry) != 3:
            raise ParserContractError("cursor_invalid")
        relative_path = _cursor_text(entry[0], 1024)
        table_name = _cursor_text(entry[1], 256)
        rowid = _cursor_integer(entry[2], positive=True)
        if (relative_path, table_name) in state["f"]:
            raise ParserContractError("cursor_invalid")
        state["f"][(relative_path, table_name)] = rowid
    return state


def _encode_cursor(state: dict[str, dict[Any, Any]]) -> str:
    document = {
        "v": 1,
        "m": [
            [relative_path, table_name, boundary[0], boundary[1]]
            for (relative_path, table_name), boundary in sorted(state["m"].items())
        ],
        "c": [
            [relative_path, rowid]
            for relative_path, rowid in sorted(state["c"].items())
        ],
        "f": [
            [relative_path, table_name, rowid]
            for (relative_path, table_name), rowid in sorted(state["f"].items())
        ],
    }
    raw = json.dumps(document, sort_keys=True, separators=(",", ":")).encode("utf-8")
    if len(raw) > MAXIMUM_CURSOR_JSON_BYTES:
        raise ParserContractError("cursor_too_large")
    compressed = zlib.compress(raw, level=9)
    return "z" + base64.urlsafe_b64encode(compressed).rstrip(b"=").decode("ascii")


def _cursor_text(value: Any, maximum: int) -> str:
    if not isinstance(value, str) or not value or len(value) > maximum:
        raise ParserContractError("cursor_invalid")
    return value


def _cursor_integer(value: Any, *, positive: bool = False) -> int:
    minimum = 1 if positive else -(2**63)
    if isinstance(value, bool) or not isinstance(value, int) or not minimum <= value < 2**63:
        raise ParserContractError("cursor_invalid")
    return value


def _classification_path(relative_path: str) -> str:
    normalized = relative_path.replace("\\", "/").lower()
    prefix = "db_storage/"
    return normalized[len(prefix) :] if normalized.startswith(prefix) else normalized


def parse_job(job: ParserJob, cancellation: CancellationState) -> dict[str, Any]:
    notices: list[dict[str, str]] = []
    cursor = _decode_cursor(job.cursor)
    contacts, contacts_have_more = _read_contacts(
        job.databases,
        cursor,
        notices,
        cancellation,
    )
    contact_map = {item["wxid"]: item for item in contacts}
    messages, messages_have_more = _read_messages(
        job.databases,
        job.maximum_messages,
        cursor,
        notices,
        cancellation,
    )
    _resolve_message_display_names(
        messages,
        job.databases,
        contact_map,
        notices,
        cancellation,
    )
    messages = _newest_messages(messages, job.maximum_messages)
    favorites, favorites_have_more = _read_favorites(
        job.databases,
        cursor,
        notices,
        cancellation,
    )
    notices.sort(key=lambda item: (item["database"], item["code"], item["detail"]))
    result = {
        "schemaVersion": 1,
        "jobId": job.job_id,
        "sourceSetId": job.source_set_id,
        "messages": messages,
        "contacts": contacts,
        "favorites": favorites,
        "notices": notices[:MAXIMUM_NOTICES],
    }
    if messages_have_more or contacts_have_more or favorites_have_more:
        result["nextCursor"] = _encode_cursor(cursor)
    return result


def _read_contacts(
    databases: tuple[ParserDatabaseInput, ...],
    cursor: dict[str, dict[Any, Any]],
    notices: list[dict[str, str]],
    cancellation: CancellationState,
) -> tuple[list[dict[str, Any]], bool]:
    candidates: list[
        tuple[tuple[int, str], str, int, dict[str, Any] | None]
    ] = []
    for database in databases:
        if Path(_classification_path(database.relative_path)).name != "contact.db":
            continue
        try:
            with _open_database(database.path, cancellation) as connection:
                boundary = cursor["c"].get(database.relative_path, 0)
                rows = connection.execute(
                    "SELECT rowid AS __rowid__, username, alias, remark, nick_name "
                    "FROM contact WHERE rowid > ? ORDER BY rowid LIMIT ?",
                    (boundary, MAXIMUM_CONTACTS + 1),
                )
                for row in rows:
                    cancellation.throw_if_cancelled()
                    rowid = _integer(row["__rowid__"])
                    username = _text(row["username"]).strip()
                    alias = _text(row["alias"]).strip()
                    remark = _text(row["remark"]).strip()
                    nick_name = _text(row["nick_name"]).strip()
                    candidates.append(
                        (
                            (rowid, database.relative_path),
                            database.relative_path,
                            rowid,
                            {
                                "wxid": username,
                                "alias": alias,
                                "remark": remark,
                                "nick_name": nick_name,
                                "display_name": remark or nick_name or alias or username,
                                "avatar": "",
                                "source_updated_at": 0,
                                "extra_json": None,
                            } if username else None,
                        )
                    )
                candidates.sort(key=lambda item: item[0])
                del candidates[MAXIMUM_CONTACTS + 1 :]
        except ParserCancelled:
            raise
        except sqlite3.Error:
            if cancellation.cancelled:
                raise ParserCancelled("parser_cancelled")
            _notice(notices, database.relative_path, "database_read_failed", "sqlite_error")

    have_more = len(candidates) > MAXIMUM_CONTACTS
    selected = candidates[:MAXIMUM_CONTACTS]
    contacts: dict[str, dict[str, Any]] = {}
    for _, relative_path, rowid, contact in selected:
        cursor["c"][relative_path] = max(cursor["c"].get(relative_path, 0), rowid)
        if contact is not None:
            contacts.setdefault(contact["wxid"], contact)
    return sorted(
        contacts.values(),
        key=lambda item: (item["display_name"].casefold(), item["wxid"]),
    ), have_more


def _read_messages(
    databases: tuple[ParserDatabaseInput, ...],
    maximum_messages: int,
    cursor: dict[str, dict[Any, Any]],
    notices: list[dict[str, str]],
    cancellation: CancellationState,
) -> tuple[list[dict[str, Any]], bool]:
    candidates: list[
        tuple[
            tuple[int, str, str, int],
            tuple[str, str],
            tuple[int, int],
            dict[str, Any],
        ]
    ] = []
    for database in databases:
        normalized = _classification_path(database.relative_path)
        if (
            not normalized.startswith("message/")
            or not normalized.endswith(".db")
            or normalized.endswith(("_fts.db", "_resource.db"))
        ):
            continue
        try:
            with _open_database(database.path, cancellation) as connection:
                sender_map = _sender_map(connection)
                hash_to_username = {
                    hashlib.md5(username.encode("utf-8")).hexdigest(): username
                    for username in sender_map.values()
                    if username
                }
                voice_map = _voice_map(connection, sender_map, notices, database.relative_path)
                tables = [
                    row[0]
                    for row in connection.execute(
                        "SELECT name FROM sqlite_master "
                        "WHERE type='table' AND name LIKE 'Msg_%' ORDER BY name"
                    )
                ]
                for table_name in tables:
                    cancellation.throw_if_cancelled()
                    quoted = _quote_identifier(table_name)
                    table_key = (database.relative_path, table_name)
                    boundary = cursor["m"].get(table_key)
                    query = (
                        f"SELECT local_id, local_type, create_time, real_sender_id, "
                        f"message_content, WCDB_CT_message_content "
                        f"FROM {quoted} "
                    )
                    parameters: tuple[Any, ...]
                    if boundary is None:
                        query += "ORDER BY create_time DESC, local_id DESC LIMIT ?"
                        parameters = (maximum_messages + 1,)
                    else:
                        query += (
                            "WHERE create_time < ? OR (create_time = ? AND local_id < ?) "
                            "ORDER BY create_time DESC, local_id DESC LIMIT ?"
                        )
                        parameters = (
                            boundary[0],
                            boundary[0],
                            boundary[1],
                            maximum_messages + 1,
                        )
                    rows = connection.execute(query, parameters)
                    table_hash = table_name[4:]
                    chat_username = hash_to_username.get(table_hash, f"unknown_{table_hash[:8]}")
                    is_group = chat_username.endswith(("@chatroom", "@openim"))
                    for row in rows:
                        cancellation.throw_if_cancelled()
                        local_id = _integer(row["local_id"])
                        message_type = _integer(row["local_type"])
                        create_time = _integer(row["create_time"])
                        sender_username = sender_map.get(_integer(row["real_sender_id"]), "")
                        is_sender = not bool(sender_username)
                        sender_target = sender_username if is_group else sender_username or chat_username
                        message: dict[str, Any] = {
                            "wxid": chat_username,
                            "local_id": local_id,
                            "content": _friendly_content(
                                message_type,
                                _message_content(
                                    row["message_content"],
                                    _integer(row["WCDB_CT_message_content"]),
                                    notices,
                                    database.relative_path,
                                ),
                            ),
                            "create_time": create_time,
                            "is_sender": is_sender,
                            "nickname": chat_username,
                            "sender": "\u6211" if is_sender else sender_target,
                            "_chat_username": chat_username,
                            "_sender_target": sender_target,
                            "avatar": "",
                            "msg_type": message_type,
                            "msg_sub_type": 0,
                            "media_type": "image" if message_type == 3 else "",
                            "media_mime": "",
                            "media_name": "",
                            "media_data": "",
                            "media_sha256": "",
                        }
                        if message_type == 34:
                            _attach_voice(
                                message,
                                voice_map.get((chat_username, local_id))
                                or voice_map.get(("", local_id)),
                                notices,
                                database.relative_path,
                            )
                        candidates.append(
                            (
                                (
                                    create_time,
                                    database.relative_path,
                                    table_name,
                                    local_id,
                                ),
                                table_key,
                                (create_time, local_id),
                                message,
                            )
                        )
                    candidates.sort(key=lambda item: item[0], reverse=True)
                    del candidates[maximum_messages + 1 :]
        except ParserCancelled:
            raise
        except sqlite3.Error:
            if cancellation.cancelled:
                raise ParserCancelled("parser_cancelled")
            _notice(notices, database.relative_path, "database_read_failed", "sqlite_error")

    have_more = len(candidates) > maximum_messages
    selected = candidates[:maximum_messages]
    for _, table_key, boundary, _ in selected:
        current = cursor["m"].get(table_key)
        if current is None or boundary < current:
            cursor["m"][table_key] = boundary
    return [message for _, _, _, message in selected], have_more


def _resolve_message_display_names(
    messages: list[dict[str, Any]],
    databases: tuple[ParserDatabaseInput, ...],
    contact_map: dict[str, dict[str, Any]],
    notices: list[dict[str, str]],
    cancellation: CancellationState,
) -> None:
    needed = {
        message["_chat_username"]
        for message in messages
        if message.get("_chat_username")
    }
    needed.update(
        message["_sender_target"]
        for message in messages
        if not message["is_sender"] and message.get("_sender_target")
    )
    missing = needed.difference(contact_map)
    if missing:
        _read_contacts_for_usernames(
            databases,
            missing,
            contact_map,
            notices,
            cancellation,
        )
    for message in messages:
        chat_username = message.pop("_chat_username", message["wxid"])
        sender_target = message.pop("_sender_target", "")
        message["nickname"] = _display_name(contact_map, chat_username)
        if not message["is_sender"]:
            message["sender"] = _display_name(contact_map, sender_target)


def _read_contacts_for_usernames(
    databases: tuple[ParserDatabaseInput, ...],
    usernames: set[str],
    contacts: dict[str, dict[str, Any]],
    notices: list[dict[str, str]],
    cancellation: CancellationState,
) -> None:
    if not usernames:
        return
    wanted = sorted(usernames)
    for database in databases:
        if Path(_classification_path(database.relative_path)).name != "contact.db":
            continue
        try:
            with _open_database(database.path, cancellation) as connection:
                for start in range(0, len(wanted), 500):
                    batch = wanted[start : start + 500]
                    placeholders = ",".join("?" for _ in batch)
                    rows = connection.execute(
                        "SELECT username, alias, remark, nick_name FROM contact "
                        f"WHERE username IN ({placeholders})",
                        batch,
                    )
                    for row in rows:
                        cancellation.throw_if_cancelled()
                        username = _text(row["username"]).strip()
                        if not username or username in contacts:
                            continue
                        alias = _text(row["alias"]).strip()
                        remark = _text(row["remark"]).strip()
                        nick_name = _text(row["nick_name"]).strip()
                        contacts[username] = {
                            "wxid": username,
                            "alias": alias,
                            "remark": remark,
                            "nick_name": nick_name,
                            "display_name": remark or nick_name or alias or username,
                            "avatar": "",
                            "source_updated_at": 0,
                            "extra_json": None,
                        }
        except ParserCancelled:
            raise
        except sqlite3.Error:
            if cancellation.cancelled:
                raise ParserCancelled("parser_cancelled")
            _notice(notices, database.relative_path, "database_read_failed", "sqlite_error")


def _read_favorites(
    databases: tuple[ParserDatabaseInput, ...],
    cursor: dict[str, dict[Any, Any]],
    notices: list[dict[str, str]],
    cancellation: CancellationState,
) -> tuple[list[dict[str, Any]], bool]:
    candidates: list[
        tuple[tuple[int, str, str], tuple[str, str], int, dict[str, Any]]
    ] = []
    for database in databases:
        filename = Path(_classification_path(database.relative_path)).name
        if "favorite" not in filename and "fav" not in filename:
            continue
        try:
            with _open_database(database.path, cancellation) as connection:
                tables = [
                    row[0]
                    for row in connection.execute(
                        "SELECT name FROM sqlite_master "
                        "WHERE type='table' AND name NOT LIKE 'sqlite_%' "
                        "AND name NOT LIKE '%fts%' ORDER BY name"
                    )
                ]
                for table_name in tables:
                    cancellation.throw_if_cancelled()
                    quoted = _quote_identifier(table_name)
                    table_key = (database.relative_path, table_name)
                    boundary = cursor["f"].get(table_key)
                    if boundary is None:
                        rows = connection.execute(
                            f"SELECT rowid AS __rowid__, * FROM {quoted} "
                            f"ORDER BY rowid DESC LIMIT ?",
                            (MAXIMUM_FAVORITES + 1,),
                        )
                    else:
                        rows = connection.execute(
                            f"SELECT rowid AS __rowid__, * FROM {quoted} "
                            f"WHERE rowid < ? ORDER BY rowid DESC LIMIT ?",
                            (boundary, MAXIMUM_FAVORITES + 1),
                        )
                    for row in rows:
                        rowid = _integer(row["__rowid__"])
                        source_id = _pick_text(
                            row,
                            "id",
                            "item_id",
                            "local_id",
                            "fav_local_id",
                            "record_id",
                            "__rowid__",
                        )
                        if not source_id:
                            continue
                        title = _pick_text(row, "title", "tag", "name", "digest", "caption")
                        summary = _pick_text(
                            row,
                            "summary",
                            "description",
                            "desc",
                            "content",
                            "source",
                            "url",
                        )
                        item_type = _pick_text(row, "item_type", "type")
                        item_sub_type = _pick_text(row, "item_sub_type", "sub_type")
                        candidates.append(
                            (
                                (rowid, database.relative_path, table_name),
                                table_key,
                                rowid,
                                {
                                    "source_table": table_name,
                                    "source_id": source_id,
                                    "title": title or summary or f"{table_name}#{source_id}",
                                    "summary": summary or title,
                                    "item_type": item_type,
                                    "item_sub_type": item_sub_type,
                                    "source_updated_at": _pick_time(row),
                                    "data_json": {
                                        key: sanitized
                                        for key in row.keys()
                                        if key != "__rowid__"
                                        and (sanitized := _sanitize_value(row[key])) not in (None, "")
                                    },
                                },
                            )
                        )
                    candidates.sort(key=lambda item: item[0], reverse=True)
                    del candidates[MAXIMUM_FAVORITES + 1 :]
        except ParserCancelled:
            raise
        except sqlite3.Error:
            if cancellation.cancelled:
                raise ParserCancelled("parser_cancelled")
            _notice(notices, database.relative_path, "database_read_failed", "sqlite_error")

    have_more = len(candidates) > MAXIMUM_FAVORITES
    selected = candidates[:MAXIMUM_FAVORITES]
    favorites: dict[tuple[str, str], dict[str, Any]] = {}
    for _, table_key, rowid, favorite in selected:
        current = cursor["f"].get(table_key)
        if current is None or rowid < current:
            cursor["f"][table_key] = rowid
        favorites.setdefault((favorite["source_table"], favorite["source_id"]), favorite)
    values = list(favorites.values())
    values.sort(
        key=lambda item: (
            -item["source_updated_at"],
            item["source_table"],
            item["source_id"],
        )
    )
    return values, have_more


@contextmanager
def _open_database(path: Path, cancellation: CancellationState):
    uri = path.as_uri() + "?mode=ro&immutable=1"
    connection = sqlite3.connect(uri, uri=True)
    try:
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA query_only=ON")
        connection.set_progress_handler(cancellation.progress_handler, 1000)
        yield connection
    finally:
        connection.close()


def _sender_map(connection: sqlite3.Connection) -> dict[int, str]:
    for candidate in ("Name2Id", "ChatName2Id"):
        if _table_exists(connection, candidate):
            quoted = _quote_identifier(candidate)
            return {
                _integer(row[0]): _text(row[1])
                for row in connection.execute(f"SELECT rowid, user_name FROM {quoted}")
            }
    return {}


def _voice_map(
    connection: sqlite3.Connection,
    sender_map: dict[int, str],
    notices: list[dict[str, str]],
    relative_path: str,
) -> dict[tuple[str, int], bytes]:
    if not _table_exists(connection, "VoiceInfo"):
        return {}
    columns = {
        row[1].lower(): row[1]
        for row in connection.execute("PRAGMA table_info(VoiceInfo)")
    }
    local_column = columns.get("local_id")
    data_column = columns.get("voice_data")
    chat_column = columns.get("chat_name_id")
    if not local_column or not data_column:
        return {}
    selected = [_quote_identifier(local_column), _quote_identifier(data_column)]
    if chat_column:
        selected.append(_quote_identifier(chat_column))
    voices: dict[tuple[str, int], bytes] = {}
    try:
        for row in connection.execute(f"SELECT {', '.join(selected)} FROM VoiceInfo"):
            raw = row[1]
            if not isinstance(raw, (bytes, bytearray)) or not raw:
                continue
            chat_username = sender_map.get(_integer(row[2]), "") if chat_column else ""
            local_id = _integer(row[0])
            if len(raw) > MAXIMUM_MEDIA_BYTES:
                _notice(notices, relative_path, "media_too_large", "voice_bytes_omitted")
                continue
            voices[(chat_username, local_id)] = bytes(raw)
    except sqlite3.Error:
        _notice(notices, relative_path, "voice_read_failed", "sqlite_error")
        return {}
    return voices


def _attach_voice(
    message: dict[str, Any],
    voice_bytes: bytes | None,
    notices: list[dict[str, str]],
    relative_path: str,
) -> None:
    message["media_type"] = "voice"
    if not voice_bytes:
        return
    if len(voice_bytes) > MAXIMUM_MEDIA_BYTES:
        _notice(notices, relative_path, "media_too_large", "voice_bytes_omitted")
        return
    message["media_mime"] = "application/octet-stream"
    message["media_name"] = f"voice_{message['create_time']}_{message['local_id']}.bin"
    message["media_data"] = base64.b64encode(voice_bytes).decode("ascii")
    message["media_sha256"] = hashlib.sha256(voice_bytes).hexdigest()


def _newest_messages(messages: list[dict[str, Any]], limit: int) -> list[dict[str, Any]]:
    unique_messages: dict[tuple[Any, ...], dict[str, Any]] = {}
    for message in messages:
        unique_messages.setdefault(_message_identity(message), message)
    result = list(unique_messages.values())
    result.sort(
        key=lambda item: (
            item["create_time"],
            item["wxid"],
            item["local_id"],
            item["sender"],
            item["content"],
        )
    )
    return result[-limit:]


def _message_identity(message: dict[str, Any]) -> tuple[Any, ...]:
    return (
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


def _friendly_content(message_type: int, content: str) -> str:
    if message_type == 1:
        return content[:500]
    if message_type == 3:
        return "[\u56fe\u7247]"
    if message_type == 34:
        return "[\u8bed\u97f3]"
    if message_type == 42:
        title = _xml_extract(content, "nickname")
        return f"[\u540d\u7247: {title}]" if title else "[\u540d\u7247]"
    if message_type == 43:
        return "[\u89c6\u9891]"
    if message_type == 47:
        return "[\u8868\u60c5\u5305]"
    if message_type == 48:
        label = _xml_extract(content, "label")
        return f"[\u4f4d\u7f6e: {label}]" if label else "[\u4f4d\u7f6e]"
    if message_type == 49:
        title = _xml_extract(content, "title")
        return f"[\u5206\u4eab: {title}]" if title else "[\u6587\u4ef6/\u94fe\u63a5]"
    if message_type in (10000, 10002):
        return f"[\u7cfb\u7edf: {content[:100]}]"
    return content[:200]


def _message_content(
    raw: Any,
    compression_flag: int,
    notices: list[dict[str, str]],
    relative_path: str,
) -> str:
    if raw is None:
        return ""
    if isinstance(raw, bytes):
        if compression_flag == 4:
            try:
                content_size = zstd.frame_content_size(raw)
                if content_size == zstd.CONTENTSIZE_ERROR:
                    raise zstd.ZstdError("invalid frame")
                if (
                    content_size != zstd.CONTENTSIZE_UNKNOWN
                    and content_size > MAXIMUM_DECOMPRESSED_MESSAGE_BYTES
                ):
                    _notice(
                        notices,
                        relative_path,
                        "message_decode_failed",
                        "zstd_output_too_large",
                    )
                    return ""
                raw = zstd.ZstdDecompressor().decompress(
                    raw,
                    max_output_size=MAXIMUM_DECOMPRESSED_MESSAGE_BYTES,
                )
            except zstd.ZstdError:
                _notice(
                    notices,
                    relative_path,
                    "message_decode_failed",
                    "zstd_invalid",
                )
                return ""
            if len(raw) > MAXIMUM_DECOMPRESSED_MESSAGE_BYTES:
                _notice(
                    notices,
                    relative_path,
                    "message_decode_failed",
                    "zstd_output_too_large",
                )
                return ""
        return raw.decode("utf-8", errors="replace")
    return str(raw)


def _xml_extract(content: str, tag: str) -> str:
    match = re.search(rf"<{tag}>(.*?)</{tag}>", content, re.DOTALL)
    return match.group(1).strip() if match else ""


def _display_name(contact_map: dict[str, dict[str, Any]], username: str) -> str:
    info = contact_map.get(username, {})
    return (
        info.get("display_name")
        or info.get("remark")
        or info.get("nick_name")
        or info.get("alias")
        or username
    )


def _table_exists(connection: sqlite3.Connection, table_name: str) -> bool:
    return connection.execute(
        "SELECT 1 FROM sqlite_master WHERE type='table' AND name=?",
        (table_name,),
    ).fetchone() is not None


def _quote_identifier(value: str) -> str:
    return '"' + value.replace('"', '""') + '"'


def _pick_text(row: sqlite3.Row, *names: str) -> str:
    keys = set(row.keys())
    for name in names:
        if name in keys:
            value = _text(row[name]).strip()
            if value:
                return value
    return ""


def _pick_time(row: sqlite3.Row) -> int:
    keys = set(row.keys())
    for name in ("update_time", "updated_time", "create_time", "time", "timestamp"):
        if name in keys:
            value = _integer(row[name])
            if value > 0:
                return value
    return 0


def _sanitize_value(value: Any) -> Any:
    if isinstance(value, float) and not math.isfinite(value):
        return None
    if value is None or isinstance(value, (int, float, bool)):
        return value
    if isinstance(value, bytes):
        return {"kind": "bytes", "size": len(value)}
    text = str(value)
    return text if len(text) <= 300 else text[:300] + "..."


def _text(value: Any) -> str:
    return "" if value is None else str(value)


def _integer(value: Any) -> int:
    try:
        return int(value or 0)
    except (TypeError, ValueError, OverflowError):
        return 0


def _notice(
    notices: list[dict[str, str]],
    database: str,
    code: str,
    detail: str,
) -> None:
    notice = {"code": code, "database": database, "detail": detail}
    if len(notices) < MAXIMUM_NOTICES and notice not in notices:
        notices.append(notice)
