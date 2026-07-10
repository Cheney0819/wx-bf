import ctypes
import ctypes.wintypes as wt
import glob
import hashlib
import hmac as hmac_mod
import json
import os
import re
import shutil
import sqlite3
import struct
import subprocess
import tempfile
import time
import gc
from base64 import b64encode
from contextlib import closing
from pathlib import Path

from Crypto.Cipher import AES

try:
    import pilk
except Exception:  # pragma: no cover
    pilk = None

try:
    import zstandard as zstd
except Exception:  # pragma: no cover
    zstd = None

PAGE_SZ = 4096
KEY_SZ = 32
SALT_SZ = 16
IV_SZ = 16
HMAC_SZ = 64
RESERVE_SZ = 80
SQLITE_HDR = b"SQLite format 3\x00"
MAX_IMAGE_BYTES = 5 * 1024 * 1024
MAX_AVATAR_BYTES = 512 * 1024
MAX_VOICE_MP3_BYTES = 2 * 1024 * 1024
VOICE_SAMPLE_RATE = 24000
V2_MAGIC_FULL = b"\x07\x08V2\x08\x07"
V1_MAGIC_FULL = b"\x07\x08V1\x08\x07"
IMAGE_KEY_MONITOR_TIMEOUT_SECONDS = max(
    0,
    int((os.environ.get("WECHAT_IMAGE_KEY_MONITOR_TIMEOUT_SECONDS") or "25").strip() or "25"),
)
IMAGE_KEY_MONITOR_SCAN_INTERVAL_SECONDS = max(
    0.5,
    float((os.environ.get("WECHAT_IMAGE_KEY_MONITOR_SCAN_INTERVAL_SECONDS") or "1.5").strip() or "1.5"),
)
CHATLOG_MEDIA_BUDGET_SECONDS = max(
    10,
    int((os.environ.get("WECHAT_CHATLOG_MEDIA_BUDGET_SECONDS") or "120").strip() or "120"),
)
DATABASE_SNAPSHOT_MAX_ATTEMPTS = 3
IMAGE_KEY_CONFIG_FILE_NAME = "image_crypto.json"

IMAGE_MAGIC = {
    "png": [0x89, 0x50, 0x4E, 0x47],
    "gif": [0x47, 0x49, 0x46, 0x38],
    "tif": [0x49, 0x49, 0x2A, 0x00],
    "webp": [0x52, 0x49, 0x46, 0x46],
    "jpg": [0xFF, 0xD8, 0xFF],
}

IMAGE_MIME = {
    "jpg": "image/jpeg",
    "png": "image/png",
    "gif": "image/gif",
    "bmp": "image/bmp",
    "webp": "image/webp",
    "tif": "image/tiff",
    "hevc": "video/hevc",
}

kernel32 = ctypes.windll.kernel32
MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100
_READABLE_PROTECT_MASK = 0x02 | 0x04 | 0x08 | 0x10 | 0x20 | 0x40 | 0x80

_HEX_RE = re.compile(b"x'([0-9a-fA-F]{64,192})'")
_HEX_BARE_RE = re.compile(b"(?<![0-9a-fA-F])[0-9a-fA-F]{64,128}(?![0-9a-fA-F])")
_ZSTD = zstd.ZstdDecompressor() if zstd is not None else None
_IMAGE_KEY32_RE = re.compile(rb'(?<![a-zA-Z0-9])[a-zA-Z0-9]{32}(?![a-zA-Z0-9])')
_IMAGE_KEY16_RE = re.compile(rb'(?<![a-zA-Z0-9])[a-zA-Z0-9]{16}(?![a-zA-Z0-9])')
_IMAGE_RW_PROTECT_MASK = 0x04 | 0x08 | 0x40 | 0x80
_IMAGE_DAT_INDEX_CACHE = {}
_EXPORT_RELEVANT_DB_BASENAMES = {
    "contact.db",
    "favorite.db",
    "favorites.db",
    "head_image.db",
    "headimage.db",
}


class MBI(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_uint64),
        ("AllocationBase", ctypes.c_uint64),
        ("AllocationProtect", wt.DWORD),
        ("_pad1", wt.DWORD),
        ("RegionSize", ctypes.c_uint64),
        ("State", wt.DWORD),
        ("Protect", wt.DWORD),
        ("Type", wt.DWORD),
        ("_pad2", wt.DWORD),
    ]


def extract_database_keys_windows(db_dir: str, preferred_pid: int = 0, log_fn=None) -> dict:
    db_files, salt_to_dbs = collect_db_files(db_dir)
    if not db_files:
        raise RuntimeError(f"未在 {db_dir} 中找到可解密数据库")

    pids = get_wechat_pids(preferred_pid)
    key_map = {}
    remaining_salts = set(salt_to_dbs.keys())
    total_hex_matches = 0
    started_at = time.time()
    selected_pid = 0

    for pid, mem_kb, process_name in pids:
        handle = kernel32.OpenProcess(0x0010 | 0x0400, False, pid)
        if not handle:
            continue

        selected_pid = selected_pid or pid
        try:
            regions = enum_regions(handle)
            total_bytes = sum(size for _, size in regions)
            scanned_bytes = 0
            for reg_idx, (base, size) in enumerate(regions):
                data = read_mem(handle, base, size)
                scanned_bytes += size
                if not data:
                    continue

                total_hex_matches += scan_memory_for_keys(
                    data=data,
                    db_files=db_files,
                    salt_to_dbs=salt_to_dbs,
                    key_map=key_map,
                    remaining_salts=remaining_salts,
                    base_addr=base,
                    pid=pid,
                    process_name=process_name,
                    log_fn=log_fn,
                )

                if log_fn and (reg_idx + 1) % 200 == 0 and total_bytes > 0:
                    progress = scanned_bytes / total_bytes * 100
                    log_fn(
                        f"[wechat-decrypt] 扫描 PID={pid} 进度 {progress:.1f}% "
                        f"({len(key_map)}/{len(salt_to_dbs)} salts)"
                    )
        finally:
            kernel32.CloseHandle(handle)

        if not remaining_salts:
            break

    cross_verify_keys(db_files, salt_to_dbs, key_map)
    elapsed = round(time.time() - started_at, 3)

    result = {}
    missing = []
    for rel, _, size, salt_hex, _ in db_files:
        if salt_hex in key_map:
            result[rel] = {
                "enc_key": key_map[salt_hex],
                "salt": salt_hex,
                "size_mb": round(size / 1024 / 1024, 3),
            }
        else:
            missing.append(rel)

    if not result:
        raise RuntimeError("未能从微信进程内存中匹配到任何数据库密钥")

    return {
        "success": True,
        "selected_pid": selected_pid,
        "db_count": len(db_files),
        "matched_count": len(result),
        "missing_count": len(missing),
        "missing_dbs": missing,
        "total_hex_matches": total_hex_matches,
        "duration_seconds": elapsed,
        "keys": result,
    }


def _emit(event_fn, event_name: str, payload: dict):
    if event_fn is None:
        return
    try:
        event_fn(event_name, payload)
    except Exception:
        pass


def decrypt_database_tree(db_dir: str, out_dir: str, keys: dict, log_fn=None, event_fn=None, wechat_running: bool = False) -> dict:
    started_at = time.time()
    success = 0
    failed = 0
    skipped = 0
    total_bytes = 0
    wal_applied = 0
    wal_skipped = 0
    integrity_failed = 0
    required_integrity_failed = 0

    with tempfile.TemporaryDirectory(prefix="wx-db-snapshot-") as snapshot_dir:
        snapshot_result = create_stable_database_snapshot(
            db_dir,
            keys,
            snapshot_dir,
            max_attempts=DATABASE_SNAPSHOT_MAX_ATTEMPTS,
            log_fn=log_fn,
            event_fn=event_fn,
        )
        if not snapshot_result["success"]:
            failure_reason = snapshot_result["reason"]
            _emit(event_fn, "client_extract_failed", {
                "stage": "database_snapshot",
                "reason": failure_reason,
                "error_message": "微信数据库持续写入，未取得一致快照",
                "snapshot_attempts": snapshot_result["attempts"],
            })
            result = {
                "success": False,
                "success_count": 0,
                "failed_count": 1,
                "skipped_count": 0,
                "wal_applied_count": 0,
                "wal_skipped_count": snapshot_result["modified_db_count"],
                "integrity_failed_count": 0,
                "snapshot_attempts": snapshot_result["attempts"],
                "failure_reason": failure_reason,
                "total_bytes": 0,
                "duration_seconds": round(time.time() - started_at, 3),
            }
            _emit(event_fn, "client_decrypt_tree_done", result)
            return result

        db_files = collect_snapshot_database_files(snapshot_dir)
        for rel, path, size in db_files:
            key_info = keys.get(rel)
            if not key_info:
                skipped += 1
                _emit(event_fn, "client_decrypt_db_result", {
                    "db_rel": rel, "db_size_bytes": size,
                    "result": "skipped", "reason": "no_key",
                })
                continue

            out_path = os.path.join(out_dir, rel)
            enc_key = bytes.fromhex(str(key_info["enc_key"]))
            ok = decrypt_database(path, out_path, enc_key)
            if not ok:
                failed += 1
                if log_fn:
                    log_fn(f"[wechat-decrypt] 解密失败: {rel}")
                _emit(event_fn, "client_decrypt_db_result", {
                    "db_rel": rel, "db_size_bytes": size,
                    "result": "failed", "reason": "hmac_or_decrypt",
                })
                continue

            wal_result = apply_encrypted_wal_to_decrypted_db(path, out_path, enc_key, log_fn=log_fn)
            if wal_result.get("applied", 0) > 0:
                wal_applied += 1
            elif wal_result.get("present"):
                wal_skipped += 1
                _emit(event_fn, "client_wal_merge_skipped", {
                    "db_rel": rel,
                    "reason": wal_result.get("reason") or "wal_not_applied",
                    "snapshot_attempts": snapshot_result["attempts"],
                })

            valid, validation_reason = validate_decrypted_database(out_path)
            if not valid:
                failed += 1
                integrity_failed += 1
                if is_required_chatlog_database(rel):
                    required_integrity_failed += 1
                try:
                    os.remove(out_path)
                except OSError:
                    pass
                if log_fn:
                    log_fn(f"[wechat-decrypt] 明文库校验失败: {rel} ({validation_reason})")
                _emit(event_fn, "client_decrypt_db_result", {
                    "db_rel": rel,
                    "db_size_bytes": size,
                    "result": "failed",
                    "reason": "sqlite_integrity_check_failed",
                    "validation_reason": validation_reason,
                    "wal_applied": wal_result.get("applied", 0),
                    "wal_present": wal_result.get("present", False),
                })
                continue

            success += 1
            total_bytes += size
            if log_fn:
                log_fn(f"[wechat-decrypt] 解密成功: {rel}")
            _emit(event_fn, "client_decrypt_db_result", {
                "db_rel": rel,
                "db_size_bytes": size,
                "result": "success",
                "wal_applied": wal_result.get("applied", 0),
                "wal_present": wal_result.get("present", False),
                "wal_committed_frames": wal_result.get("committed_frames", 0),
                "snapshot_attempts": snapshot_result["attempts"],
                "integrity_checked": True,
            })

    result = {
        "success": success > 0 and required_integrity_failed == 0,
        "success_count": success,
        "failed_count": failed,
        "skipped_count": skipped,
        "wal_applied_count": wal_applied,
        "wal_skipped_count": wal_skipped,
        "integrity_failed_count": integrity_failed,
        "required_integrity_failed_count": required_integrity_failed,
        "snapshot_attempts": snapshot_result["attempts"],
        "total_bytes": total_bytes,
        "duration_seconds": round(time.time() - started_at, 3),
    }
    _emit(event_fn, "client_decrypt_tree_done", result)
    return result


def collect_snapshot_source_database_files(db_dir: str, keys: dict):
    files = []
    for root, _, names in os.walk(db_dir):
        for name in names:
            if not name.endswith(".db") or name.endswith(("-wal", "-shm")):
                continue
            path = os.path.join(root, name)
            rel = os.path.relpath(path, db_dir)
            if not is_export_relevant_db(rel) or rel not in keys:
                continue
            files.append((rel, path))
    return sorted(files, key=lambda item: item[0].lower())


def is_required_chatlog_database(rel_path: str) -> bool:
    rel_norm = str(rel_path or "").replace("\\", "/").lower()
    return bool(re.fullmatch(r"message/message_\d+\.db", rel_norm))


def collect_snapshot_database_files(snapshot_dir: str):
    files = []
    for root, _, names in os.walk(snapshot_dir):
        for name in names:
            if not name.endswith(".db") or name.endswith(("-wal", "-shm")):
                continue
            path = os.path.join(root, name)
            rel = os.path.relpath(path, snapshot_dir)
            files.append((rel, path, os.path.getsize(path)))
    return sorted(files, key=lambda item: item[2])


def snapshot_file_signature(path: str):
    try:
        stat = os.stat(path)
        return stat.st_size, stat.st_mtime_ns
    except OSError:
        return None


def reset_snapshot_directory(snapshot_dir: str):
    shutil.rmtree(snapshot_dir, ignore_errors=True)
    os.makedirs(snapshot_dir, exist_ok=True)


def create_stable_database_snapshot(
    db_dir: str,
    keys: dict,
    snapshot_dir: str,
    max_attempts: int = DATABASE_SNAPSHOT_MAX_ATTEMPTS,
    log_fn=None,
    event_fn=None,
):
    db_files = collect_snapshot_source_database_files(db_dir, keys)
    if not db_files:
        return {
            "success": False,
            "reason": "snapshot_candidates_missing",
            "attempts": 0,
            "modified_db_count": 0,
        }

    max_attempts = max(1, max_attempts)
    for attempt in range(1, max_attempts + 1):
        reset_snapshot_directory(snapshot_dir)
        before = {}
        for rel, source_path in db_files:
            for suffix in ("", "-wal", "-shm"):
                before[(rel, suffix)] = snapshot_file_signature(source_path + suffix)

        changed_rels = set()
        try:
            for rel, source_path in db_files:
                for suffix in ("", "-wal", "-shm"):
                    source_file = source_path + suffix
                    signature = before[(rel, suffix)]
                    if signature is None:
                        continue
                    snapshot_file = os.path.join(snapshot_dir, rel) + suffix
                    os.makedirs(os.path.dirname(snapshot_file), exist_ok=True)
                    shutil.copyfile(source_file, snapshot_file)
                    copied_signature = snapshot_file_signature(snapshot_file)
                    if copied_signature is None or copied_signature[0] != signature[0]:
                        changed_rels.add(rel)
        except OSError:
            changed_rels.update(rel for rel, _ in db_files)

        for rel, source_path in db_files:
            for suffix in ("", "-wal", "-shm"):
                if before[(rel, suffix)] != snapshot_file_signature(source_path + suffix):
                    changed_rels.add(rel)

        if not changed_rels:
            if log_fn:
                log_fn(
                    f"[wechat-decrypt] 已获取稳定数据库快照: {len(db_files)} 个库，尝试 {attempt}/{max_attempts}"
                )
            return {
                "success": True,
                "reason": "snapshot_stable",
                "attempts": attempt,
                "modified_db_count": 0,
            }

        for rel in sorted(changed_rels)[:12]:
            _emit(event_fn, "client_wal_merge_skipped", {
                "db_rel": rel,
                "reason": "source_modified",
                "snapshot_attempt": attempt,
                "snapshot_attempt_total": max_attempts,
            })
        if log_fn:
            log_fn(
                f"[wechat-decrypt] 数据库快照不稳定，准备重试 {attempt}/{max_attempts}: "
                f"{', '.join(sorted(changed_rels)[:3])}"
            )
        if attempt < max_attempts:
            time.sleep(0.2)

    return {
        "success": False,
        "reason": "source_modified",
        "attempts": max_attempts,
        "modified_db_count": len(changed_rels),
    }


def validate_decrypted_database(db_path: str):
    try:
        db_uri = Path(db_path).resolve().as_uri() + "?mode=ro"
        with closing(sqlite3.connect(db_uri, uri=True)) as conn:
            quick_check = conn.execute("PRAGMA quick_check(1)").fetchone()
            quick_check_value = str(quick_check[0] if quick_check else "").strip().lower()
            if quick_check_value != "ok":
                return False, f"quick_check:{quick_check_value or 'empty'}"
            conn.execute("SELECT name FROM sqlite_master LIMIT 1").fetchone()
    except Exception as exc:
        return False, f"sqlite_open_failed:{exc}"
    return True, "ok"


def decrypt_database_to_bytes(db_path: str, enc_key: bytes):
    file_size = os.path.getsize(db_path)
    total_pages = file_size // PAGE_SZ
    if file_size % PAGE_SZ != 0:
        total_pages += 1

    with open(db_path, "rb") as fin:
        page1 = fin.read(PAGE_SZ)
    if len(page1) < PAGE_SZ:
        return None

    salt = page1[:SALT_SZ]
    mac_key = derive_mac_key(enc_key, salt)
    hmac_data = page1[SALT_SZ : PAGE_SZ - RESERVE_SZ + IV_SZ]
    stored_hmac = page1[PAGE_SZ - HMAC_SZ : PAGE_SZ]
    hm = hmac_mod.new(mac_key, hmac_data, hashlib.sha512)
    hm.update(struct.pack("<I", 1))
    if hm.digest() != stored_hmac:
        return None

    output = bytearray(total_pages * PAGE_SZ)
    with open(db_path, "rb") as fin:
        for page_number in range(1, total_pages + 1):
            page = fin.read(PAGE_SZ)
            if len(page) < PAGE_SZ:
                if len(page) == 0:
                    break
                page = page + b"\x00" * (PAGE_SZ - len(page))
            decrypted = decrypt_page(enc_key, page, page_number)
            start = (page_number - 1) * PAGE_SZ
            output[start : start + PAGE_SZ] = decrypted
    return bytes(output[:file_size])


def open_memory_database(db_bytes: bytes):
    conn = sqlite3.connect(":memory:")
    conn.row_factory = sqlite3.Row
    conn.deserialize(db_bytes)
    return conn


def is_export_relevant_db(rel_path: str) -> bool:
    rel_norm = str(rel_path or "").replace("\\", "/").lower()
    if rel_norm.startswith("message/"):
        # Full-text and resource indexes are not read by the chat exporter.
        return not rel_norm.endswith(("_fts.db", "_resource.db"))
    return os.path.basename(rel_norm) in _EXPORT_RELEVANT_DB_BASENAMES


def export_chatlog_json(
    decrypted_dir: str,
    output_path: str,
    max_messages: int = 5000,
    source_data_dir: str = "",
    preferred_pid: int = 0,
    log_fn=None,
    event_fn=None,
) -> list[dict]:
    media_budget_deadline = time.time() + CHATLOG_MEDIA_BUDGET_SECONDS
    contact_records = load_contact_records(decrypted_dir, log_fn=log_fn, event_fn=event_fn)
    contact_map = contact_records
    messages = []
    readable_db_count = 0
    readable_table_count = 0
    skipped_db_errors = []

    message_dir = os.path.join(decrypted_dir, "message")
    if not os.path.isdir(message_dir):
        raise RuntimeError(f"未找到明文消息目录: {message_dir}")

    db_files = sorted(
        path
        for path in (
            os.path.join(message_dir, name)
            for name in os.listdir(message_dir)
        )
        if path.endswith(".db") and not path.endswith(("_fts.db", "_resource.db", "-wal", "-shm"))
    )

    try:
        for db_path in db_files:
            try:
                with closing(sqlite3.connect(db_path)) as conn:
                    conn.row_factory = sqlite3.Row
                    sender_map = load_sender_map(conn)
                    hash_to_username = {
                        hashlib.md5(username.encode()).hexdigest(): username
                        for username in sender_map.values()
                        if username
                    }

                    all_tables = [
                        row[0]
                        for row in conn.execute(
                            "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Msg_%'"
                        )
                    ]
                    readable_db_count += 1

                    for table_name in all_tables:
                        try:
                            current_min_kept_create_time = 0
                            if len(messages) >= max_messages:
                                current_min_kept_create_time = min(
                                    int(item.get("create_time") or 0)
                                    for item in messages
                                )
                                latest_create_time_row = conn.execute(
                                    f"SELECT MAX(create_time) FROM [{table_name}]"
                                ).fetchone()
                                latest_create_time = int(
                                    (latest_create_time_row[0] if latest_create_time_row else 0) or 0
                                )
                                if latest_create_time and latest_create_time < current_min_kept_create_time:
                                    continue

                            chat_username = hash_to_username.get(table_name[4:], f"unknown_{table_name[4:12]}")
                            chat_display_name = display_name(contact_map, chat_username)
                            is_group = chat_username.endswith("@chatroom") or chat_username.endswith("@openim")

                            if current_min_kept_create_time:
                                rows = conn.execute(
                                    f"SELECT local_id, local_type, create_time, real_sender_id, "
                                    f"message_content, WCDB_CT_message_content "
                                    f"FROM [{table_name}] "
                                    f"WHERE create_time >= ? "
                                    f"ORDER BY create_time DESC LIMIT 1000",
                                    (current_min_kept_create_time,),
                                ).fetchall()
                            else:
                                rows = conn.execute(
                                    f"SELECT local_id, local_type, create_time, real_sender_id, "
                                    f"message_content, WCDB_CT_message_content "
                                    f"FROM [{table_name}] ORDER BY create_time DESC LIMIT 1000"
                                ).fetchall()
                            readable_table_count += 1

                            for row in rows:
                                local_id = int(row["local_id"] or 0)
                                local_type = int(row["local_type"] or 0)
                                create_time = int(row["create_time"] or 0)
                                if current_min_kept_create_time and create_time < current_min_kept_create_time:
                                    break
                                raw_content = row["message_content"]
                                ct_flag = int(row["WCDB_CT_message_content"] or 0)
                                content = get_content(raw_content, ct_flag)
                                sender_username = sender_map.get(int(row["real_sender_id"] or 0), "")
                                is_sender = not bool(sender_username)
                                sender_name = "我" if is_sender else display_name(
                                    contact_map,
                                    sender_username if is_group else (sender_username or chat_username),
                                )
                                messages.append(
                                    {
                                        "wxid": chat_username,
                                        "local_id": local_id,
                                        "content": friendly_content(local_type, content),
                                        "create_time": create_time,
                                        "is_sender": is_sender,
                                        "nickname": chat_display_name,
                                        "sender": sender_name,
                                        "avatar": (
                                            contact_records.get(chat_username, {}).get("avatar", "")
                                            if not is_sender
                                            else ""
                                        ),
                                        "msg_type": local_type,
                                        "msg_sub_type": 0,
                                        "media_type": "",
                                        "media_mime": "",
                                        "media_name": "",
                                        "media_data": "",
                                    }
                                )
                                trim_message_buffer(messages, max_messages, buffer_factor=1)
                                if len(messages) >= max_messages:
                                    current_min_kept_create_time = min(
                                        int(item.get("create_time") or 0)
                                        for item in messages
                                    )
                        except Exception as exc:
                            skipped_db_errors.append(f"{os.path.basename(db_path)}::{table_name}: {exc}")
                            if log_fn:
                                log_fn(
                                    f"[wechat-decrypt] 跳过损坏消息表: {os.path.basename(db_path)}::{table_name} ({exc})"
                                )
                            continue
            except Exception as exc:
                skipped_db_errors.append(f"{os.path.basename(db_path)}: {exc}")
                if log_fn:
                    log_fn(f"[wechat-decrypt] 跳过损坏消息库: {db_path} ({exc})")
                continue
    finally:
        pass

    if not messages and db_files and skipped_db_errors and readable_db_count == 0:
        raise RuntimeError(skipped_db_errors[-1])

    if not messages and db_files and skipped_db_errors and readable_table_count == 0:
        raise RuntimeError(skipped_db_errors[-1])

    messages.sort(key=lambda item: item["create_time"])
    if len(messages) > max_messages:
        messages = messages[-max_messages:]

    enrich_chatlog_media(
        messages=messages,
        decrypted_dir=decrypted_dir,
        source_data_dir=source_data_dir,
        preferred_pid=preferred_pid,
        media_budget_deadline=media_budget_deadline,
        log_fn=log_fn,
        event_fn=event_fn,
    )

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as handle:
        json.dump(messages, handle, ensure_ascii=False)

    return messages


def export_contacts_json(decrypted_dir: str, output_path: str, log_fn=None, event_fn=None) -> list[dict]:
    contacts = list(load_contact_records(decrypted_dir, log_fn=log_fn, event_fn=event_fn).values())
    contacts.sort(key=lambda item: (item.get("display_name") or item.get("wxid") or "").lower())

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as handle:
        json.dump(contacts, handle, ensure_ascii=False)
    return contacts


def export_favorites_json(decrypted_dir: str, output_path: str, log_fn=None, event_fn=None, max_items: int = 1000) -> list[dict]:
    favorites = load_favorite_records(decrypted_dir, log_fn=log_fn, event_fn=event_fn, max_items=max_items)
    favorites.sort(key=lambda item: int(item.get("source_updated_at") or 0), reverse=True)

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as handle:
        json.dump(favorites, handle, ensure_ascii=False)
    return favorites


def export_chatlog_memory(db_dir: str, keys: dict, max_messages: int = 5000, log_fn=None, event_fn=None):
    started_at = time.time()
    db_files = []
    for root, _, files in os.walk(db_dir):
        for name in files:
            if not name.endswith(".db") or name.endswith("-wal") or name.endswith("-shm"):
                continue
            path = os.path.join(root, name)
            rel = os.path.relpath(path, db_dir)
            db_files.append((rel, path, os.path.getsize(path)))

    db_files.sort(key=lambda item: item[0].lower())

    contact_map = {}
    for rel, path, size in db_files:
        rel_norm = rel.replace("\\", "/").lower()
        if rel_norm not in {"contact/contact.db", "contact/contact_fts.db"}:
            continue
        key_info = keys.get(rel)
        if not key_info:
            continue
        if event_fn:
            event_fn("client_memory_db_progress", {
                "stage": "contact_map",
                "db_rel": rel,
                "db_size_bytes": size,
            })
        db_bytes = decrypt_database_to_bytes(path, bytes.fromhex(str(key_info["enc_key"])))
        if not db_bytes:
            continue
        with closing(open_memory_database(db_bytes)) as conn:
            for username, alias, remark, nick_name in conn.execute(
                "SELECT username, alias, remark, nick_name FROM contact"
            ):
                contact_map[username] = {
                    "alias": alias or "",
                    "remark": remark or "",
                    "nick_name": nick_name or "",
                }
        del db_bytes
        gc.collect()
        break

    messages = []
    message_db_candidates = [
        (rel, path, size)
        for rel, path, size in db_files
        if rel.replace("\\", "/").lower().startswith("message/")
        and rel.lower().endswith(".db")
        and not rel.lower().endswith(("_fts.db", "_resource.db"))
    ]

    peak_db_bytes = 0
    processed_db_count = 0
    for index, (rel, path, size) in enumerate(message_db_candidates, start=1):
        key_info = keys.get(rel)
        if not key_info:
            continue

        if event_fn:
            event_fn("client_memory_db_progress", {
                "stage": "decrypting_message_db",
                "db_rel": rel,
                "db_index": index,
                "db_total": len(message_db_candidates),
                "db_size_bytes": size,
            })

        db_bytes = decrypt_database_to_bytes(path, bytes.fromhex(str(key_info["enc_key"])))
        if not db_bytes:
            if log_fn:
                log_fn(f"[wechat-decrypt] 内存解密失败: {rel}")
            if event_fn:
                event_fn("client_memory_db_progress", {
                    "stage": "decrypt_failed",
                    "db_rel": rel,
                    "db_index": index,
                    "db_total": len(message_db_candidates),
                })
            continue

        processed_db_count += 1
        peak_db_bytes = max(peak_db_bytes, len(db_bytes))
        if log_fn:
            log_fn(f"[wechat-decrypt] 内存解密成功: {rel}")

        with closing(open_memory_database(db_bytes)) as conn:
            sender_map = load_sender_map(conn)
            hash_to_username = {
                hashlib.md5(username.encode()).hexdigest(): username
                for username in sender_map.values()
                if username
            }

            all_tables = [
                row[0]
                for row in conn.execute(
                    "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Msg_%'"
                )
            ]

            for table_name in all_tables:
                chat_username = hash_to_username.get(table_name[4:], f"unknown_{table_name[4:12]}")
                chat_display_name = display_name(contact_map, chat_username)
                is_group = chat_username.endswith("@chatroom") or chat_username.endswith("@openim")

                rows = conn.execute(
                    f"SELECT local_id, local_type, create_time, real_sender_id, "
                    f"message_content, WCDB_CT_message_content "
                    f"FROM [{table_name}] ORDER BY create_time DESC LIMIT 1000"
                ).fetchall()

                for row in rows:
                    local_type = int(row["local_type"] or 0)
                    create_time = int(row["create_time"] or 0)
                    raw_content = row["message_content"]
                    ct_flag = int(row["WCDB_CT_message_content"] or 0)
                    content = get_content(raw_content, ct_flag)
                    sender_username = sender_map.get(int(row["real_sender_id"] or 0), "")
                    is_sender = not bool(sender_username)
                    sender_name = "我" if is_sender else display_name(
                        contact_map,
                        sender_username if is_group else (sender_username or chat_username),
                    )

                    messages.append(
                        {
                            "wxid": chat_username,
                            "content": friendly_content(local_type, content),
                            "create_time": create_time,
                            "is_sender": is_sender,
                            "nickname": chat_display_name,
                            "sender": sender_name,
                            "msg_type": local_type,
                            "msg_sub_type": 0,
                            "media_type": "image" if local_type == 3 else "",
                            "media_mime": "",
                            "media_name": "",
                            "media_data": "",
                        }
                    )

        del db_bytes
        gc.collect()
        if event_fn:
            event_fn("client_memory_db_released", {
                "db_rel": rel,
                "db_index": index,
                "db_total": len(message_db_candidates),
            })

        if len(messages) > max_messages * 2:
            messages.sort(key=lambda item: item["create_time"])
            messages = messages[-max_messages:]

    messages.sort(key=lambda item: item["create_time"])
    if len(messages) > max_messages:
        messages = messages[-max_messages:]

    return {
        "messages": messages,
        "message_count": len(messages),
        "processed_db_count": processed_db_count,
        "peak_db_bytes": peak_db_bytes,
        "duration_seconds": round(time.time() - started_at, 3),
    }


def collect_db_files(db_dir: str):
    db_files = []
    salt_to_dbs = {}
    for root, _, files in os.walk(db_dir):
        for name in files:
            if not name.endswith(".db") or name.endswith("-wal") or name.endswith("-shm"):
                continue
            path = os.path.join(root, name)
            rel = os.path.relpath(path, db_dir)
            # Keep key discovery aligned with the databases we actually export.
            if not is_export_relevant_db(rel):
                continue
            size = os.path.getsize(path)
            if size < PAGE_SZ:
                continue
            with open(path, "rb") as handle:
                page1 = handle.read(PAGE_SZ)
            salt = page1[:SALT_SZ].hex()
            db_files.append((rel, path, size, salt, page1))
            salt_to_dbs.setdefault(salt, []).append(rel)
    return db_files, salt_to_dbs


def get_wechat_pids(preferred_pid: int = 0):
    result = subprocess.run(
        ["tasklist", "/FO", "CSV", "/NH"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="ignore",
        check=False,
    )
    pids = []
    for line in result.stdout.strip().splitlines():
        if not line.strip():
            continue
        parts = line.strip('"').split('","')
        if len(parts) < 5:
            continue
        name = (parts[0] or "").strip()
        if name.lower() not in {"weixin.exe", "wechat.exe"}:
            continue
        pid = int(parts[1])
        mem_kb = int(parts[4].replace(",", "").replace(" K", "").strip() or "0")
        score = 0 if pid == preferred_pid and preferred_pid > 0 else 1
        pids.append((score, pid, mem_kb, name))

    if not pids:
        raise RuntimeError("未找到正在运行的 Weixin.exe 或 WeChat.exe")

    pids.sort(key=lambda item: (item[0], -item[2]))
    return [(pid, mem_kb, name) for _, pid, mem_kb, name in pids]


def read_mem(handle, addr, size):
    buffer = ctypes.create_string_buffer(size)
    read_size = ctypes.c_size_t(0)
    if kernel32.ReadProcessMemory(handle, ctypes.c_uint64(addr), buffer, size, ctypes.byref(read_size)):
        return buffer.raw[: read_size.value]
    return None


def is_readable_protect(protect: int):
    return (
        protect != PAGE_NOACCESS
        and (protect & PAGE_GUARD) == 0
        and (protect & _READABLE_PROTECT_MASK) != 0
    )


def is_rw_protect(protect: int):
    return (
        protect != PAGE_NOACCESS
        and (protect & PAGE_GUARD) == 0
        and (protect & _IMAGE_RW_PROTECT_MASK) != 0
    )


def enum_regions(handle):
    regions = []
    addr = 0
    mbi = MBI()
    while addr < 0x7FFFFFFFFFFF:
        if kernel32.VirtualQueryEx(handle, ctypes.c_uint64(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)) == 0:
            break
        if mbi.State == MEM_COMMIT and is_readable_protect(int(mbi.Protect)) and 0 < mbi.RegionSize < 500 * 1024 * 1024:
            regions.append((mbi.BaseAddress, mbi.RegionSize))
        nxt = mbi.BaseAddress + mbi.RegionSize
        if nxt <= addr:
            break
        addr = nxt
    return regions


def scan_memory_for_keys(data, db_files, salt_to_dbs, key_map, remaining_salts, base_addr, pid, process_name, log_fn=None):
    matches = 0

    # 方式 A: x'<hex>' 格式（4.1.9 及以下）
    for match in _HEX_RE.finditer(data):
        hex_str = match.group(1).decode()
        addr = base_addr + match.start()
        matches += _process_hex_hit(hex_str, addr, data, match.start(), db_files, salt_to_dbs,
                                     key_map, remaining_salts, pid, process_name, log_fn)

    # 方式 B: 裸 hex 字符串（4.1.10+ 可能不带 x' 包装）
    if remaining_salts:
        for match in _HEX_BARE_RE.finditer(data):
            hex_str = match.group(0).decode()
            addr = base_addr + match.start()
            # 跳过已被 x' 包装捕获的（重叠匹配）
            # 检查原数据中是否被 x' 包裹
            start = match.start()
            if start >= 2 and data[start-2:start] == b"x'":
                continue
            matches += _process_hex_hit(hex_str, addr, data, match.start(), db_files, salt_to_dbs,
                                         key_map, remaining_salts, pid, process_name, log_fn)
            if not remaining_salts:
                break

    return matches


def _process_hex_hit(hex_str, addr, data, offset, db_files, salt_to_dbs,
                     key_map, remaining_salts, pid, process_name, log_fn):
    hex_len = len(hex_str)

    if hex_len == 96:
        enc_key_hex = hex_str[:64]
        salt_hex = hex_str[64:]
        if salt_hex in remaining_salts and verify_hex_key(enc_key_hex, salt_hex, db_files):
            key_map[salt_hex] = enc_key_hex
            remaining_salts.discard(salt_hex)
            if log_fn:
                log_fn(f"[wechat-decrypt] 命中 salt={salt_hex} pid={pid} ({process_name}) addr=0x{addr:016X}")
    elif hex_len == 64:
        enc_key_hex = hex_str
        for rel, _, _, salt_hex, page1 in db_files:
            if salt_hex in remaining_salts and verify_enc_key(bytes.fromhex(enc_key_hex), page1):
                key_map[salt_hex] = enc_key_hex
                remaining_salts.discard(salt_hex)
                if log_fn:
                    log_fn(f"[wechat-decrypt] 命中 salt={salt_hex} pid={pid} ({process_name}) addr=0x{addr:016X}")
                break
    elif hex_len > 96 and hex_len % 2 == 0:
        enc_key_hex = hex_str[:64]
        salt_hex = hex_str[-32:]
        if salt_hex in remaining_salts and verify_hex_key(enc_key_hex, salt_hex, db_files):
            key_map[salt_hex] = enc_key_hex
            remaining_salts.discard(salt_hex)
            if log_fn:
                log_fn(f"[wechat-decrypt] 命中长 hex salt={salt_hex} pid={pid} ({process_name}) addr=0x{addr:016X}")

    return 1


def verify_hex_key(enc_key_hex: str, salt_hex: str, db_files) -> bool:
    enc_key = bytes.fromhex(enc_key_hex)
    for _, _, _, db_salt, page1 in db_files:
        if db_salt == salt_hex and verify_enc_key(enc_key, page1):
            return True
    return False


def verify_enc_key(enc_key: bytes, db_page1: bytes) -> bool:
    salt = db_page1[:SALT_SZ]
    mac_salt = bytes(value ^ 0x3A for value in salt)
    mac_key = hashlib.pbkdf2_hmac("sha512", enc_key, mac_salt, 2, dklen=KEY_SZ)
    hmac_data = db_page1[SALT_SZ : PAGE_SZ - RESERVE_SZ + IV_SZ]
    stored_hmac = db_page1[PAGE_SZ - HMAC_SZ : PAGE_SZ]
    hm = hmac_mod.new(mac_key, hmac_data, hashlib.sha512)
    hm.update(struct.pack("<I", 1))
    return hm.digest() == stored_hmac


def cross_verify_keys(db_files, salt_to_dbs, key_map):
    missing_salts = set(salt_to_dbs.keys()) - set(key_map.keys())
    if not missing_salts or not key_map:
        return
    for salt_hex in list(missing_salts):
        for _, _, _, db_salt, page1 in db_files:
            if db_salt != salt_hex:
                continue
            for known_key_hex in key_map.values():
                enc_key = bytes.fromhex(known_key_hex)
                if verify_enc_key(enc_key, page1):
                    key_map[salt_hex] = known_key_hex
                    missing_salts.discard(salt_hex)
                    break
            break


def derive_mac_key(enc_key: bytes, salt: bytes):
    mac_salt = bytes(value ^ 0x3A for value in salt)
    return hashlib.pbkdf2_hmac("sha512", enc_key, mac_salt, 2, dklen=KEY_SZ)


def decrypt_page(enc_key: bytes, page_data: bytes, page_number: int):
    iv = page_data[PAGE_SZ - RESERVE_SZ : PAGE_SZ - RESERVE_SZ + IV_SZ]
    if page_number == 1:
        encrypted = page_data[SALT_SZ : PAGE_SZ - RESERVE_SZ]
        cipher = AES.new(enc_key, AES.MODE_CBC, iv)
        decrypted = cipher.decrypt(encrypted)
        page = bytearray(SQLITE_HDR + decrypted + b"\x00" * RESERVE_SZ)
        return bytes(page)
    encrypted = page_data[: PAGE_SZ - RESERVE_SZ]
    cipher = AES.new(enc_key, AES.MODE_CBC, iv)
    decrypted = cipher.decrypt(encrypted)
    return decrypted + b"\x00" * RESERVE_SZ


def decrypt_database(db_path: str, out_path: str, enc_key: bytes):
    file_size = os.path.getsize(db_path)
    total_pages = file_size // PAGE_SZ
    if file_size % PAGE_SZ != 0:
        total_pages += 1

    with open(db_path, "rb") as fin:
        page1 = fin.read(PAGE_SZ)
    if len(page1) < PAGE_SZ:
        return False

    salt = page1[:SALT_SZ]
    mac_key = derive_mac_key(enc_key, salt)
    hmac_data = page1[SALT_SZ : PAGE_SZ - RESERVE_SZ + IV_SZ]
    stored_hmac = page1[PAGE_SZ - HMAC_SZ : PAGE_SZ]
    hm = hmac_mod.new(mac_key, hmac_data, hashlib.sha512)
    hm.update(struct.pack("<I", 1))
    if hm.digest() != stored_hmac:
        return False

    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(db_path, "rb") as fin, open(out_path, "wb") as fout:
        for page_number in range(1, total_pages + 1):
            page = fin.read(PAGE_SZ)
            if len(page) < PAGE_SZ:
                if len(page) == 0:
                    break
                page = page + b"\x00" * (PAGE_SZ - len(page))
            fout.write(decrypt_page(enc_key, page, page_number))
    return True


def apply_encrypted_wal_to_decrypted_db(db_path: str, out_path: str, enc_key: bytes, log_fn=None):
    wal_path = db_path + "-wal"
    if not os.path.exists(wal_path) or not os.path.exists(out_path):
        return {"applied": 0, "wal_path": wal_path, "present": False, "reason": "wal_missing"}

    try:
        with open(wal_path, "rb") as handle:
            wal_header = handle.read(32)
            if len(wal_header) < 32:
                return {"applied": 0, "wal_path": wal_path, "present": True, "reason": "wal_header_too_short"}

            magic = struct.unpack(">I", wal_header[:4])[0]
            if magic not in (0x377F0682, 0x377F0683):
                return {"applied": 0, "wal_path": wal_path, "present": True, "reason": "wal_magic_mismatch"}

            wal_page_size = struct.unpack(">I", wal_header[8:12])[0]
            if wal_page_size == 1:
                wal_page_size = 65536
            if wal_page_size != PAGE_SZ:
                return {
                    "applied": 0,
                    "wal_path": wal_path,
                    "present": True,
                    "reason": f"wal_page_size_unsupported:{wal_page_size}",
                }

            frame_size = 24 + wal_page_size
            total_frames = 0
            committed_frames = 0
            committed_end_offset = 32
            committed_db_pages = 0
            while True:
                frame_header = handle.read(24)
                if not frame_header or len(frame_header) < 24:
                    break
                handle.seek(wal_page_size, os.SEEK_CUR)
                if handle.tell() > os.path.getsize(wal_path):
                    break

                total_frames += 1
                db_size_after_commit = struct.unpack(">I", frame_header[4:8])[0]
                if db_size_after_commit > 0:
                    committed_frames = total_frames
                    committed_end_offset = 32 + total_frames * frame_size
                    committed_db_pages = db_size_after_commit

            if committed_frames == 0:
                return {
                    "applied": 0,
                    "wal_path": wal_path,
                    "present": True,
                    "reason": "wal_no_committed_frames",
                    "frame_count": total_frames,
                    "committed_frames": 0,
                }

        applied = 0
        with open(wal_path, "rb") as handle:
            handle.seek(32)
            with open(out_path, "r+b") as fout:
                for _ in range(committed_frames):
                    frame_header = handle.read(24)
                    if len(frame_header) < 24:
                        raise RuntimeError("wal_frame_header_truncated")

                    page_data = handle.read(wal_page_size)
                    if len(page_data) < wal_page_size:
                        raise RuntimeError("wal_frame_data_truncated")

                    page_number = struct.unpack(">I", frame_header[:4])[0]
                    if page_number <= 0:
                        continue

                    decrypted_page = decrypt_page(enc_key, page_data, page_number)
                    fout.seek((page_number - 1) * PAGE_SZ)
                    fout.write(decrypted_page)
                    applied += 1
                if committed_db_pages > 0:
                    fout.truncate(committed_db_pages * PAGE_SZ)

            if log_fn and applied > 0:
                log_fn(
                    f"[wechat-decrypt] 已合并 WAL 页面: {os.path.basename(wal_path)} -> {applied} 页 "
                    f"(已提交帧 {committed_frames}/{total_frames})"
                )
            return {
                "applied": applied,
                "wal_path": wal_path,
                "present": True,
                "reason": "wal_applied",
                "frame_count": total_frames,
                "committed_frames": committed_frames,
                "uncommitted_frames": max(total_frames - committed_frames, 0),
                "commit_end_offset": committed_end_offset,
            }
    except Exception as exc:
        if log_fn:
            log_fn(f"[wechat-decrypt] 合并 WAL 失败: {wal_path} ({exc})")
        return {
            "applied": 0,
            "wal_path": wal_path,
            "present": True,
            "reason": "wal_apply_failed",
            "error_message": str(exc),
        }


def iter_image_sample_files(source_data_dir: str):
    if not source_data_dir or not os.path.isdir(source_data_dir):
        return []

    patterns = [
        os.path.join(source_data_dir, "msg", "attach", "**", "Img", "*_t.dat"),
        os.path.join(source_data_dir, "MsgAttach", "**", "Image", "*_t.dat"),
        os.path.join(source_data_dir, "FileStorage", "MsgAttach", "**", "Image", "*_t.dat"),
    ]

    files = []
    seen = set()
    for pattern in patterns:
        for path in glob.glob(pattern, recursive=True):
            if path in seen or not os.path.isfile(path):
                continue
            seen.add(path)
            files.append(path)

    files.sort(key=lambda path: os.path.getmtime(path), reverse=True)
    return files


def detect_v2_image_samples(source_data_dir: str):
    samples = []
    for path in iter_image_sample_files(source_data_dir):
        try:
            with open(path, "rb") as handle:
                header = handle.read(31)
            if len(header) >= 31 and header[:6] in (V2_MAGIC_FULL, V1_MAGIC_FULL):
                samples.append((path, header))
        except OSError:
            continue
    return samples


def extract_image_sample_ciphertext(header: bytes):
    if len(header) < 31 or header[:6] != V2_MAGIC_FULL:
        return b""
    return header[15:31]


def collect_image_v2_patterns(v2_samples: list[tuple[str, bytes]], limit: int = 32):
    patterns = {}
    for index, (path, header) in enumerate(v2_samples[:256]):
        ciphertext = extract_image_sample_ciphertext(header)
        if not ciphertext:
            continue

        ct_hex = ciphertext.hex()
        pattern = patterns.get(ct_hex)
        if pattern is None:
            pattern = {
                "ct_hex": ct_hex,
                "ciphertext": ciphertext,
                "sample_path": path,
                "count": 0,
                "first_index": index,
            }
            patterns[ct_hex] = pattern
        pattern["count"] += 1

    ranked = sorted(
        patterns.values(),
        key=lambda item: (-int(item["count"]), int(item["first_index"])),
    )
    return ranked[:limit]


def get_image_crypto_config_path():
    configured = (
        os.environ.get("WECHAT_IMAGE_CRYPTO_FILE")
        or os.environ.get("WEFLOW_IMAGE_CRYPTO_FILE")
        or ""
    ).strip()
    if configured:
        return configured

    runtime_dir = (os.environ.get("WECHAT_RUNTIME_DIR") or "").strip()
    if runtime_dir:
        return os.path.join(runtime_dir, IMAGE_KEY_CONFIG_FILE_NAME)

    return os.path.join(os.path.dirname(os.path.abspath(__file__)), IMAGE_KEY_CONFIG_FILE_NAME)


def load_saved_image_crypto_config():
    config_path = get_image_crypto_config_path()
    if not config_path or not os.path.exists(config_path):
        return {}

    try:
        with open(config_path, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
        if isinstance(payload, dict):
            payload["_config_path"] = config_path
            return payload
    except Exception:
        return {}
    return {}


def normalize_image_aes_key(aes_key):
    text = str(aes_key or "").strip()
    if not text:
        return ""
    normalized = text.encode("ascii", errors="ignore")[:16].decode("ascii", errors="ignore")
    return normalized if len(normalized) == 16 else ""


def normalize_image_key_map(raw_map):
    normalized = {}
    if not isinstance(raw_map, dict):
        return normalized

    for ct_hex, aes_key in raw_map.items():
        ct = str(ct_hex or "").strip().lower()
        if len(ct) != 32 or not all(char in "0123456789abcdef" for char in ct):
            continue
        key = normalize_image_aes_key(aes_key)
        if key:
            normalized[ct] = key
    return normalized


def merge_image_key_maps(*maps):
    merged = {}
    for raw_map in maps:
        for ct_hex, aes_key in normalize_image_key_map(raw_map).items():
            merged[ct_hex] = aes_key
    return merged


def solve_image_patterns_with_key(patterns, aes_key: str):
    normalized_key = normalize_image_aes_key(aes_key)
    if not normalized_key:
        return {}

    solved = {}
    key_bytes = normalized_key.encode("ascii", errors="ignore")
    for pattern in patterns:
        fmt = try_image_aes_key(key_bytes, pattern["ciphertext"])
        if fmt:
            solved[pattern["ct_hex"]] = normalized_key
    return solved


def pick_primary_image_aes_key(patterns, image_key_map, fallback_key: str = ""):
    normalized_map = normalize_image_key_map(image_key_map)
    for pattern in patterns:
        matched = normalized_map.get(pattern["ct_hex"])
        if matched:
            return matched

    normalized_fallback = normalize_image_aes_key(fallback_key)
    if normalized_fallback:
        return normalized_fallback
    return next(iter(normalized_map.values()), "")


def build_image_key_coverage_fields(patterns, image_key_map):
    total_pattern_count = len(patterns or [])
    solved_pattern_count = len(normalize_image_key_map(image_key_map))
    pending_pattern_count = max(total_pattern_count - solved_pattern_count, 0)
    coverage_percent = 100.0 if total_pattern_count <= 0 else round(solved_pattern_count * 100.0 / total_pattern_count, 1)
    return {
        "solved_pattern_count": solved_pattern_count,
        "pending_pattern_count": pending_pattern_count,
        "key_coverage_complete": pending_pattern_count == 0,
        "key_coverage_partial": total_pattern_count > 0 and pending_pattern_count > 0,
        "key_coverage_percent": coverage_percent,
    }


def save_image_crypto_config(
    aes_key: str,
    xor_key: int,
    image_key_map=None,
    *,
    source: str = "",
    verified_sample: str = "",
    log_fn=None,
):
    normalized_map = normalize_image_key_map(image_key_map or {})
    primary_key = pick_primary_image_aes_key([], normalized_map, fallback_key=aes_key)
    if not primary_key and not normalized_map:
        return ""

    config_path = get_image_crypto_config_path()
    if not config_path:
        return ""

    payload = {
        "image_aes_key": primary_key,
        "image_xor_key": int(xor_key),
        "image_keys": normalized_map,
        "image_key_count": len(normalized_map),
        "updated_at": int(time.time()),
        "source": source,
    }
    if normalized_map:
        payload["primary_ct_hex"] = next(iter(normalized_map.keys()))
    if verified_sample:
        payload["verified_sample"] = verified_sample

    try:
        config_dir = os.path.dirname(config_path)
        if config_dir:
            os.makedirs(config_dir, exist_ok=True)
        with open(config_path, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, ensure_ascii=False, indent=2)
        if log_fn:
            log_fn(f"[wechat-decrypt] 已写入图片密钥缓存: {config_path}")
        return config_path
    except Exception as exc:
        if log_fn:
            log_fn(f"[wechat-decrypt] 写入图片密钥缓存失败: {exc}")
        return ""


def verify_image_aes_key(aes_key: str, ciphertext: bytes):
    normalized_key = normalize_image_aes_key(aes_key)
    if not normalized_key or not ciphertext:
        return ""
    return try_image_aes_key(normalized_key.encode("ascii", errors="ignore"), ciphertext)


def resolve_image_aes_key_for_ciphertext(ciphertext: bytes, image_aes_key: str = "", image_key_map=None):
    normalized_map = normalize_image_key_map(image_key_map or {})
    ct_hex = ciphertext.hex().lower() if ciphertext else ""
    if ct_hex and ct_hex in normalized_map:
        return normalized_map[ct_hex], ct_hex
    return normalize_image_aes_key(image_aes_key), ct_hex


def verify_image_crypto_with_samples(
    v2_samples: list[tuple[str, bytes]],
    patterns,
    image_aes_key: str,
    image_key_map,
    xor_key: int,
):
    normalized_map = merge_image_key_maps(image_key_map, solve_image_patterns_with_key(patterns, image_aes_key))
    verified_sample = ""
    verified_mime = ""
    verified_size = 0
    verified_count = 0
    solved_patterns = set()

    for path, header in v2_samples[:16]:
        ciphertext = extract_image_sample_ciphertext(header)
        if ciphertext:
            aes_key, ct_hex = resolve_image_aes_key_for_ciphertext(
                ciphertext,
                image_aes_key=image_aes_key,
                image_key_map=normalized_map,
            )
            if not aes_key or not verify_image_aes_key(aes_key, ciphertext):
                continue
            if ct_hex:
                solved_patterns.add(ct_hex)

        media = decode_image_dat_file(
            path,
            image_aes_key=image_aes_key,
            image_xor_key=xor_key,
            image_key_map=normalized_map,
        )
        if not media:
            continue

        verified_count += 1
        if not verified_sample:
            verified_sample = path
            verified_mime = media.get("mime", "")
            verified_size = int(media.get("size", 0) or 0)

    return {
        "success": verified_count > 0,
        "verified_sample": verified_sample,
        "verified_mime": verified_mime,
        "verified_size": verified_size,
        "verified_count": verified_count,
        "solved_pattern_count": len(solved_patterns),
        "total_pattern_count": len(patterns),
        "image_key_map": normalized_map,
    }


def derive_image_xor_key(v2_samples: list[tuple[str, bytes]]):
    tail_counts = {}
    for path, _ in v2_samples[:32]:
        try:
            with open(path, "rb") as handle:
                handle.seek(-2, os.SEEK_END)
                tail = handle.read(2)
            if len(tail) != 2:
                continue
            tail_counts[tail] = tail_counts.get(tail, 0) + 1
        except OSError:
            continue

    if not tail_counts:
        return 0x88

    x, y = max(tail_counts, key=tail_counts.get)
    guessed = x ^ 0xFF
    if (y ^ 0xD9) == guessed:
        return guessed
    return guessed


def try_image_aes_key(key_bytes: bytes, ciphertext: bytes):
    try:
        cipher = AES.new(key_bytes, AES.MODE_ECB)
        decrypted = cipher.decrypt(ciphertext)
    except Exception:
        return ""

    fmt = detect_image_format(decrypted[:16])
    return fmt if fmt != "bin" else ""


def iter_image_key_candidates(data: bytes):
    seen = set()
    for match in _IMAGE_KEY32_RE.finditer(data):
        candidate = match.group()[:16]
        if len(candidate) != 16 or candidate in seen:
            continue
        seen.add(candidate)
        yield candidate

    for match in _IMAGE_KEY16_RE.finditer(data):
        candidate = match.group()
        if len(candidate) != 16 or candidate in seen:
            continue
        seen.add(candidate)
        yield candidate


def scan_data_for_image_aes_keys(data: bytes, pending_patterns):
    found = {}
    remaining = list(pending_patterns.values())
    if not remaining:
        return found

    for candidate in iter_image_key_candidates(data):
        key_text = normalize_image_aes_key(candidate.decode("ascii", errors="ignore"))
        if not key_text:
            continue

        still_pending = []
        for pattern in remaining:
            fmt = try_image_aes_key(candidate, pattern["ciphertext"])
            if fmt:
                found[pattern["ct_hex"]] = {
                    "aes_key": key_text,
                    "fmt": fmt,
                }
            else:
                still_pending.append(pattern)

        remaining = still_pending
        if not remaining:
            break

    return found


def scan_memory_for_image_aes_keys(patterns, preferred_pid: int = 0, log_fn=None):
    pending_patterns = {pattern["ct_hex"]: pattern for pattern in patterns}
    found_keys = {}
    if not pending_patterns:
        return found_keys

    pids = get_wechat_pids(preferred_pid)

    for pid, mem_kb, process_name in pids:
        handle = kernel32.OpenProcess(0x0010 | 0x0400, False, pid)
        if not handle:
            continue

        try:
            regions = enum_regions(handle)
            total_bytes = sum(size for _, size in regions)
            scanned_bytes = 0

            for reg_idx, (base, size) in enumerate(regions):
                data = read_mem(handle, base, size)
                scanned_bytes += size
                if not data:
                    continue

                matched = scan_data_for_image_aes_keys(data, pending_patterns)
                if matched:
                    for ct_hex, info in matched.items():
                        found_keys[ct_hex] = info["aes_key"]
                        pending_patterns.pop(ct_hex, None)
                        if log_fn:
                            log_fn(
                                f"[wechat-decrypt] 命中图片 AES key pid={pid} ({process_name}) "
                                f"fmt={info['fmt']} len=16 ct={ct_hex[:8]}..."
                            )
                    if not pending_patterns:
                        return found_keys

                if log_fn and (reg_idx + 1) % 200 == 0 and total_bytes > 0:
                    progress = scanned_bytes / total_bytes * 100
                    log_fn(
                        f"[wechat-decrypt] 扫描图片 key PID={pid} 进度 {progress:.1f}% "
                        f"(已命中 {len(found_keys)}/{len(found_keys) + len(pending_patterns)})"
                    )
        finally:
            kernel32.CloseHandle(handle)

    return found_keys


def scan_memory_for_image_aes_key(ciphertext: bytes, preferred_pid: int = 0, log_fn=None):
    if not ciphertext:
        return ""
    patterns = [{"ct_hex": ciphertext.hex(), "ciphertext": ciphertext, "sample_path": "", "count": 1}]
    found = scan_memory_for_image_aes_keys(patterns, preferred_pid=preferred_pid, log_fn=log_fn)
    return found.get(ciphertext.hex(), "")


def enum_rw_regions(handle):
    regions = []
    addr = 0
    mbi = MBI()
    while addr < 0x7FFFFFFFFFFF:
        if kernel32.VirtualQueryEx(handle, ctypes.c_uint64(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)) == 0:
            break
        if (
            mbi.State == MEM_COMMIT
            and is_rw_protect(int(mbi.Protect))
            and 0 < mbi.RegionSize < 50 * 1024 * 1024
        ):
            regions.append((mbi.BaseAddress, mbi.RegionSize))
        nxt = mbi.BaseAddress + mbi.RegionSize
        if nxt <= addr:
            break
        addr = nxt
    return regions


def enum_readable_non_rw_regions(handle, rw_regions=None):
    readable_regions = enum_regions(handle)
    rw_set = {(base, size) for base, size in (rw_regions or [])}
    return [
        (base, size)
        for base, size in readable_regions
        if (base, size) not in rw_set
    ]


def scan_regions_for_image_aes_keys(handle, regions, pending_patterns, deadline: float = 0):
    found_keys = {}
    for base, size in regions:
        if deadline and time.time() >= deadline:
            break
        data = read_mem(handle, base, size)
        if not data or len(data) < 16:
            continue

        matched = scan_data_for_image_aes_keys(data, pending_patterns)
        if matched:
            for ct_hex, info in matched.items():
                found_keys[ct_hex] = info
                pending_patterns.pop(ct_hex, None)
            if not pending_patterns:
                break

    return found_keys


def scan_regions_for_image_aes_key(handle, regions, ciphertext: bytes):
    if not ciphertext:
        return "", ""
    pending_patterns = {
        ciphertext.hex(): {
            "ct_hex": ciphertext.hex(),
            "ciphertext": ciphertext,
            "sample_path": "",
            "count": 1,
        }
    }
    matched = scan_regions_for_image_aes_keys(handle, regions, pending_patterns)
    info = matched.get(ciphertext.hex())
    if not info:
        return "", ""
    return info["aes_key"], info["fmt"]


def quick_scan_memory_for_image_aes_keys(patterns, preferred_pid: int = 0, deadline: float = 0, log_fn=None):
    pending_patterns = {pattern["ct_hex"]: pattern for pattern in patterns}
    found_keys = {}
    if not pending_patterns:
        return found_keys

    try:
        pids = get_wechat_pids(preferred_pid)
    except Exception:
        return found_keys

    for pid, mem_kb, process_name in pids:
        if deadline and time.time() >= deadline:
            break
        handle = kernel32.OpenProcess(0x0010 | 0x0400, False, pid)
        if not handle:
            continue

        try:
            rw_regions = enum_rw_regions(handle)
            matched = scan_regions_for_image_aes_keys(
                handle,
                rw_regions,
                pending_patterns,
                deadline=deadline,
            )
            if matched:
                for ct_hex, info in matched.items():
                    found_keys[ct_hex] = info["aes_key"]
                    if log_fn:
                        log_fn(
                            f"[wechat-decrypt] 监控命中图片 AES key pid={pid} ({process_name}) "
                            f"fmt={info['fmt']} rw_regions={len(rw_regions)} phase=rw ct={ct_hex[:8]}..."
                        )
                if not pending_patterns:
                    return found_keys

            if deadline and time.time() >= deadline:
                break
            readable_non_rw_regions = enum_readable_non_rw_regions(handle, rw_regions=rw_regions)
            matched = scan_regions_for_image_aes_keys(
                handle,
                readable_non_rw_regions,
                pending_patterns,
                deadline=deadline,
            )
            if matched:
                for ct_hex, info in matched.items():
                    found_keys[ct_hex] = info["aes_key"]
                    if log_fn:
                        log_fn(
                            f"[wechat-decrypt] 监控命中图片 AES key pid={pid} ({process_name}) "
                            f"fmt={info['fmt']} rw_regions={len(rw_regions)} readable_non_rw_regions={len(readable_non_rw_regions)} "
                            f"phase=readable ct={ct_hex[:8]}..."
                        )
                if not pending_patterns:
                    return found_keys
        finally:
            kernel32.CloseHandle(handle)

    return found_keys


def quick_scan_memory_for_image_aes_key(
    ciphertext: bytes,
    preferred_pid: int = 0,
    deadline: float = 0,
    log_fn=None,
):
    if not ciphertext:
        return ""
    patterns = [{"ct_hex": ciphertext.hex(), "ciphertext": ciphertext, "sample_path": "", "count": 1}]
    found = quick_scan_memory_for_image_aes_keys(
        patterns,
        preferred_pid=preferred_pid,
        deadline=deadline,
        log_fn=log_fn,
    )
    return found.get(ciphertext.hex(), "")


def monitor_image_aes_keys(
    patterns,
    preferred_pid: int = 0,
    timeout_seconds: int = IMAGE_KEY_MONITOR_TIMEOUT_SECONDS,
    scan_interval_seconds: float = IMAGE_KEY_MONITOR_SCAN_INTERVAL_SECONDS,
    log_fn=None,
):
    if not patterns or timeout_seconds <= 0:
        return {}, 0, 0.0

    started_at = time.time()
    attempt_count = 0
    found_keys = {}
    pending_patterns = {pattern["ct_hex"]: pattern for pattern in patterns}
    while (time.time() - started_at) < timeout_seconds:
        attempt_count += 1
        matched = quick_scan_memory_for_image_aes_keys(
            list(pending_patterns.values()),
            preferred_pid=preferred_pid,
            log_fn=log_fn,
        )
        if matched:
            found_keys.update(matched)
            for ct_hex in matched:
                pending_patterns.pop(ct_hex, None)
            if not pending_patterns:
                return found_keys, attempt_count, round(time.time() - started_at, 3)
        time.sleep(scan_interval_seconds)

    return found_keys, attempt_count, round(time.time() - started_at, 3)


def monitor_image_aes_key(
    ciphertext: bytes,
    preferred_pid: int = 0,
    timeout_seconds: int = IMAGE_KEY_MONITOR_TIMEOUT_SECONDS,
    scan_interval_seconds: float = IMAGE_KEY_MONITOR_SCAN_INTERVAL_SECONDS,
    log_fn=None,
):
    if not ciphertext:
        return "", 0, 0.0
    patterns = [{"ct_hex": ciphertext.hex(), "ciphertext": ciphertext, "sample_path": "", "count": 1}]
    found, attempt_count, elapsed = monitor_image_aes_keys(
        patterns,
        preferred_pid=preferred_pid,
        timeout_seconds=timeout_seconds,
        scan_interval_seconds=scan_interval_seconds,
        log_fn=log_fn,
    )
    return found.get(ciphertext.hex(), ""), attempt_count, elapsed


def discover_image_crypto(source_data_dir: str, preferred_pid: int = 0, log_fn=None):
    v2_samples = detect_v2_image_samples(source_data_dir)
    patterns = collect_image_v2_patterns(v2_samples)
    if not patterns:
        return "", 0x88, {}

    xor_key = derive_image_xor_key(v2_samples)
    image_key_map = scan_memory_for_image_aes_keys(patterns, preferred_pid=preferred_pid, log_fn=log_fn)
    aes_key = pick_primary_image_aes_key(patterns, image_key_map)

    if log_fn:
        if image_key_map:
            log_fn(
                f"[wechat-decrypt] 图片密钥已就绪，xor=0x{xor_key:02x} patterns={len(image_key_map)}"
            )
        else:
            log_fn("[wechat-decrypt] 未找到图片 AES key，V2 图片将无法解密")

    return aes_key, xor_key, image_key_map


def load_image_crypto_config(source_data_dir: str = "", preferred_pid: int = 0, log_fn=None, event_fn=None):
    env_aes_key = normalize_image_aes_key(os.environ.get("WECHAT_IMAGE_AES_KEY") or "")
    xor_raw = (os.environ.get("WECHAT_IMAGE_XOR_KEY") or "0x88").strip()
    try:
        xor_key = int(xor_raw, 0)
    except ValueError:
        xor_key = 0x88

    v2_samples = detect_v2_image_samples(source_data_dir) if source_data_dir else []
    patterns = collect_image_v2_patterns(v2_samples)
    sample_path = patterns[0]["sample_path"] if patterns else (v2_samples[0][0] if v2_samples else "")
    if v2_samples:
        xor_key = derive_image_xor_key(v2_samples)

    cache_checked = False
    cache_valid = False
    cache_path = ""
    selected_source = ""
    image_key_map = {}
    primary_key = ""

    if env_aes_key:
        env_verify = verify_image_crypto_with_samples(v2_samples, patterns, env_aes_key, {}, xor_key)
        image_key_map = env_verify["image_key_map"]
        primary_key = pick_primary_image_aes_key(patterns, image_key_map, fallback_key=env_aes_key)
        if env_verify["success"] or not patterns:
            emit_media_event(
                event_fn,
                "client_image_key_result",
                {
                    "success": True,
                    "source": "environment",
                    "verified": env_verify["success"] or not patterns,
                    "v2_sample_count": len(v2_samples),
                    "v2_pattern_count": len(patterns),
                    "sample_file": sample_path,
                    "verified_sample": env_verify["verified_sample"],
                    "verified_size": env_verify["verified_size"],
                    "verified_count": env_verify["verified_count"],
                    "aes_key_length": len(primary_key),
                    "image_xor_key": xor_key,
                    "image_key_count": len(image_key_map),
                    **build_image_key_coverage_fields(patterns, image_key_map),
                },
            )
            if not patterns or len(image_key_map) >= len(patterns):
                return primary_key, xor_key, image_key_map
            if log_fn:
                log_fn(
                    f"[wechat-decrypt] 环境变量图片 key 只覆盖了 {len(image_key_map)}/{len(patterns)} 个 pattern，继续补扫"
                )

        if log_fn:
            log_fn("[wechat-decrypt] 环境变量里的图片 AES key 校验失败，准备重新扫描")

    saved = load_saved_image_crypto_config()
    saved_aes_key = normalize_image_aes_key(saved.get("image_aes_key") or "")
    saved_key_map = normalize_image_key_map(saved.get("image_keys") or {})
    if saved_aes_key or saved_key_map:
        cache_checked = True
        cache_path = str(saved.get("_config_path") or "")
        saved_xor = saved.get("image_xor_key")
        if saved_xor not in (None, ""):
            try:
                xor_key = int(saved_xor)
            except Exception:
                pass

        cache_verify = verify_image_crypto_with_samples(
            v2_samples,
            patterns,
            saved_aes_key,
            saved_key_map,
            xor_key,
        )
        cache_valid = cache_verify["success"] or not patterns
        image_key_map = cache_verify["image_key_map"]
        primary_key = pick_primary_image_aes_key(patterns, image_key_map, fallback_key=saved_aes_key)
        if cache_valid:
            emit_media_event(
                event_fn,
                "client_image_key_result",
                {
                    "success": True,
                    "source": "local_cache",
                    "verified": True,
                    "cache_checked": True,
                    "cache_valid": True,
                    "cache_path": cache_path,
                    "v2_sample_count": len(v2_samples),
                    "v2_pattern_count": len(patterns),
                    "sample_file": sample_path,
                    "verified_sample": cache_verify["verified_sample"],
                    "verified_size": cache_verify["verified_size"],
                    "verified_count": cache_verify["verified_count"],
                    "aes_key_length": len(primary_key),
                    "image_xor_key": xor_key,
                    "image_key_count": len(image_key_map),
                    **build_image_key_coverage_fields(patterns, image_key_map),
                },
            )
            if not patterns or len(image_key_map) >= len(patterns):
                return primary_key, xor_key, image_key_map
            if log_fn:
                log_fn(
                    f"[wechat-decrypt] 图片缓存只覆盖了 {len(image_key_map)}/{len(patterns)} 个 pattern，继续补扫"
                )

        if log_fn and not cache_valid:
            log_fn("[wechat-decrypt] 本地缓存的图片 AES key 校验失败，准备重新扫描")

    if source_data_dir and patterns:
        pending_patterns = [pattern for pattern in patterns if pattern["ct_hex"] not in image_key_map]
        try:
            discovered_key_map = scan_memory_for_image_aes_keys(
                pending_patterns,
                preferred_pid=preferred_pid,
                log_fn=log_fn,
            )
        except Exception as exc:
            discovered_key_map = {}
            if log_fn:
                log_fn(f"[wechat-decrypt] 一次性扫描图片 AES key 失败: {exc}")
        selected_source = "memory_scan"
        monitor_attempts = 0
        monitor_elapsed = 0.0
        image_key_map = merge_image_key_maps(image_key_map, discovered_key_map)

        if len(image_key_map) < len(patterns):
            emit_media_event(
                event_fn,
                "client_image_key_monitor_started",
                {
                    "timeout_seconds": IMAGE_KEY_MONITOR_TIMEOUT_SECONDS,
                    "scan_interval_seconds": IMAGE_KEY_MONITOR_SCAN_INTERVAL_SECONDS,
                    "v2_sample_count": len(v2_samples),
                    "v2_pattern_count": len(patterns),
                    "pending_pattern_count": max(len(patterns) - len(image_key_map), 0),
                    "sample_file": sample_path,
                },
            )
            monitored_key_map, monitor_attempts, monitor_elapsed = monitor_image_aes_keys(
                [pattern for pattern in patterns if pattern["ct_hex"] not in image_key_map],
                preferred_pid=preferred_pid,
                timeout_seconds=IMAGE_KEY_MONITOR_TIMEOUT_SECONDS,
                scan_interval_seconds=IMAGE_KEY_MONITOR_SCAN_INTERVAL_SECONDS,
                log_fn=log_fn,
            )
            image_key_map = merge_image_key_maps(image_key_map, monitored_key_map)
            if monitored_key_map:
                selected_source = "monitor_scan"

        primary_key = pick_primary_image_aes_key(patterns, image_key_map, fallback_key=saved_aes_key or env_aes_key)
        if primary_key or image_key_map:
            verify_result = verify_image_crypto_with_samples(
                v2_samples,
                patterns,
                primary_key,
                image_key_map,
                xor_key,
            )
            saved_path = ""
            if verify_result["success"]:
                saved_path = save_image_crypto_config(
                    primary_key,
                    xor_key,
                    image_key_map,
                    source=selected_source,
                    verified_sample=verify_result["verified_sample"],
                    log_fn=log_fn,
                )

            emit_media_event(
                event_fn,
                "client_image_key_result",
                {
                    "success": True,
                    "source": selected_source,
                    "verified": verify_result["success"],
                    "cache_checked": cache_checked,
                    "cache_valid": cache_valid,
                    "cache_path": cache_path,
                    "saved_path": saved_path,
                    "v2_sample_count": len(v2_samples),
                    "v2_pattern_count": len(patterns),
                    "sample_file": sample_path,
                    "verified_sample": verify_result["verified_sample"],
                    "verified_size": verify_result["verified_size"],
                    "verified_count": verify_result["verified_count"],
                    "aes_key_length": len(primary_key),
                    "image_xor_key": xor_key,
                    "image_key_count": len(image_key_map),
                    **build_image_key_coverage_fields(patterns, image_key_map),
                    "monitor_attempt_count": monitor_attempts,
                    "monitor_elapsed_seconds": monitor_elapsed,
                },
            )
            return primary_key, xor_key, image_key_map

        emit_media_event(
            event_fn,
            "client_image_key_result",
            {
                "success": False,
                "source": "monitor_scan",
                "reason": "image_aes_key_not_found",
                "cache_checked": cache_checked,
                "cache_valid": cache_valid,
                "cache_path": cache_path,
                "v2_sample_count": len(v2_samples),
                "v2_pattern_count": len(patterns),
                "sample_file": sample_path,
                "image_xor_key": xor_key,
                "image_key_count": len(image_key_map),
                **build_image_key_coverage_fields(patterns, image_key_map),
                "monitor_attempt_count": monitor_attempts,
                "monitor_elapsed_seconds": monitor_elapsed,
            },
        )
        return "", xor_key, image_key_map

    if source_data_dir:
        emit_media_event(
            event_fn,
            "client_image_key_result",
            {
                "success": False,
                "source": "image_scan",
                "reason": "no_v2_image_samples",
                "cache_checked": cache_checked,
                "cache_valid": cache_valid,
                "cache_path": cache_path,
                "v2_sample_count": len(v2_samples),
                "v2_pattern_count": len(patterns),
                **build_image_key_coverage_fields(patterns, image_key_map),
            },
        )

    return "", xor_key, {}


def extract_md5_from_packed_info(blob):
    if not blob or not isinstance(blob, bytes):
        return ""

    marker = b"\x12\x22\x0a\x20"
    idx = blob.find(marker)
    if idx >= 0 and idx + len(marker) + 32 <= len(blob):
        candidate = blob[idx + len(marker) : idx + len(marker) + 32]
        try:
            md5_value = candidate.decode("ascii")
            int(md5_value, 16)
            return md5_value
        except (UnicodeDecodeError, ValueError):
            pass

    hex_chars = set(b"0123456789abcdef")
    i = 0
    while i <= len(blob) - 32:
        if blob[i] in hex_chars:
            candidate = blob[i : i + 32]
            if all(ch in hex_chars for ch in candidate):
                try:
                    return candidate.decode("ascii")
                except UnicodeDecodeError:
                    pass
            i += 32
        else:
            i += 1
    return ""


def load_resource_md5_map(
    decrypted_dir: str,
    min_create_time: int = 0,
    deadline: float = 0,
    max_rows: int = 12000,
    log_fn=None,
):
    resource_path = os.path.join(decrypted_dir, "message", "message_resource.db")
    exact_map = {}
    fallback_map = {}
    if not os.path.exists(resource_path):
        return {"exact": exact_map, "fallback": fallback_map}

    try:
        with closing(sqlite3.connect(resource_path)) as conn:
            chat_id_map = {
                row[0]: row[1]
                for row in conn.execute("SELECT rowid, user_name FROM ChatName2Id")
            }
            clauses = []
            params = []
            if min_create_time > 0:
                clauses.append("message_create_time >= ?")
                params.append(min_create_time)
            where_clause = f" WHERE {' AND '.join(clauses)}" if clauses else ""
            rows = conn.execute(
                "SELECT chat_id, message_local_id, message_create_time, message_local_type, packed_info "
                f"FROM MessageResourceInfo{where_clause} "
                "ORDER BY message_create_time DESC LIMIT ?",
                (*params, max(1, max_rows)),
            )
            for chat_id, local_id, create_time, local_type, packed_info in rows:
                if deadline and time.time() >= deadline:
                    if log_fn:
                        log_fn("[wechat-decrypt] 图片资源索引达到媒体预算，停止继续读取")
                    break
                if int(local_type or 0) % 4294967296 != 3:
                    continue
                chat_username = chat_id_map.get(chat_id, "")
                if not chat_username:
                    continue
                file_md5 = extract_md5_from_packed_info(packed_info)
                if not file_md5:
                    continue

                exact_key = (chat_username, int(local_id or 0), int(create_time or 0))
                fallback_key = (chat_username, int(local_id or 0))
                if exact_key not in exact_map:
                    exact_map[exact_key] = file_md5
                if fallback_key not in fallback_map:
                    fallback_map[fallback_key] = file_md5
    except Exception:
        return {"exact": {}, "fallback": {}}

    return {"exact": exact_map, "fallback": fallback_map}


def load_cached_image_crypto_config():
    env_aes_key = normalize_image_aes_key(os.environ.get("WECHAT_IMAGE_AES_KEY") or "")
    xor_raw = (os.environ.get("WECHAT_IMAGE_XOR_KEY") or "0x88").strip()
    try:
        xor_key = int(xor_raw, 0)
    except ValueError:
        xor_key = 0x88

    saved = load_saved_image_crypto_config()
    saved_aes_key = normalize_image_aes_key(saved.get("image_aes_key") or "")
    saved_key_map = normalize_image_key_map(saved.get("image_keys") or {})
    if saved.get("image_xor_key") not in (None, ""):
        try:
            xor_key = int(saved["image_xor_key"])
        except Exception:
            pass

    primary_key = env_aes_key or saved_aes_key
    if not primary_key and saved_key_map:
        primary_key = next(iter(saved_key_map.values()))
    return primary_key, xor_key, saved_key_map


def enrich_chatlog_media(
    messages: list[dict],
    decrypted_dir: str,
    source_data_dir: str,
    preferred_pid: int,
    media_budget_deadline: float,
    log_fn=None,
    event_fn=None,
):
    def budget_exhausted() -> bool:
        return bool(media_budget_deadline and time.time() >= media_budget_deadline)

    def report_budget_exhausted():
        if log_fn:
            log_fn(
                f"[wechat-decrypt] 聊天记录媒体解析超出预算 {CHATLOG_MEDIA_BUDGET_SECONDS} 秒，后续仅保留消息文本"
            )
        emit_media_event(
            event_fn,
            "client_media_skipped",
            {
                "media_type": "mixed",
                "reason": "media_budget_exceeded",
                "budget_seconds": CHATLOG_MEDIA_BUDGET_SECONDS,
            },
        )

    image_messages = [item for item in messages if int(item.get("msg_type") or 0) == 3]
    voice_messages = [item for item in messages if int(item.get("msg_type") or 0) == 34]
    if not image_messages and not voice_messages:
        return
    if budget_exhausted():
        report_budget_exhausted()
        return

    min_image_create_time = min(
        (int(item.get("create_time") or 0) for item in image_messages),
        default=0,
    )
    resource_md5_maps = load_resource_md5_map(
        decrypted_dir,
        min_create_time=min_image_create_time,
        deadline=media_budget_deadline,
        max_rows=max(1000, min(12000, len(image_messages) * 3)),
        log_fn=log_fn,
    ) if image_messages else {"exact": {}, "fallback": {}}
    if budget_exhausted():
        report_budget_exhausted()
        return

    image_aes_key, image_xor_key, image_key_map = load_cached_image_crypto_config()
    voice_context = build_voice_media_context(decrypted_dir, log_fn=log_fn) if voice_messages else {"dbs": []}
    try:
        for item in messages:
            if budget_exhausted():
                report_budget_exhausted()
                break

            local_type = int(item.get("msg_type") or 0)
            local_id = int(item.get("local_id") or 0)
            create_time = int(item.get("create_time") or 0)
            media = None
            if local_type == 3:
                item["media_type"] = "image"
                media = try_load_image_media(
                    source_data_dir=source_data_dir,
                    chat_username=str(item.get("wxid") or ""),
                    local_id=local_id,
                    create_time=create_time,
                    resource_md5_maps=resource_md5_maps,
                    image_aes_key=image_aes_key,
                    image_xor_key=image_xor_key,
                    image_key_map=image_key_map,
                    preferred_pid=preferred_pid,
                    media_deadline=media_budget_deadline,
                    log_fn=log_fn,
                    event_fn=event_fn,
                )
            elif local_type == 34:
                item["media_type"] = "voice"
                media = try_load_voice_media(
                    voice_context=voice_context,
                    chat_username=str(item.get("wxid") or ""),
                    local_id=local_id,
                    create_time=create_time,
                    media_deadline=media_budget_deadline,
                    log_fn=log_fn,
                    event_fn=event_fn,
                )

            if media:
                item["media_mime"] = media["mime"]
                item["media_name"] = media["name"]
                item["media_data"] = media["data_b64"]
            if budget_exhausted():
                report_budget_exhausted()
                break
    finally:
        close_voice_media_context(voice_context)


def emit_media_event(event_fn, event_name: str, payload: dict):
    if event_fn is None:
        return
    try:
        event_fn(event_name, payload)
    except Exception:
        pass


def trim_message_buffer(messages: list[dict], max_messages: int, buffer_factor: int = 2):
    if max_messages <= 0:
        return messages
    hard_limit = max(max_messages * max(buffer_factor, 1), max_messages)
    if len(messages) <= hard_limit:
        return messages
    messages.sort(key=lambda item: item["create_time"])
    del messages[:-max_messages]
    return messages


def try_load_image_media(
    source_data_dir: str,
    chat_username: str,
    local_id: int,
    create_time: int,
    resource_md5_maps: dict,
    image_aes_key: str,
    image_xor_key: int,
    image_key_map: dict,
    preferred_pid: int = 0,
    media_deadline: float = 0,
    log_fn=None,
    event_fn=None,
):
    if media_deadline and time.time() >= media_deadline:
        return None

    search_roots = build_image_search_roots(source_data_dir, chat_username) if source_data_dir else []
    existing_search_roots = trim_paths_for_event(
        [path for path in search_roots if os.path.isdir(path)],
        root_dir=source_data_dir,
        limit=6,
    )
    recent_dat_samples = (
        collect_recent_image_dat_samples(source_data_dir, chat_username)
        if source_data_dir and not media_deadline
        else []
    )

    if not source_data_dir:
        emit_media_event(
            event_fn,
            "client_media_missing",
            {
                "media_type": "image",
                "reason": "source_data_dir_missing",
                "chat_username": chat_username,
                "local_id": local_id,
                "search_roots": [],
                "existing_search_roots": [],
            },
        )
        return None

    exact_key = (chat_username, local_id, create_time)
    fallback_key = (chat_username, local_id)
    file_md5 = (
        (resource_md5_maps.get("exact") or {}).get(exact_key)
        or (resource_md5_maps.get("fallback") or {}).get(fallback_key)
        or ""
    )
    if not file_md5:
        emit_media_event(
            event_fn,
            "client_media_missing",
            {
                "media_type": "image",
                "reason": "resource_md5_missing",
                "chat_username": chat_username,
                "local_id": local_id,
                "create_time": create_time,
                "resource_exact_count": len((resource_md5_maps.get("exact") or {})),
                "resource_fallback_count": len((resource_md5_maps.get("fallback") or {})),
                "source_data_dir": source_data_dir,
                "search_roots": trim_paths_for_event(search_roots, root_dir=source_data_dir, limit=6),
                "existing_search_roots": existing_search_roots,
                "recent_dat_samples": recent_dat_samples,
                "has_image_aes_key": bool(image_aes_key),
                "image_key_count": len(normalize_image_key_map(image_key_map)),
            },
        )
        return None

    dat_candidates = find_image_dat_candidates(
        source_data_dir,
        chat_username,
        file_md5,
        deadline=media_deadline,
    )
    if not dat_candidates:
        emit_media_event(
            event_fn,
            "client_media_missing",
            {
                "media_type": "image",
                "reason": "image_dat_missing",
                "chat_username": chat_username,
                "local_id": local_id,
                "create_time": create_time,
                "file_md5": file_md5,
                "source_data_dir": source_data_dir,
                "search_roots": trim_paths_for_event(search_roots, root_dir=source_data_dir, limit=6),
                "existing_search_roots": existing_search_roots,
                "recent_dat_samples": recent_dat_samples,
                "candidate_count": 0,
                "has_image_aes_key": bool(image_aes_key),
                "image_key_count": len(normalize_image_key_map(image_key_map)),
            },
        )
        return None

    attempted_files = []
    last_failure = {}
    for dat_path in dat_candidates:
        if media_deadline and time.time() >= media_deadline:
            break
        media, failure_meta = decode_image_dat_candidate(
            dat_path,
            image_aes_key=image_aes_key,
            image_xor_key=image_xor_key,
            image_key_map=image_key_map,
            preferred_pid=preferred_pid,
            deadline=media_deadline,
            log_fn=log_fn,
        )
        if media:
            if log_fn:
                log_fn(f"[wechat-decrypt] 图片命中: {chat_username} local_id={local_id} file={os.path.basename(dat_path)}")
            emit_media_event(
                event_fn,
                "client_media_loaded",
                {
                    "media_type": "image",
                    "chat_username": chat_username,
                    "local_id": local_id,
                    "create_time": create_time,
                    "file_md5": file_md5,
                    "file_path": dat_path,
                    "media_name": media["name"],
                    "media_mime": media["mime"],
                    "media_size": media["size"],
                    "candidate_count": len(dat_candidates),
                    "resolved_via_key_rescan": bool(failure_meta.get("lazy_key_scan_found")),
                },
            )
            return media

        attempted_files.append(dat_path)
        last_failure = failure_meta or {}

    emit_media_event(
        event_fn,
        "client_media_missing",
        {
            "media_type": "image",
            "reason": "image_decode_failed",
            "chat_username": chat_username,
            "local_id": local_id,
            "create_time": create_time,
            "file_md5": file_md5,
            "file_path": attempted_files[-1] if attempted_files else "",
            "attempted_files": trim_paths_for_event(attempted_files, root_dir=source_data_dir, limit=8),
            "candidate_count": len(dat_candidates),
            "v2_requires_aes_key": bool(last_failure.get("v2_requires_aes_key")),
            "has_image_aes_key": bool(image_aes_key),
            "image_xor_key": image_xor_key,
            "image_key_count": len(normalize_image_key_map(image_key_map)),
            "file_ct_hex": str(last_failure.get("file_ct_hex") or ""),
            "matched_image_key": bool(last_failure.get("matched_image_key")),
            "lazy_key_scan_attempted": bool(last_failure.get("lazy_key_scan_attempted")),
            "lazy_key_scan_found": bool(last_failure.get("lazy_key_scan_found")),
            "source_data_dir": source_data_dir,
            "search_roots": trim_paths_for_event(search_roots, root_dir=source_data_dir, limit=6),
            "existing_search_roots": existing_search_roots,
            "recent_dat_samples": recent_dat_samples,
        },
    )
    return None


def media_db_candidates(decrypted_dir: str):
    message_dir = os.path.join(decrypted_dir, "message")
    if not os.path.isdir(message_dir):
        return []
    return sorted(
        os.path.join(message_dir, name)
        for name in os.listdir(message_dir)
        if re.fullmatch(r"media_\d+\.db", name or "", re.IGNORECASE)
    )


def resolve_ffmpeg_path():
    configured = (
        os.environ.get("WECHAT_FFMPEG_PATH")
        or os.environ.get("WEFLOW_FFMPEG_PATH")
        or ""
    ).strip()
    if configured and os.path.isfile(configured):
        return configured

    runtime_dir = (os.environ.get("WECHAT_RUNTIME_DIR") or "").strip()
    local_candidates = []
    if runtime_dir:
        local_candidates.extend(
            [
                os.path.join(runtime_dir, "ffmpeg.exe"),
                os.path.join(runtime_dir, "ffmpeg"),
                os.path.join(runtime_dir, "bin", "ffmpeg.exe"),
                os.path.join(runtime_dir, "bin", "ffmpeg"),
            ]
        )

    module_dir = os.path.dirname(os.path.abspath(__file__))
    local_candidates.extend(
        [
            os.path.join(module_dir, "ffmpeg.exe"),
            os.path.join(module_dir, "ffmpeg"),
            os.path.join(module_dir, "bin", "ffmpeg.exe"),
            os.path.join(module_dir, "bin", "ffmpeg"),
        ]
    )

    for candidate in local_candidates:
        if candidate and os.path.isfile(candidate):
            return candidate

    return shutil.which("ffmpeg") or ""


def build_voice_media_context(decrypted_dir: str, log_fn=None):
    dbs = []
    for path in media_db_candidates(decrypted_dir):
        name_table = ""
        has_voice_info = False
        try:
            with closing(sqlite3.connect(path)) as conn:
                for candidate in ("Name2Id", "ChatName2Id"):
                    if table_exists(conn, candidate):
                        name_table = candidate
                        break
                has_voice_info = table_exists(conn, "VoiceInfo")
        except Exception as exc:
            if log_fn:
                log_fn(f"[wechat-decrypt] 语音媒体库探测失败: {path} error={exc}")
            continue

        if not has_voice_info or not name_table:
            continue
        dbs.append(
            {
                "path": path,
                "name_table": name_table,
                "conn": None,
                "chat_name_id_cache": {},
            }
        )

    return {
        "dbs": dbs,
        "ffmpeg_path": resolve_ffmpeg_path(),
        "pilk_ready": pilk is not None,
    }


def close_voice_media_context(voice_context: dict):
    for db_info in voice_context.get("dbs") or []:
        conn = db_info.get("conn")
        if conn is None:
            continue
        try:
            conn.close()
        except Exception:
            pass
        db_info["conn"] = None


def ensure_voice_media_connection(db_info: dict):
    conn = db_info.get("conn")
    if conn is not None:
        return conn
    conn = sqlite3.connect(db_info["path"])
    conn.row_factory = sqlite3.Row
    db_info["conn"] = conn
    return conn


def get_voice_chat_name_id(conn: sqlite3.Connection, db_info: dict, chat_username: str):
    cache = db_info.setdefault("chat_name_id_cache", {})
    if chat_username in cache:
        return cache[chat_username]

    row = conn.execute(
        f"SELECT rowid FROM {db_info['name_table']} WHERE user_name = ?",
        (chat_username,),
    ).fetchone()
    chat_name_id = row[0] if row else None
    cache[chat_username] = chat_name_id
    return chat_name_id


def fetch_voice_row(voice_context: dict, chat_username: str, local_id: int):
    for db_info in voice_context.get("dbs") or []:
        conn = ensure_voice_media_connection(db_info)
        chat_name_id = get_voice_chat_name_id(conn, db_info, chat_username)
        if chat_name_id is None:
            continue

        row = conn.execute(
            "SELECT voice_data, create_time FROM VoiceInfo "
            "WHERE chat_name_id = ? AND local_id = ? "
            "ORDER BY create_time DESC LIMIT 1",
            (chat_name_id, local_id),
        ).fetchone()
        if row:
            return row, db_info["path"]
    return None, ""


def normalize_silk_voice_data(voice_data):
    if not voice_data:
        return None, "voice_blob_empty"

    data = bytes(voice_data)
    silk_data = data[1:] if data[:1] == b"\x02" else data
    if not silk_data.startswith(b"#!SILK_V3"):
        return None, "voice_format_invalid"
    if not silk_data.endswith(b"\xff\xff"):
        silk_data += b"\xff\xff"
    return silk_data, ""


def silk_voice_to_mp3_bytes(silk_data: bytes, ffmpeg_path: str, timeout_seconds: float = 0):
    if pilk is None:
        return None, "pilk_missing"
    if not ffmpeg_path:
        return None, "ffmpeg_missing"

    with tempfile.TemporaryDirectory(prefix="wx-voice-") as temp_dir:
        silk_path = os.path.join(temp_dir, "voice.silk")
        pcm_path = os.path.join(temp_dir, "voice.pcm")
        mp3_path = os.path.join(temp_dir, "voice.mp3")

        with open(silk_path, "wb") as handle:
            handle.write(silk_data)

        try:
            pilk.decode(silk_path, pcm_path)
        except Exception as exc:
            return None, f"pilk_decode_failed: {exc}"

        try:
            result = subprocess.run(
                [
                    ffmpeg_path,
                    "-loglevel",
                    "error",
                    "-y",
                    "-f",
                    "s16le",
                    "-ar",
                    str(VOICE_SAMPLE_RATE),
                    "-ac",
                    "1",
                    "-i",
                    pcm_path,
                    "-vn",
                    "-codec:a",
                    "libmp3lame",
                    "-q:a",
                    "4",
                    mp3_path,
                ],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                check=False,
                timeout=timeout_seconds if timeout_seconds > 0 else None,
            )
        except subprocess.TimeoutExpired:
            return None, "media_budget_exceeded"
        if result.returncode != 0:
            detail = (result.stderr or result.stdout or "").strip()
            return None, f"ffmpeg_convert_failed: {detail or 'unknown error'}"

        try:
            with open(mp3_path, "rb") as handle:
                return handle.read(MAX_VOICE_MP3_BYTES + 1), ""
        except OSError as exc:
            return None, f"mp3_read_failed: {exc}"


def try_load_voice_media(
    voice_context: dict,
    chat_username: str,
    local_id: int,
    create_time: int,
    media_deadline: float = 0,
    log_fn=None,
    event_fn=None,
):
    if media_deadline and time.time() >= media_deadline:
        return None
    media_db_paths = trim_paths_for_event(
        [db_info.get("path", "") for db_info in voice_context.get("dbs") or [] if db_info.get("path")],
        limit=6,
    )

    if not voice_context.get("dbs"):
        emit_media_event(
            event_fn,
            "client_media_missing",
            {
                "media_type": "voice",
                "reason": "voice_db_missing",
                "chat_username": chat_username,
                "local_id": local_id,
                "create_time": create_time,
                "media_db_count": 0,
                "media_db_paths": media_db_paths,
            },
        )
        return None

    if not voice_context.get("pilk_ready"):
        emit_media_event(
            event_fn,
            "client_media_missing",
            {
                "media_type": "voice",
                "reason": "pilk_missing",
                "chat_username": chat_username,
                "local_id": local_id,
                "create_time": create_time,
                "media_db_count": len(voice_context.get("dbs") or []),
                "media_db_paths": media_db_paths,
            },
        )
        return None

    if not voice_context.get("ffmpeg_path"):
        emit_media_event(
            event_fn,
            "client_media_missing",
            {
                "media_type": "voice",
                "reason": "ffmpeg_missing",
                "chat_username": chat_username,
                "local_id": local_id,
                "create_time": create_time,
                "media_db_count": len(voice_context.get("dbs") or []),
                "media_db_paths": media_db_paths,
            },
        )
        return None

    try:
        row, db_path = fetch_voice_row(voice_context, chat_username, local_id)
    except Exception as exc:
        emit_media_event(
            event_fn,
            "client_media_missing",
            {
                "media_type": "voice",
                "reason": "voice_lookup_failed",
                "chat_username": chat_username,
                "local_id": local_id,
                "create_time": create_time,
                "media_db_count": len(voice_context.get("dbs") or []),
                "media_db_paths": media_db_paths,
                "error_message": str(exc),
            },
        )
        return None

    if not row:
        emit_media_event(
            event_fn,
            "client_media_missing",
            {
                "media_type": "voice",
                "reason": "voice_row_missing",
                "chat_username": chat_username,
                "local_id": local_id,
                "create_time": create_time,
                "media_db_count": len(voice_context.get("dbs") or []),
                "media_db_paths": media_db_paths,
            },
        )
        return None

    silk_data, normalize_error = normalize_silk_voice_data(row["voice_data"])
    if not silk_data:
        emit_media_event(
            event_fn,
            "client_media_missing",
            {
                "media_type": "voice",
                "reason": normalize_error or "voice_format_invalid",
                "chat_username": chat_username,
                "local_id": local_id,
                "create_time": create_time,
                "db_path": db_path,
                "media_db_count": len(voice_context.get("dbs") or []),
                "media_db_paths": media_db_paths,
            },
        )
        return None

    if media_deadline and time.time() >= media_deadline:
        return None

    remaining_seconds = max(1.0, media_deadline - time.time()) if media_deadline else 0
    mp3_bytes, convert_error = silk_voice_to_mp3_bytes(
        silk_data,
        voice_context.get("ffmpeg_path", ""),
        timeout_seconds=remaining_seconds,
    )
    if not mp3_bytes:
        if log_fn:
            log_fn(
                f"[wechat-decrypt] 语音转换失败: chat={chat_username} local_id={local_id} "
                f"db={os.path.basename(db_path)} reason={convert_error}"
            )
        emit_media_event(
            event_fn,
            "client_media_missing",
            {
                "media_type": "voice",
                "reason": "voice_decode_failed",
                "chat_username": chat_username,
                "local_id": local_id,
                "create_time": create_time,
                "db_path": db_path,
                "media_db_count": len(voice_context.get("dbs") or []),
                "media_db_paths": media_db_paths,
                "error_message": convert_error,
            },
        )
        return None

    if len(mp3_bytes) > MAX_VOICE_MP3_BYTES:
        emit_media_event(
            event_fn,
            "client_media_skipped",
            {
                "media_type": "voice",
                "reason": "file_too_large",
                "chat_username": chat_username,
                "local_id": local_id,
                "create_time": create_time,
                "file_name": f"voice_{create_time}_{local_id}.mp3",
                "file_size": len(mp3_bytes),
            },
        )
        return None

    if log_fn:
        log_fn(f"[wechat-decrypt] 语音命中: {chat_username} local_id={local_id} db={os.path.basename(db_path)}")
    emit_media_event(
        event_fn,
        "client_media_loaded",
        {
            "media_type": "voice",
            "chat_username": chat_username,
            "local_id": local_id,
            "create_time": create_time,
            "db_path": db_path,
            "media_name": f"voice_{create_time}_{local_id}.mp3",
            "media_mime": "audio/mpeg",
            "media_size": len(mp3_bytes),
        },
    )
    return {
        "mime": "audio/mpeg",
        "name": f"voice_{create_time}_{local_id}.mp3",
        "data_b64": b64encode(mp3_bytes).decode("ascii"),
        "size": len(mp3_bytes),
    }


def find_image_dat_candidates(
    source_data_dir: str,
    chat_username: str,
    file_md5: str,
    deadline: float = 0,
):
    username_hash = hashlib.md5(chat_username.encode("utf-8")).hexdigest()
    candidates = []

    attach_dir = os.path.join(source_data_dir, "msg", "attach", username_hash)
    if os.path.isdir(attach_dir):
        candidates.extend(glob.glob(os.path.join(attach_dir, "*", "Img", f"{file_md5}*.dat")))

    base_dirs = (
        os.path.join(source_data_dir, "MsgAttach", username_hash),
        os.path.join(source_data_dir, "FileStorage", "MsgAttach", username_hash),
    )
    for base_dir in base_dirs:
        if deadline and time.time() >= deadline:
            return []
        if os.path.isdir(base_dir):
            candidates.extend(glob.glob(os.path.join(base_dir, "Image", "*", f"{file_md5}*.dat")))

    if not candidates and not (deadline and time.time() >= deadline):
        recursive_roots = [attach_dir, *base_dirs]
        for root_dir in recursive_roots:
            if deadline and time.time() >= deadline:
                return []
            if not os.path.isdir(root_dir):
                continue
            candidates.extend(glob.glob(os.path.join(root_dir, "**", f"{file_md5}*.dat"), recursive=True))

    if not candidates and not (deadline and time.time() >= deadline):
        candidates.extend(get_image_dat_index(source_data_dir, deadline=deadline).get(file_md5.lower(), []))

    if not candidates:
        return []

    ranked = []
    for path in sorted(set(candidates)):
        name = os.path.basename(path).lower()
        size = 0
        try:
            size = os.path.getsize(path)
        except OSError:
            pass

        if "_t_" in name:
            rank = 5
        elif "_t." in name:
            rank = 4
        elif "_w." in name:
            rank = 2
        elif "_h." in name:
            rank = 1
        elif name == f"{file_md5}.dat".lower():
            rank = 0
        else:
            rank = 3
        ranked.append((rank, -size, path))

    ranked.sort()
    return [path for _, _, path in ranked]


def find_image_dat_file(source_data_dir: str, chat_username: str, file_md5: str):
    candidates = find_image_dat_candidates(source_data_dir, chat_username, file_md5)
    return candidates[0] if candidates else ""


def extract_image_ciphertext_from_dat(dat_path: str):
    if not dat_file_is_v2(dat_path):
        return b""
    try:
        with open(dat_path, "rb") as handle:
            return extract_image_sample_ciphertext(handle.read(31))
    except OSError:
        return b""


def decode_image_dat_candidate(
    dat_path: str,
    image_aes_key: str,
    image_xor_key: int,
    image_key_map: dict,
    preferred_pid: int = 0,
    deadline: float = 0,
    log_fn=None,
):
    media = decode_image_dat_file(
        dat_path,
        image_aes_key=image_aes_key,
        image_xor_key=image_xor_key,
        image_key_map=image_key_map,
    )
    if media:
        return media, {
            "v2_requires_aes_key": dat_file_is_v2(dat_path),
            "file_ct_hex": "",
            "matched_image_key": False,
            "lazy_key_scan_attempted": False,
            "lazy_key_scan_found": False,
        }

    ciphertext = extract_image_ciphertext_from_dat(dat_path)
    file_ct_hex = ciphertext.hex().lower() if ciphertext else ""
    normalized_key_map = normalize_image_key_map(image_key_map)
    matched_image_key = bool(file_ct_hex and normalized_key_map.get(file_ct_hex))
    lazy_key_scan_attempted = False
    lazy_key_scan_found = False

    if ciphertext and not matched_image_key and not (deadline and time.time() >= deadline):
        lazy_key_scan_attempted = True
        discovered_key = quick_scan_memory_for_image_aes_key(
            ciphertext,
            preferred_pid=preferred_pid,
            deadline=deadline,
            log_fn=log_fn,
        )
        discovered_key = normalize_image_aes_key(discovered_key)
        if discovered_key:
            lazy_key_scan_found = True
            if isinstance(image_key_map, dict):
                image_key_map[file_ct_hex] = discovered_key
            media = decode_image_dat_file(
                dat_path,
                image_aes_key=image_aes_key,
                image_xor_key=image_xor_key,
                image_key_map=image_key_map,
            )
            if media:
                return media, {
                    "v2_requires_aes_key": True,
                    "file_ct_hex": file_ct_hex,
                    "matched_image_key": True,
                    "lazy_key_scan_attempted": True,
                    "lazy_key_scan_found": True,
                }

    return None, {
        "v2_requires_aes_key": dat_file_is_v2(dat_path),
        "file_ct_hex": file_ct_hex,
        "matched_image_key": matched_image_key,
        "lazy_key_scan_attempted": lazy_key_scan_attempted,
        "lazy_key_scan_found": lazy_key_scan_found,
    }


def dat_file_is_v2(dat_path: str):
    try:
        with open(dat_path, "rb") as handle:
            return handle.read(6) == V2_MAGIC_FULL
    except OSError:
        return False


def aligned_aes_block_size(aes_size: int):
    if aes_size % 16:
        return aes_size + (16 - aes_size % 16)
    return aes_size + 16


def detect_image_format(header_bytes: bytes):
    if header_bytes[:3] == bytes([0xFF, 0xD8, 0xFF]):
        return "jpg"
    if header_bytes[:4] == bytes([0x89, 0x50, 0x4E, 0x47]):
        return "png"
    if header_bytes[:4] == b"wxgf":
        return "hevc"
    if header_bytes[:3] == b"GIF":
        return "gif"
    if header_bytes[:2] == b"BM":
        return "bmp"
    if header_bytes[:4] == b"RIFF" and len(header_bytes) >= 12 and header_bytes[8:12] == b"WEBP":
        return "webp"
    if header_bytes[:4] == bytes([0x49, 0x49, 0x2A, 0x00]):
        return "tif"
    return "bin"


def validate_decoded_image_bytes(decoded: bytes):
    fmt = detect_image_format(decoded[:16])
    if fmt == "bin":
        return None
    if fmt == "hevc":
        return decoded, fmt
    if fmt == "jpg":
        eof_idx = decoded.rfind(b"\xff\xd9")
        if eof_idx < 2:
            return None
        return decoded[: eof_idx + 2], fmt
    if fmt == "png":
        iend_idx = decoded.rfind(b"IEND")
        if iend_idx < 0:
            return None
        png_end = iend_idx + 8
        if png_end > len(decoded):
            return None
        return decoded[:png_end], fmt
    return decoded, fmt


def iter_unique_bytes(candidates):
    seen = set()
    for item in candidates:
        if not item:
            continue
        if item in seen:
            continue
        seen.add(item)
        yield item


def find_wxgf_hevc_offset(data: bytes):
    for marker in (b"\x00\x00\x00\x01\x40\x01", b"\x00\x00\x00\x01\x42\x01"):
        idx = data.find(marker)
        if idx >= 0:
            return idx
    return -1


def convert_wxgf_to_jpeg_bytes(wxgf_data: bytes, ffmpeg_path: str = ""):
    ffmpeg_bin = ffmpeg_path or resolve_ffmpeg_path()
    if not ffmpeg_bin:
        return None

    hevc_offset = find_wxgf_hevc_offset(wxgf_data)
    if hevc_offset < 0:
        return None

    with tempfile.TemporaryDirectory(prefix="wx-wxgf-") as temp_dir:
        hevc_path = os.path.join(temp_dir, "image.h265")
        jpeg_path = os.path.join(temp_dir, "image.jpg")
        with open(hevc_path, "wb") as handle:
            handle.write(wxgf_data[hevc_offset:])

        try:
            result = subprocess.run(
                [
                    ffmpeg_bin,
                    "-y",
                    "-loglevel",
                    "error",
                    "-f",
                    "hevc",
                    "-i",
                    hevc_path,
                    "-frames:v",
                    "1",
                    jpeg_path,
                ],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="ignore",
                check=False,
                timeout=20,
            )
        except Exception:
            return None

        if result.returncode != 0 or not os.path.isfile(jpeg_path):
            return None

        try:
            with open(jpeg_path, "rb") as handle:
                jpeg_bytes = handle.read()
        except OSError:
            return None
        return jpeg_bytes or None


def decrypt_v2_dat(data: bytes, image_aes_key: str, image_xor_key: int, image_key_map=None):
    if len(data) < 15:
        return None

    sig = data[:6]
    if sig not in (V2_MAGIC_FULL, V1_MAGIC_FULL):
        return None

    if sig == V1_MAGIC_FULL:
        aes_key = b"cfcd208495d565ef"
    else:
        ciphertext = data[15:31] if len(data) >= 31 else b""
        resolved_key, _ = resolve_image_aes_key_for_ciphertext(
            ciphertext,
            image_aes_key=image_aes_key,
            image_key_map=image_key_map,
        )
        if not resolved_key:
            return None
        aes_key = resolved_key.encode("ascii", errors="ignore")[:16]
        if len(aes_key) < 16:
            return None

    aes_size, xor_size = struct.unpack_from("<LL", data, 6)
    aligned_size = aligned_aes_block_size(aes_size)
    offset = 15
    if offset + aligned_size > len(data):
        return None

    cipher = AES.new(aes_key[:16], AES.MODE_ECB)
    encrypted_aes = data[offset : offset + aligned_size]
    decrypted_aes_full = cipher.decrypt(encrypted_aes)

    aes_prefix_candidates = []
    try:
        from Crypto.Util import Padding

        aes_prefix_candidates.append(Padding.unpad(decrypted_aes_full, AES.block_size))
    except Exception:
        pass
    if 0 < aes_size <= len(decrypted_aes_full):
        aes_prefix_candidates.append(decrypted_aes_full[:aes_size])
    aes_prefix_candidates.append(decrypted_aes_full.rstrip(b"\x00"))
    aes_prefix_candidates.append(decrypted_aes_full)

    tail_offset = offset + aligned_size
    remaining = data[tail_offset:]
    tail_variants = []
    if 0 <= xor_size <= len(remaining):
        if xor_size > 0:
            tail_variants.append((remaining[:-xor_size], remaining[-xor_size:], True))
        else:
            tail_variants.append((remaining, b"", True))
    tail_variants.append((remaining, b"", False))

    for aes_prefix in iter_unique_bytes(aes_prefix_candidates):
        for raw_data, xor_data, _ in tail_variants:
            decrypted = aes_prefix + raw_data + bytes(byte ^ image_xor_key for byte in xor_data)
            validated = validate_decoded_image_bytes(decrypted)
            if validated:
                return validated
    return None


def decrypt_legacy_dat(data: bytes):
    for fmt, magic in IMAGE_MAGIC.items():
        key = data[0] ^ magic[0]
        if all(index < len(data) and (data[index] ^ key) == magic[index] for index in range(len(magic))):
            return bytes(byte ^ key for byte in data), fmt

    bmp_magic = [0x42, 0x4D]
    key = data[0] ^ bmp_magic[0]
    if len(data) >= 2 and (data[1] ^ key) == bmp_magic[1]:
        decoded = bytes(byte ^ key for byte in data)
        if detect_image_format(decoded[:16]) == "bmp":
            return decoded, "bmp"
    return None


def decode_image_dat_file(
    dat_path: str,
    image_aes_key: str = "",
    image_xor_key: int = 0x88,
    image_key_map=None,
):
    try:
        with open(dat_path, "rb") as handle:
            data = handle.read(MAX_IMAGE_BYTES + 4096)
    except OSError:
        return None

    if len(data) > MAX_IMAGE_BYTES:
        return None

    head6 = data[:6]
    decoded = decrypt_v2_dat(
        data,
        image_aes_key=image_aes_key,
        image_xor_key=image_xor_key,
        image_key_map=image_key_map,
    )
    if decoded is None and head6 not in (V2_MAGIC_FULL, V1_MAGIC_FULL):
        decoded = decrypt_legacy_dat(data)
    if decoded is None:
        return None

    raw_bytes, fmt = decoded
    if fmt == "hevc":
        converted_jpeg = convert_wxgf_to_jpeg_bytes(raw_bytes)
        if converted_jpeg:
            raw_bytes = converted_jpeg
            fmt = "jpg"
    if len(raw_bytes) > MAX_IMAGE_BYTES:
        return None

    mime = IMAGE_MIME.get(fmt, "image/jpeg")
    file_stem = os.path.splitext(os.path.basename(dat_path))[0].split("_")[0]
    return {
        "mime": mime,
        "name": f"{file_stem}.{fmt}",
        "data_b64": b64encode(raw_bytes).decode("ascii"),
        "size": len(raw_bytes),
    }


def contact_db_candidates(decrypted_dir: str):
    return [
        os.path.join(decrypted_dir, "contact", "contact.db"),
        os.path.join(decrypted_dir, "Contact", "contact.db"),
        os.path.join(decrypted_dir, "contact.db"),
    ]


def resolve_db_path(decrypted_dir: str, candidates: list[str], log_fn=None) -> str:
    for path in candidates:
        if os.path.exists(path):
            return path

    wanted_names = {os.path.basename(path).lower() for path in candidates}
    for root, _, files in os.walk(decrypted_dir):
        for file_name in files:
            if file_name.lower() not in wanted_names:
                continue
            resolved = os.path.join(root, file_name)
            if log_fn:
                log_fn(f"[wechat-decrypt] 使用回退路径命中数据库: {resolved}")
            return resolved

    if any("favorite" in os.path.basename(path).lower() for path in candidates):
        for root, _, files in os.walk(decrypted_dir):
            for file_name in files:
                lower_name = file_name.lower()
                if not lower_name.endswith(".db"):
                    continue
                if "favorite" not in lower_name and "fav" not in lower_name:
                    continue
                resolved = os.path.join(root, file_name)
                if log_fn:
                    log_fn(f"[wechat-decrypt] 使用模糊回退路径命中收藏数据库: {resolved}")
                return resolved

    return ""


def load_contact_records(decrypted_dir: str, log_fn=None, event_fn=None):
    contact_path = resolve_db_path(decrypted_dir, contact_db_candidates(decrypted_dir), log_fn=log_fn)
    if not contact_path:
        if log_fn:
            log_fn("[wechat-decrypt] 未找到联系人数据库 contact.db")
        return {}

    avatar_map = load_avatar_map(decrypted_dir, log_fn=log_fn, event_fn=event_fn)
    contacts = {}
    try:
        with closing(sqlite3.connect(contact_path)) as conn:
            conn.row_factory = sqlite3.Row
            for row in conn.execute("SELECT username, alias, remark, nick_name FROM contact"):
                username = str(row["username"] or "").strip()
                if not username:
                    continue
                alias = str(row["alias"] or "").strip()
                remark = str(row["remark"] or "").strip()
                nick_name = str(row["nick_name"] or "").strip()
                contacts[username] = {
                    "wxid": username,
                    "alias": alias,
                    "remark": remark,
                    "nick_name": nick_name,
                    "display_name": remark or nick_name or alias or username,
                    "avatar": avatar_map.get(username, ""),
                    "source_updated_at": 0,
                    "extra_json": None,
                }
    except Exception as exc:
        if log_fn:
            log_fn(f"[wechat-decrypt] 读取联系人数据库失败，降级为空联系人表: {contact_path} ({exc})")
        return {}
    return contacts


def load_contact_map(decrypted_dir: str):
    return load_contact_records(decrypted_dir)


def head_image_db_candidates(decrypted_dir: str):
    return [
        os.path.join(decrypted_dir, "head_image", "head_image.db"),
        os.path.join(decrypted_dir, "HeadImage", "head_image.db"),
        os.path.join(decrypted_dir, "head_image.db"),
        os.path.join(decrypted_dir, "headimage", "headimage.db"),
        os.path.join(decrypted_dir, "HeadImage", "headimage.db"),
    ]


def favorite_db_candidates(decrypted_dir: str):
    return [
        os.path.join(decrypted_dir, "favorite", "favorite.db"),
        os.path.join(decrypted_dir, "favorite", "favorites.db"),
        os.path.join(decrypted_dir, "Favorite", "favorite.db"),
        os.path.join(decrypted_dir, "Favorite", "favorites.db"),
        os.path.join(decrypted_dir, "favorites", "favorite.db"),
        os.path.join(decrypted_dir, "favorites", "favorites.db"),
        os.path.join(decrypted_dir, "Favorites", "favorite.db"),
        os.path.join(decrypted_dir, "Favorites", "favorites.db"),
        os.path.join(decrypted_dir, "favorite.db"),
        os.path.join(decrypted_dir, "favorites.db"),
    ]


def trim_paths_for_event(paths, root_dir: str = "", limit: int = 10):
    items = []
    seen = set()
    for path in paths or []:
        text = str(path or "").strip()
        if not text:
            continue
        if root_dir:
            try:
                text = os.path.relpath(text, root_dir)
            except Exception:
                pass
        normalized = text.replace("\\", "/")
        if normalized in seen:
            continue
        seen.add(normalized)
        items.append(normalized)
        if len(items) >= limit:
            break
    return items


def iter_image_dat_storage_roots(source_data_dir: str):
    if not source_data_dir:
        return []
    return [
        os.path.join(source_data_dir, "msg", "attach"),
        os.path.join(source_data_dir, "MsgAttach"),
        os.path.join(source_data_dir, "FileStorage", "MsgAttach"),
    ]


def build_image_dat_index(source_data_dir: str, deadline: float = 0):
    index = {}
    for root_dir in iter_image_dat_storage_roots(source_data_dir):
        if deadline and time.time() >= deadline:
            return index, False
        if not os.path.isdir(root_dir):
            continue
        for walk_root, _, files in os.walk(root_dir):
            if deadline and time.time() >= deadline:
                return index, False
            for file_name in files:
                if deadline and time.time() >= deadline:
                    return index, False
                lower_name = file_name.lower()
                if not lower_name.endswith(".dat"):
                    continue
                file_md5 = lower_name.split("_", 1)[0]
                if len(file_md5) != 32 or not all(char in "0123456789abcdef" for char in file_md5):
                    continue
                index.setdefault(file_md5, []).append(os.path.join(walk_root, file_name))
    return index, True


def get_image_dat_index(source_data_dir: str, deadline: float = 0):
    cache_key = os.path.normcase(os.path.abspath(source_data_dir or ""))
    if not cache_key:
        return {}
    cached = _IMAGE_DAT_INDEX_CACHE.get(cache_key)
    if cached is not None:
        return cached
    index, complete = build_image_dat_index(source_data_dir, deadline=deadline)
    if complete:
        _IMAGE_DAT_INDEX_CACHE[cache_key] = index
    return index


def collect_db_file_inventory(root_dir: str, keywords=(), limit: int = 12):
    if not root_dir or not os.path.isdir(root_dir):
        return [], 0

    items = []
    total = 0
    lowered_keywords = [str(keyword or "").lower() for keyword in (keywords or ()) if str(keyword or "").strip()]
    for walk_root, _, files in os.walk(root_dir):
        for file_name in sorted(files):
            if not file_name.lower().endswith(".db"):
                continue
            full_path = os.path.join(walk_root, file_name)
            rel_path = os.path.relpath(full_path, root_dir).replace("\\", "/")
            lower_rel = rel_path.lower()
            if lowered_keywords and not any(keyword in lower_rel for keyword in lowered_keywords):
                continue
            total += 1
            if len(items) < limit:
                items.append(rel_path)
    return items, total


def build_image_search_roots(source_data_dir: str, chat_username: str):
    username_hash = hashlib.md5(chat_username.encode("utf-8")).hexdigest()
    return [
        os.path.join(source_data_dir, "msg", "attach", username_hash),
        os.path.join(source_data_dir, "MsgAttach", username_hash),
        os.path.join(source_data_dir, "FileStorage", "MsgAttach", username_hash),
    ]


def collect_recent_image_dat_samples(source_data_dir: str, chat_username: str, limit: int = 6):
    roots = build_image_search_roots(source_data_dir, chat_username)
    patterns = [
        os.path.join(roots[0], "*", "Img", "*.dat"),
        os.path.join(roots[1], "Image", "*", "*.dat"),
        os.path.join(roots[2], "Image", "*", "*.dat"),
    ]
    candidates = []
    seen = set()
    for pattern in patterns:
        for path in glob.glob(pattern):
            if not os.path.isfile(path) or path in seen:
                continue
            seen.add(path)
            try:
                mtime = os.path.getmtime(path)
            except OSError:
                mtime = 0
            candidates.append((mtime, path))

    candidates.sort(reverse=True)
    return trim_paths_for_event([path for _, path in candidates], root_dir=source_data_dir, limit=limit)


def get_table_columns(conn: sqlite3.Connection, table_name: str):
    rows = conn.execute(f"PRAGMA table_info([{table_name}])").fetchall()
    columns = []
    for row in rows:
        if isinstance(row, sqlite3.Row):
            columns.append({"name": row["name"], "type": row["type"]})
        else:
            columns.append({"name": row[1], "type": row[2]})
    return columns


def choose_column_name(columns: list[dict], exact_candidates: tuple[str, ...], fuzzy_candidates: tuple[str, ...] = ()):
    names = [str(column.get("name") or "") for column in columns]
    lowered = {name.lower(): name for name in names}
    for candidate in exact_candidates:
        if candidate.lower() in lowered:
            return lowered[candidate.lower()]
    for name in names:
        lower_name = name.lower()
        if any(token in lower_name for token in fuzzy_candidates):
            return name
    return ""


def decode_avatar_blob(blob):
    if not isinstance(blob, (bytes, bytearray)):
        return ""
    raw = bytes(blob)
    if not raw or len(raw) > MAX_AVATAR_BYTES:
        return ""

    fmt = detect_image_format(raw[:16])
    if fmt != "bin":
        return b64encode(raw).decode("ascii")

    decoded = decrypt_legacy_dat(raw)
    if decoded is None:
        return ""
    return b64encode(decoded[0]).decode("ascii")


def load_avatar_map(decrypted_dir: str, log_fn=None, event_fn=None):
    db_path = resolve_db_path(decrypted_dir, head_image_db_candidates(decrypted_dir), log_fn=log_fn)
    avatar_map = {}
    scanned_tables = 0
    if not db_path:
        if log_fn:
            log_fn("[wechat-decrypt] 未找到头像数据库 head_image.db")
        return avatar_map

    try:
        with closing(sqlite3.connect(db_path)) as conn:
            conn.row_factory = sqlite3.Row
            table_names = [
                row[0]
                for row in conn.execute(
                    "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"
                )
            ]
            for table_name in table_names:
                columns = get_table_columns(conn, table_name)
                username_col = choose_column_name(
                    columns,
                    ("username", "user_name", "usr_name", "wxid", "talker"),
                    ("user", "wxid", "talker"),
                )
                if not username_col:
                    continue

                blob_columns = [
                    column["name"]
                    for column in columns
                    if (
                        "blob" in str(column.get("type") or "").lower()
                        or any(
                            token in str(column.get("name") or "").lower()
                            for token in ("buf", "blob", "avatar", "head", "img")
                        )
                    )
                ]
                if not blob_columns:
                    continue

                scanned_tables += 1
                select_columns = ", ".join([f"[{username_col}]"] + [f"[{name}]" for name in blob_columns])
                rows = conn.execute(f"SELECT {select_columns} FROM [{table_name}] LIMIT 5000").fetchall()
                for row in rows:
                    username = str(row[username_col] or "").strip()
                    if not username or username in avatar_map:
                        continue
                    for blob_col in blob_columns:
                        avatar_b64 = decode_avatar_blob(row[blob_col])
                        if avatar_b64:
                            avatar_map[username] = avatar_b64
                            break
    except Exception:
        return avatar_map

    emit_media_event(
        event_fn,
        "client_avatar_scan_result",
        {"avatar_count": len(avatar_map), "table_count": scanned_tables},
    )
    if log_fn:
        log_fn(f"[wechat-decrypt] 头像扫描完成: {len(avatar_map)} 个联系人头像")
    return avatar_map


def sanitize_export_value(value):
    if value is None:
        return None
    if isinstance(value, (int, float, bool)):
        return value
    if isinstance(value, bytes):
        return {"kind": "bytes", "size": len(value)}
    text = str(value)
    if len(text) > 300:
        return text[:300] + "..."
    return text


def load_favorite_records(decrypted_dir: str, log_fn=None, event_fn=None, max_items: int = 1000):
    candidate_paths = favorite_db_candidates(decrypted_dir)
    favorite_db_files, favorite_db_file_count = collect_db_file_inventory(
        decrypted_dir,
        keywords=("favorite", "fav"),
    )
    db_path = resolve_db_path(decrypted_dir, candidate_paths, log_fn=log_fn)
    if not db_path:
        if log_fn:
            log_fn("[wechat-decrypt] 未找到收藏数据库 favorite.db / favorites.db")
        if event_fn:
            event_fn(
                "client_favorites_export_result",
                {
                    "success": False,
                    "favorite_count": 0,
                    "reason": "favorite_db_missing",
                    "candidate_paths": trim_paths_for_event(candidate_paths, root_dir=decrypted_dir),
                    "db_files_seen": favorite_db_files,
                    "db_file_count": favorite_db_file_count,
                },
            )
        return []

    favorites = []
    seen = set()
    table_summaries = []
    try:
        with closing(sqlite3.connect(db_path)) as conn:
            conn.row_factory = sqlite3.Row
            table_names = [
                row[0]
                for row in conn.execute(
                    "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '%fts%'"
                )
            ]
            for table_name in table_names:
                columns = get_table_columns(conn, table_name)
                column_names = [column["name"] for column in columns]
                rows = conn.execute(f"SELECT rowid AS __rowid__, * FROM [{table_name}] ORDER BY rowid DESC LIMIT 500").fetchall()
                table_summaries.append(
                    {
                        "table_name": table_name,
                        "sample_row_count": len(rows),
                        "column_count": len(column_names),
                        "columns": column_names[:12],
                    }
                )

                for row in rows:
                    source_id = ""
                    for candidate in ("id", "item_id", "local_id", "fav_local_id", "record_id", "__rowid__"):
                        if candidate in row.keys():
                            source_id = str(row[candidate] or "").strip()
                            if source_id:
                                break
                    if not source_id:
                        continue

                    unique_key = (table_name, source_id)
                    if unique_key in seen:
                        continue
                    seen.add(unique_key)

                    def pick_text(*candidates):
                        for candidate in candidates:
                            if candidate in row.keys():
                                text = str(row[candidate] or "").strip()
                                if text:
                                    return text
                        return ""

                    def pick_time():
                        for candidate in ("update_time", "updated_time", "create_time", "time", "timestamp"):
                            if candidate in row.keys():
                                value = row[candidate]
                                try:
                                    ivalue = int(value or 0)
                                    if ivalue > 0:
                                        return ivalue
                                except Exception:
                                    continue
                        return 0

                    title = pick_text("title", "tag", "name", "digest", "caption")
                    summary = pick_text("summary", "description", "desc", "content", "source", "url")
                    item_type = pick_text("item_type", "type")
                    item_sub_type = pick_text("item_sub_type", "sub_type")

                    payload = {}
                    for column_name in column_names:
                        value = sanitize_export_value(row[column_name])
                        if value in (None, "", [], {}):
                            continue
                        payload[column_name] = value

                    favorites.append(
                        {
                            "source_table": table_name,
                            "source_id": source_id,
                            "title": title or summary or f"{table_name}#{source_id}",
                            "summary": summary or title,
                            "item_type": item_type,
                            "item_sub_type": item_sub_type,
                            "source_updated_at": pick_time(),
                            "data_json": payload,
                        }
                    )
                    if len(favorites) >= max_items:
                        break
                if len(favorites) >= max_items:
                    break
    except Exception as exc:
        if log_fn:
            log_fn(f"[wechat-decrypt] 读取收藏数据库失败: {db_path} ({exc})")
        if event_fn:
            event_fn(
                "client_favorites_export_result",
                {
                    "success": False,
                    "favorite_count": len(favorites),
                    "reason": "favorite_db_read_failed",
                    "db_path": db_path,
                    "error_message": str(exc),
                    "table_count": len(table_summaries),
                    "table_summaries": table_summaries[:8],
                    "candidate_paths": trim_paths_for_event(candidate_paths, root_dir=decrypted_dir),
                    "db_files_seen": favorite_db_files,
                    "db_file_count": favorite_db_file_count,
                },
            )
        return favorites

    if log_fn:
        log_fn(f"[wechat-decrypt] 收藏导出完成: {len(favorites)} 条")
    if event_fn:
        event_fn(
            "client_favorites_export_result",
            {
                "success": True,
                "favorite_count": len(favorites),
                "db_path": db_path,
                "table_count": len(table_summaries),
                "table_summaries": table_summaries[:8],
                "candidate_paths": trim_paths_for_event(candidate_paths, root_dir=decrypted_dir),
                "db_files_seen": favorite_db_files,
                "db_file_count": favorite_db_file_count,
            },
        )
    return favorites
def load_sender_map(conn: sqlite3.Connection):
    sender_map = {}
    table_name = ""
    for candidate in ("Name2Id", "ChatName2Id"):
        if table_exists(conn, candidate):
            table_name = candidate
            break

    if not table_name:
        return sender_map

    for row in conn.execute(f"SELECT rowid, user_name FROM {table_name}"):
        sender_map[row[0]] = row[1]
    return sender_map


def table_exists(conn: sqlite3.Connection, table_name: str):
    row = conn.execute(
        "SELECT 1 FROM sqlite_master WHERE type='table' AND name=?",
        (table_name,),
    ).fetchone()
    return row is not None


def display_name(contact_map: dict, username: str):
    info = contact_map.get(username, {})
    return info.get("display_name") or info.get("remark") or info.get("nick_name") or info.get("alias") or username


def get_content(raw, ct_flag) -> str:
    if raw is None:
        return ""
    if isinstance(raw, bytes):
        if ct_flag == 4 and _ZSTD is not None:
            try:
                return _ZSTD.decompress(raw).decode("utf-8", errors="replace")
            except Exception:
                pass
        return raw.decode("utf-8", errors="replace")
    return str(raw)


def xml_extract(content: str, tag: str) -> str:
    match = re.search(rf"<{tag}>(.*?)</{tag}>", content, re.DOTALL)
    if match:
        return match.group(1).strip()
    return ""


def friendly_content(msg_type: int, content: str) -> str:
    if msg_type == 1:
        return content[:500]
    if msg_type == 3:
        return "[图片]"
    if msg_type == 34:
        return "[语音]"
    if msg_type == 42:
        title = xml_extract(content, "nickname")
        return f"[名片: {title}]" if title else "[名片]"
    if msg_type == 43:
        return "[视频]"
    if msg_type == 47:
        return "[表情包]"
    if msg_type == 48:
        label = xml_extract(content, "label")
        return f"[位置: {label}]" if label else "[位置]"
    if msg_type == 49:
        title = xml_extract(content, "title")
        return f"[分享: {title}]" if title else "[文件/链接]"
    if msg_type in (10000, 10002):
        return f"[系统: {content[:100]}]"
    return content[:200]
