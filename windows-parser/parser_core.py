import base64
import hashlib
import re
import sqlite3
from contextlib import contextmanager
from pathlib import Path
from typing import Any

from parser_contract import (
    MAXIMUM_CONTACTS,
    MAXIMUM_FAVORITES,
    MAXIMUM_MEDIA_BYTES,
    MAXIMUM_NOTICES,
    CancellationState,
    ParserCancelled,
    ParserDatabaseInput,
    ParserJob,
)


def parse_job(job: ParserJob, cancellation: CancellationState) -> dict[str, Any]:
    notices: list[dict[str, str]] = []
    contacts = _read_contacts(job.databases, notices, cancellation)
    contact_map = {item["wxid"]: item for item in contacts}
    messages = _read_messages(
        job.databases,
        contact_map,
        job.maximum_messages,
        notices,
        cancellation,
    )
    favorites = _read_favorites(job.databases, notices, cancellation)
    notices.sort(key=lambda item: (item["database"], item["code"], item["detail"]))
    return {
        "schemaVersion": 1,
        "jobId": job.job_id,
        "sourceSetId": job.source_set_id,
        "messages": messages,
        "contacts": contacts,
        "favorites": favorites,
        "notices": notices[:MAXIMUM_NOTICES],
    }


def _read_contacts(
    databases: tuple[ParserDatabaseInput, ...],
    notices: list[dict[str, str]],
    cancellation: CancellationState,
) -> list[dict[str, Any]]:
    contacts: dict[str, dict[str, Any]] = {}
    for database in databases:
        if Path(database.relative_path).name.lower() != "contact.db":
            continue
        try:
            with _open_database(database.path, cancellation) as connection:
                rows = connection.execute(
                    "SELECT username, alias, remark, nick_name FROM contact LIMIT ?",
                    (MAXIMUM_CONTACTS + 1,),
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
                    if len(contacts) >= MAXIMUM_CONTACTS:
                        break
        except ParserCancelled:
            raise
        except sqlite3.Error:
            if cancellation.cancelled:
                raise ParserCancelled("parser_cancelled")
            _notice(notices, database.relative_path, "database_read_failed", "sqlite_error")
    return sorted(
        contacts.values(),
        key=lambda item: (item["display_name"].casefold(), item["wxid"]),
    )[:MAXIMUM_CONTACTS]


def _read_messages(
    databases: tuple[ParserDatabaseInput, ...],
    contact_map: dict[str, dict[str, Any]],
    maximum_messages: int,
    notices: list[dict[str, str]],
    cancellation: CancellationState,
) -> list[dict[str, Any]]:
    messages: list[dict[str, Any]] = []
    for database in databases:
        normalized = database.relative_path.lower()
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
                    rows = connection.execute(
                        f"SELECT local_id, local_type, create_time, real_sender_id, "
                        f"message_content, WCDB_CT_message_content "
                        f"FROM {quoted} "
                        f"ORDER BY create_time DESC, local_id DESC LIMIT ?",
                        (maximum_messages,),
                    )
                    table_hash = table_name[4:]
                    chat_username = hash_to_username.get(table_hash, f"unknown_{table_hash[:8]}")
                    chat_display_name = _display_name(contact_map, chat_username)
                    is_group = chat_username.endswith(("@chatroom", "@openim"))
                    for row in rows:
                        cancellation.throw_if_cancelled()
                        local_id = _integer(row["local_id"])
                        message_type = _integer(row["local_type"])
                        create_time = _integer(row["create_time"])
                        sender_username = sender_map.get(_integer(row["real_sender_id"]), "")
                        is_sender = not bool(sender_username)
                        sender_target = sender_username if is_group else sender_username or chat_username
                        message = {
                            "wxid": chat_username,
                            "local_id": local_id,
                            "content": _friendly_content(
                                message_type,
                                _message_content(
                                    row["message_content"],
                                    _integer(row["WCDB_CT_message_content"]),
                                ),
                            ),
                            "create_time": create_time,
                            "is_sender": is_sender,
                            "nickname": chat_display_name,
                            "sender": "\u6211" if is_sender else _display_name(contact_map, sender_target),
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
                        messages.append(message)
                    messages = _newest_messages(messages, maximum_messages)
        except ParserCancelled:
            raise
        except sqlite3.Error:
            if cancellation.cancelled:
                raise ParserCancelled("parser_cancelled")
            _notice(notices, database.relative_path, "database_read_failed", "sqlite_error")
    return _newest_messages(messages, maximum_messages)


def _read_favorites(
    databases: tuple[ParserDatabaseInput, ...],
    notices: list[dict[str, str]],
    cancellation: CancellationState,
) -> list[dict[str, Any]]:
    favorites: list[dict[str, Any]] = []
    seen: set[tuple[str, str]] = set()
    for database in databases:
        filename = Path(database.relative_path).name.lower()
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
                    rows = connection.execute(
                        f"SELECT rowid AS __rowid__, * FROM {quoted} "
                        f"ORDER BY rowid DESC LIMIT ?",
                        (MAXIMUM_FAVORITES,),
                    )
                    for row in rows:
                        source_id = _pick_text(
                            row,
                            "id",
                            "item_id",
                            "local_id",
                            "fav_local_id",
                            "record_id",
                            "__rowid__",
                        )
                        if not source_id or (table_name, source_id) in seen:
                            continue
                        seen.add((table_name, source_id))
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
                        favorites.append(
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
                            }
                        )
                        if len(favorites) >= MAXIMUM_FAVORITES:
                            break
                    if len(favorites) >= MAXIMUM_FAVORITES:
                        break
        except ParserCancelled:
            raise
        except sqlite3.Error:
            if cancellation.cancelled:
                raise ParserCancelled("parser_cancelled")
            _notice(notices, database.relative_path, "database_read_failed", "sqlite_error")
    favorites.sort(
        key=lambda item: (
            -item["source_updated_at"],
            item["source_table"],
            item["source_id"],
        )
    )
    return favorites[:MAXIMUM_FAVORITES]


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
    messages.sort(
        key=lambda item: (
            item["create_time"],
            item["wxid"],
            item["local_id"],
            item["sender"],
            item["content"],
        )
    )
    return messages[-limit:]


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


def _message_content(raw: Any, _compression_flag: int) -> str:
    if raw is None:
        return ""
    if isinstance(raw, bytes):
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
