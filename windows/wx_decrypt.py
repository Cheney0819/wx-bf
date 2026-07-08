"""
wx_decrypt.py - 微信 4.x 解密统一入口
面向新版 Weixin.exe / xwechat_files / db_storage，使用 wechat-decrypt SQLCipher 4 链路。
"""
import csv
import io
import json
import os
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from string import ascii_uppercase
from base64 import b64encode
from datetime import datetime
from pathlib import Path

from wechat_decrypt_engine import (
    decrypt_database_tree,
    export_chatlog_json,
    export_contacts_json,
    export_favorites_json,
    export_chatlog_memory,
    extract_database_keys_windows,
)

LOGIN_STATUS_PREFIX = "__WX_LOGIN_STATUS__="
RUNTIME_EVENT_PREFIX = "__WX_EVENT__="
DB_KEY_CACHE_PREFIX = "__WX_DB_KEY_CACHE__="
EXPORT_JSON_NAME = "chatlog_export.json"
CONTACT_EXPORT_JSON_NAME = "contact_export.json"
FAVORITE_EXPORT_JSON_NAME = "favorite_export.json"
META_JSON_NAME = "decrypt_meta.json"
MAX_MESSAGES = 5000
MAX_IMAGE_BYTES = 5 * 1024 * 1024
WCDB_SESSION_FETCH_LIMIT = 200
WCDB_MESSAGE_FETCH_LIMIT = 500
MEMORY_BATCH_SIZE = 500
HTTP_RETRY_MAX_ATTEMPTS = 3
HTTP_RETRY_BASE_DELAY_SECONDS = 1.5
LOCAL_DEBUG_LOG_ENABLED = (
    (os.environ.get("WEFLOW_LOCAL_DEBUG_LOG") or "").strip().lower()
    in {"1", "true", "yes", "on"}
)
_REQUEST_SEQUENCE = 0
_DEFAULT_RUNTIME_SESSION_ID = f"client-py-{os.getpid()}-{int(time.time())}"


def configure_stdio():
    for stream_name in ("stdout", "stderr"):
        stream = getattr(sys, stream_name, None)
        if stream is None:
            continue
        reconfigure = getattr(stream, "reconfigure", None)
        if callable(reconfigure):
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except Exception:
                pass


configure_stdio()


def emit_login_status(is_logged_in: bool):
    print(f"{LOGIN_STATUS_PREFIX}{1 if is_logged_in else 0}", flush=True)


def emit_runtime_event(event_name: str, payload: dict):
    print(
        f"{RUNTIME_EVENT_PREFIX}"
        + json.dumps(
            {
                "event_name": event_name,
                "payload": payload or {},
            },
            ensure_ascii=False,
        ),
        flush=True,
    )


def emit_db_key_cache(cache_payload: dict):
    print(f"{DB_KEY_CACHE_PREFIX}{json.dumps(cache_payload, ensure_ascii=False)}", flush=True)


def log_debug(message: str):
    if LOCAL_DEBUG_LOG_ENABLED:
        print(f"[wx_decrypt] {message}", flush=True)


def emit_extract_failed(error_message: str, stage: str = "decrypt_process", **extra):
    payload = {
        "stage": stage,
        "error_message": error_message,
    }
    payload.update(extra)
    emit_runtime_event("client_extract_failed", payload)


def get_runtime_server_config() -> tuple[str, str]:
    return (
        (os.environ.get("WECHAT_MONITOR_SERVER_URL") or "").strip(),
        (os.environ.get("WECHAT_MONITOR_SERVER_TOKEN") or "").strip(),
    )


def get_runtime_session_context() -> tuple[str, str]:
    session_id = (os.environ.get("WECHAT_MONITOR_SESSION_ID") or "").strip()
    source = (os.environ.get("WECHAT_MONITOR_CLIENT_SOURCE") or "").strip()
    if not session_id:
        session_id = _DEFAULT_RUNTIME_SESSION_ID
    if not source:
        source = "client_py"
    return session_id, source


def get_runtime_db_key_cache():
    raw = (os.environ.get("WECHAT_MONITOR_DB_KEY_CACHE_JSON") or "").strip()
    if not raw:
        return None

    try:
        parsed = json.loads(raw)
    except Exception:
        return None

    return parsed if isinstance(parsed, dict) else None


def build_request_id(prefix: str, batch_index: int = 0) -> str:
    global _REQUEST_SEQUENCE

    safe_prefix = "".join(
        char if char.isalnum() or char == "_" else "_"
        for char in (prefix or "request").strip().lower()
    ).strip("_") or "request"
    session_id, _ = get_runtime_session_context()
    _REQUEST_SEQUENCE += 1
    return (
        f"{session_id}-{safe_prefix}-{int(time.time() * 1000)}"
        f"-b{batch_index}-n{_REQUEST_SEQUENCE}"
    )


def build_db_key_cache_payload(
    *,
    pid: int,
    wxid: str,
    data_dir: str,
    db_storage_dir: str,
    key_scan_result: dict,
):
    return {
        "schema": 1,
        "pid": int(key_scan_result.get("selected_pid") or pid or 0),
        "wxid": wxid or "",
        "data_dir": normalize_windows_path(data_dir or ""),
        "db_storage_dir": normalize_windows_path(db_storage_dir or ""),
        "db_count": int(key_scan_result.get("db_count") or 0),
        "matched_count": int(key_scan_result.get("matched_count") or 0),
        "missing_count": int(key_scan_result.get("missing_count") or 0),
        "missing_dbs": list(key_scan_result.get("missing_dbs") or [])[:200],
        "keys": key_scan_result.get("keys") or {},
        "cached_at": datetime.utcnow().isoformat(timespec="seconds") + "Z",
    }


def try_get_cached_key_scan_result(
    *,
    pid: int,
    selected_wxid: str,
    selected_data_dir: str,
    selected_db_storage_dir: str,
):
    cache_payload = get_runtime_db_key_cache()
    if not cache_payload:
        return None

    keys = cache_payload.get("keys") or {}
    if not isinstance(keys, dict) or not keys:
        return None

    cached_pid = int(cache_payload.get("pid") or 0)
    if cached_pid <= 0 or cached_pid != int(pid or 0):
        return None

    cached_wxid = str(cache_payload.get("wxid") or "").strip()
    if cached_wxid and cached_wxid != (selected_wxid or "").strip():
        return None

    cached_data_dir = normalize_windows_path(str(cache_payload.get("data_dir") or ""))
    current_data_dir = normalize_windows_path(selected_data_dir or "")
    if cached_data_dir and current_data_dir and cached_data_dir != current_data_dir:
        return None

    cached_db_storage_dir = normalize_windows_path(str(cache_payload.get("db_storage_dir") or ""))
    current_db_storage_dir = normalize_windows_path(selected_db_storage_dir or "")
    if cached_db_storage_dir and current_db_storage_dir and cached_db_storage_dir != current_db_storage_dir:
        return None

    for value in keys.values():
        if not isinstance(value, dict) or not str(value.get("enc_key") or "").strip():
            return None

    matched_count = int(cache_payload.get("matched_count") or len(keys))
    missing_dbs = list(cache_payload.get("missing_dbs") or [])
    missing_count = int(cache_payload.get("missing_count") or len(missing_dbs))

    return {
        "success": True,
        "selected_pid": cached_pid,
        "db_count": int(cache_payload.get("db_count") or (matched_count + missing_count)),
        "matched_count": matched_count,
        "missing_count": missing_count,
        "missing_dbs": missing_dbs,
        "total_hex_matches": 0,
        "duration_seconds": 0.0,
        "keys": keys,
        "source": "memory_cache",
    }


def should_fallback_from_cached_decrypt(decrypt_dir: Path, decrypt_result: dict) -> bool:
    if not decrypt_result.get("success"):
        return True
    if int(decrypt_result.get("failed_count") or 0) > 0:
        return True
    return not (decrypt_dir / "message").is_dir()


def is_malformed_sqlite_error(exc: Exception) -> bool:
    return "database disk image is malformed" in str(exc or "").lower()


def should_retry_status_code(status_code: int) -> bool:
    return status_code in {408, 429} or status_code >= 500


def post_json_with_retry(
    url: str,
    payload_obj: dict,
    timeout: int = 60,
    max_attempts: int = HTTP_RETRY_MAX_ATTEMPTS,
    base_delay_seconds: float = HTTP_RETRY_BASE_DELAY_SECONDS,
):
    payload = json.dumps(payload_obj, ensure_ascii=False).encode("utf-8")
    last_error = ""

    for attempt in range(1, max(1, max_attempts) + 1):
        req = urllib.request.Request(
            url,
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                body = resp.read().decode("utf-8", errors="ignore")
                return True, int(getattr(resp, "status", 200) or 200), body
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", errors="ignore")
            last_error = body[:300] if body else str(exc)
            if attempt >= max_attempts or not should_retry_status_code(int(exc.code or 0)):
                return False, int(exc.code or 0), body
        except Exception as exc:
            last_error = str(exc)
            if attempt >= max_attempts:
                return False, 0, last_error

        time.sleep(max(0.5, base_delay_seconds) * attempt)

    return False, 0, last_error


def push_messages_direct(messages: list[dict], server_url: str, server_token: str):
    if not server_url or not server_token:
        raise RuntimeError("没有提供服务器地址或上传密钥")

    total_added = 0
    uploaded = 0
    session_id, source = get_runtime_session_context()
    batches = [
        messages[index : index + MEMORY_BATCH_SIZE]
        for index in range(0, len(messages), MEMORY_BATCH_SIZE)
    ]

    for batch_index, batch in enumerate(batches, start=1):
        request_id = build_request_id("messages", batch_index)
        emit_runtime_event(
            "client_push_batch_started",
            {
                "batch_index": batch_index,
                "batch_total": len(batches),
                "message_count": len(batch),
                "request_id": request_id,
            },
        )
        payload_obj = {
            "messages": batch,
            "token": server_token,
            "source": source,
            "session_id": session_id,
            "request_id": request_id,
        }
        success, status_code, body = post_json_with_retry(server_url, payload_obj)
        if success:
            uploaded += len(batch)
            added = 0
            if body:
                try:
                    parsed = json.loads(body)
                    added = int(parsed.get("added") or 0)
                except Exception:
                    added = 0
            total_added += added
            emit_runtime_event(
                "client_push_batch_result",
                {
                    "success": True,
                    "batch_index": batch_index,
                    "batch_total": len(batches),
                    "message_count": len(batch),
                    "uploaded_count": uploaded,
                    "added_count": total_added,
                    "status_code": status_code,
                    "request_id": request_id,
                },
            )
            continue

        error_message = body[:300] if body else "上传失败"
        emit_runtime_event(
            "client_push_batch_result",
            {
                "success": False,
                "batch_index": batch_index,
                "batch_total": len(batches),
                "message_count": len(batch),
                "status_code": status_code,
                "error_message": error_message,
                "request_id": request_id,
            },
        )
        if status_code > 0:
            raise RuntimeError(f"上传失败: HTTP {status_code} {body[:200]}")  # noqa: TRY003
        raise RuntimeError(f"上传失败: {error_message}")  # noqa: TRY003

    return {
        "uploaded_count": uploaded,
        "added_count": total_added,
        "batch_count": len(batches),
    }


def runtime_dir() -> Path:
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


os.environ.setdefault("WECHAT_RUNTIME_DIR", str(runtime_dir()))


def reset_output_artifacts(decrypt_dir: Path):
    for name in (EXPORT_JSON_NAME, CONTACT_EXPORT_JSON_NAME, FAVORITE_EXPORT_JSON_NAME, META_JSON_NAME):
        path = decrypt_dir / name
        if path.exists():
            path.unlink()


def write_meta(
    decrypt_dir: Path,
    db_dir: str,
    mode: str,
    work_dir: str = "",
    decrypt_key: str = "",
    wxid: str = "",
):
    payload = {
        "db_dir": db_dir,
        "mode": mode,
    }
    if work_dir:
        payload["work_dir"] = work_dir
    if decrypt_key:
        payload["decrypt_key"] = decrypt_key
    if wxid:
        payload["wxid"] = wxid
    (decrypt_dir / META_JSON_NAME).write_text(
        json.dumps(payload, ensure_ascii=False),
        encoding="utf-8",
    )


def detect_wechat_processes():
    try:
        result = subprocess.run(
            ["tasklist", "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="ignore",
            check=False,
        )
        rows = csv.reader(io.StringIO(result.stdout))
        names = []
        for row in rows:
            if not row:
                continue
            name = (row[0] or "").strip()
            if name.lower() in {"wechat.exe", "weixin.exe"}:
                names.append(name)
        return sorted(set(names))
    except Exception:
        return []


def normalize_windows_path(path: str) -> str:
    return path.replace("/", "\\").rstrip("\\")


def is_unc_path(path: str) -> bool:
    normalized = normalize_windows_path(path)
    return normalized.startswith("\\\\")


def is_local_drive_path(path: str) -> bool:
    normalized = normalize_windows_path(path)
    return len(normalized) >= 3 and normalized[1:3] == ":\\" and normalized[0].isalpha()


def find_first_glob(path: Path, pattern: str) -> Path | None:
    try:
        for item in path.glob(pattern):
            return item
    except Exception:
        return None
    return None


def collect_v4_data_roots_from_wechat_ini() -> list[Path]:
    appdata = (os.environ.get("APPDATA") or "").strip()
    if not appdata:
        return []

    config_dir = Path(appdata) / "Tencent" / "xwechat" / "config"
    if not config_dir.is_dir():
        return []

    roots = []
    seen = set()
    for ini_file in sorted(config_dir.glob("*.ini")):
        content = None
        for encoding in ("utf-8", "gbk"):
            try:
                content = ini_file.read_text(encoding=encoding).strip()
                break
            except UnicodeDecodeError:
                continue
            except OSError:
                content = None
                break

        if not content or any(ch in content for ch in "\n\r\x00"):
            continue

        root = Path(content)
        if not root.is_dir():
            continue

        normalized = normalize_windows_path(str(root))
        if normalized in seen:
            continue
        seen.add(normalized)
        roots.append(root)

    return roots


def collect_windows_drive_roots() -> list[Path]:
    roots = []
    seen = set()
    for drive in ascii_uppercase:
        drive_root = Path(f"{drive}:\\")
        try:
            if not drive_root.exists():
                continue
        except Exception:
            continue

        normalized = normalize_windows_path(str(drive_root))
        if normalized in seen:
            continue
        seen.add(normalized)
        roots.append(drive_root)
    return roots


def get_v4_db_markers(path: Path) -> tuple[Path | None, Path | None]:
    session_dir = path / "db_storage" / "session"
    message_dir = path / "db_storage" / "message"

    session_db = session_dir / "session.db"
    if not session_db.exists():
        session_db = find_first_glob(session_dir, "*.db")

    message_db = message_dir / "message_0.db"
    if not message_db.exists():
        # 有些环境实际是 biz_message_0.db，这里也视为可用消息库。
        biz_message_db = message_dir / "biz_message_0.db"
        if biz_message_db.exists():
            message_db = biz_message_db
        else:
            message_db = (
                find_first_glob(message_dir, "message_*.db")
                or find_first_glob(message_dir, "biz_message_*.db")
                or find_first_glob(message_dir, "*.db")
            )

    return session_db, message_db


def is_valid_v4_data_dir(path: Path) -> bool:
    try:
        session_db, message_db = get_v4_db_markers(path)
        return session_db is not None and message_db is not None
    except Exception:
        return False


def get_v4_dir_signal(path: Path) -> dict:
    session_db, message_db = get_v4_db_markers(path)
    normalized_path = normalize_windows_path(str(path))
    signal = {
        "path": str(path),
        "normalized_path": normalized_path,
        "session_mtime": 0.0,
        "message_mtime": 0.0,
        "score": 0.0,
        "is_local_drive": is_local_drive_path(normalized_path),
        "is_unc": is_unc_path(normalized_path),
        "session_db": str(session_db) if session_db else "",
        "message_db": str(message_db) if message_db else "",
    }
    if session_db is not None:
        try:
            signal["session_mtime"] = session_db.stat().st_mtime
        except Exception:
            pass
    if message_db is not None:
        try:
            signal["message_mtime"] = message_db.stat().st_mtime
        except Exception:
            pass

    # 更看重新消息库的最近活跃时间，其次参考 session.db。
    signal["score"] = signal["message_mtime"] * 2 + signal["session_mtime"]
    return signal


def collect_v4_data_dir_candidates():
    env_roots = collect_v4_data_roots_from_wechat_ini() + [
        Path.home(),
        Path.home() / "Documents",
    ]

    userprofile = (os.environ.get("USERPROFILE") or "").strip()
    if userprofile:
        env_roots.append(Path(userprofile))
        env_roots.append(Path(userprofile) / "Documents")

    homepath = (os.environ.get("HOMEPATH") or "").strip()
    homedrive = (os.environ.get("HOMEDRIVE") or "").strip()
    if homepath:
        home_root = Path(f"{homedrive}{homepath}") if homedrive else Path(homepath)
        env_roots.append(home_root)
        env_roots.append(home_root / "Documents")

    for env_name in ("OneDrive", "OneDriveConsumer"):
        value = (os.environ.get(env_name) or "").strip()
        if value:
            env_roots.append(Path(value))
            env_roots.append(Path(value) / "Documents")

    search_patterns = [
        Path("Documents") / "Weixin Files" / "*" / "db_storage" / "session" / "*.db",
        Path("Documents") / "WeChat Files" / "*" / "db_storage" / "session" / "*.db",
        Path("Documents") / "xwechat_files" / "*" / "db_storage" / "session" / "*.db",
        Path("Weixin Files") / "*" / "db_storage" / "session" / "*.db",
        Path("WeChat Files") / "*" / "db_storage" / "session" / "*.db",
        Path("xwechat_files") / "*" / "db_storage" / "session" / "*.db",
    ]

    # 面向换电脑的有限深度扫描：
    # 允许在盘符根目录下寻找 Users/*、Mac/Home/* 等变体，而不是写死绝对路径。
    drive_search_patterns = [
        Path("*") / "Documents" / "Weixin Files" / "*" / "db_storage" / "session" / "*.db",
        Path("*") / "Documents" / "WeChat Files" / "*" / "db_storage" / "session" / "*.db",
        Path("*") / "Documents" / "xwechat_files" / "*" / "db_storage" / "session" / "*.db",
        Path("*") / "*" / "Documents" / "Weixin Files" / "*" / "db_storage" / "session" / "*.db",
        Path("*") / "*" / "Documents" / "WeChat Files" / "*" / "db_storage" / "session" / "*.db",
        Path("*") / "*" / "Documents" / "xwechat_files" / "*" / "db_storage" / "session" / "*.db",
        Path("*") / "Weixin Files" / "*" / "db_storage" / "session" / "*.db",
        Path("*") / "WeChat Files" / "*" / "db_storage" / "session" / "*.db",
        Path("*") / "xwechat_files" / "*" / "db_storage" / "session" / "*.db",
        Path("*") / "*" / "Weixin Files" / "*" / "db_storage" / "session" / "*.db",
        Path("*") / "*" / "WeChat Files" / "*" / "db_storage" / "session" / "*.db",
        Path("*") / "*" / "xwechat_files" / "*" / "db_storage" / "session" / "*.db",
    ]

    unique_roots = []
    seen_roots = set()
    for root in env_roots:
        try:
            normalized = normalize_windows_path(str(root.resolve()))
        except Exception:
            normalized = normalize_windows_path(str(root))
        if normalized in seen_roots:
            continue
        seen_roots.add(normalized)
        unique_roots.append(root)

    log_debug(
        "v4 目录扫描根路径: " + ", ".join(normalize_windows_path(str(root)) for root in unique_roots[:10])
    )

    drive_roots = []
    seen_drive_roots = set()

    for root in unique_roots:
        anchor = root.anchor or ""
        if not anchor:
            continue
        drive_root = Path(anchor)
        normalized = normalize_windows_path(str(drive_root))
        if normalized in seen_drive_roots:
            continue
        seen_drive_roots.add(normalized)
        drive_roots.append(drive_root)

    system_drive = (os.environ.get("SystemDrive") or "").strip()
    if system_drive:
        drive_root = Path(f"{system_drive}\\")
        normalized = normalize_windows_path(str(drive_root))
        if normalized not in seen_drive_roots:
            seen_drive_roots.add(normalized)
            drive_roots.append(drive_root)

    for drive_root in collect_windows_drive_roots():
        normalized = normalize_windows_path(str(drive_root))
        if normalized in seen_drive_roots:
            continue
        seen_drive_roots.add(normalized)
        drive_roots.append(drive_root)

    discovered = []
    discovered_keys = set()
    for root in unique_roots:
        if not root.exists():
            continue
        for pattern in search_patterns:
            for session_db in root.glob(str(pattern)):
                data_dir = session_db.parent.parent.parent
                if not is_valid_v4_data_dir(data_dir):
                    continue
                try:
                    resolved = data_dir.resolve()
                except Exception:
                    resolved = data_dir
                key = normalize_windows_path(str(resolved))
                if key in discovered_keys:
                    continue
                discovered_keys.add(key)
                signal = get_v4_dir_signal(resolved)
                signal["key"] = key
                discovered.append(signal)

    for drive_root in drive_roots:
        if not drive_root.exists():
            continue
        for pattern in drive_search_patterns:
            for session_db in drive_root.glob(str(pattern)):
                data_dir = session_db.parent.parent.parent
                if not is_valid_v4_data_dir(data_dir):
                    continue
                try:
                    resolved = data_dir.resolve()
                except Exception:
                    resolved = data_dir
                key = normalize_windows_path(str(resolved))
                if key in discovered_keys:
                    continue
                discovered_keys.add(key)
                signal = get_v4_dir_signal(resolved)
                signal["key"] = key
                discovered.append(signal)

    has_local_drive_candidate = any(item.get("is_local_drive") for item in discovered)
    if has_local_drive_candidate:
        discovered = [item for item in discovered if item.get("is_local_drive")]

    discovered.sort(
        key=lambda item: (
            1 if item.get("is_local_drive") else 0,
            0 if item.get("is_unc") else 1,
            item.get("score", 0.0),
            item.get("message_mtime", 0.0),
            item.get("session_mtime", 0.0),
        ),
        reverse=True,
    )
    return {
        "candidates": discovered,
        "configured_roots": [
            normalize_windows_path(str(root)) for root in collect_v4_data_roots_from_wechat_ini()
        ],
        "search_roots": [
            normalize_windows_path(str(root)) for root in unique_roots
        ],
        "drive_roots": [
            normalize_windows_path(str(root)) for root in drive_roots
        ],
    }


def detect_v4_data_dir_from_open_files(proc) -> str:
    try:
        for opened in proc.open_files() or []:
            path = normalize_windows_path(opened.path or "")
            if is_unc_path(path):
                continue
            lowered = path.lower()
            if (
                ("db_storage\\session\\" in lowered or "db_storage\\message\\" in lowered)
                and lowered.endswith(".db")
            ):
                candidate = Path(opened.path).resolve().parents[2]
                if is_valid_v4_data_dir(candidate):
                    return str(candidate)
        return ""
    except Exception:
        return ""


def detect_v4_unc_data_dir_from_open_files(proc) -> str:
    try:
        for opened in proc.open_files() or []:
            path = normalize_windows_path(opened.path or "")
            if not is_unc_path(path):
                continue
            lowered = path.lower()
            if (
                ("db_storage\\session\\" in lowered or "db_storage\\message\\" in lowered)
                and lowered.endswith(".db")
            ):
                return str(Path(opened.path).resolve().parents[2])
        return ""
    except Exception:
        return ""


def ensure_v4_validator_db(data_dir: str):
    message_dir = Path(data_dir) / "db_storage" / "message"
    canonical_db = message_dir / "message_0.db"
    if canonical_db.exists():
        return

    biz_db = message_dir / "biz_message_0.db"
    if not biz_db.exists():
        return

    try:
        canonical_db.hardlink_to(biz_db)
        log_debug(f"v4 兼容处理: 已为 chatlog 创建硬链接 {canonical_db} -> {biz_db}")
        return
    except Exception:
        pass

    try:
        canonical_db.write_bytes(biz_db.read_bytes())
        log_debug(f"v4 兼容处理: 已为 chatlog 复制 message_0.db <- {biz_db}")
    except Exception as exc:
        log_debug(f"v4 兼容处理失败: 无法生成 message_0.db: {exc}")


def sync_v4_validator_db_to_unc(local_data_dir: str, unc_data_dir: str):
    if not local_data_dir or not unc_data_dir:
        return

    local_dir = Path(local_data_dir)
    unc_dir = Path(unc_data_dir)
    local_session_db, local_message_db = get_v4_db_markers(local_dir)
    if local_message_db is None:
        return

    try:
        unc_message_dir = unc_dir / "db_storage" / "message"
        unc_message_dir.mkdir(parents=True, exist_ok=True)
        unc_target_db = unc_message_dir / "message_0.db"

        if not unc_target_db.exists():
            unc_target_db.write_bytes(local_message_db.read_bytes())
            log_debug(
                f"v4 兼容处理: 已同步本地消息库到共享目录 {unc_target_db}"
            )

        if local_session_db is not None:
            unc_session_dir = unc_dir / "db_storage" / "session"
            unc_session_dir.mkdir(parents=True, exist_ok=True)
            unc_session_db = unc_session_dir / "session.db"
            if not unc_session_db.exists():
                unc_session_db.write_bytes(local_session_db.read_bytes())
                log_debug(
                    f"v4 兼容处理: 已同步本地 session.db 到共享目录 {unc_session_db}"
                )
    except Exception as exc:
        log_debug(f"v4 兼容处理失败: 无法同步共享目录校验库: {exc}")


def detect_v4_instance():
    try:
        import psutil
    except ImportError:
        return None

    fallback = None
    for proc in psutil.process_iter(["pid", "name", "cmdline"]):
        try:
            name = (proc.info.get("name") or "").strip()
            if name.lower() != "weixin.exe":
                continue

            cmdline = " ".join(proc.info.get("cmdline") or [])
            if "--" in cmdline:
                continue

            data_dir = detect_v4_data_dir_from_open_files(proc)
            unc_data_dir = detect_v4_unc_data_dir_from_open_files(proc)
            data_dir_candidates = []
            data_dir_source = ""
            scan_context = {
                "configured_roots": [],
                "search_roots": [],
                "drive_roots": [],
            }
            if data_dir:
                log_debug(f"Weixin.exe pid={proc.info.get('pid')} 通过 open_files 命中 data_dir: {data_dir}")
                data_dir_candidates.append(data_dir)
                data_dir_source = "open_files"
            if not data_dir:
                scan_result = collect_v4_data_dir_candidates()
                candidates = scan_result.get("candidates") or []
                scan_context = {
                    "configured_roots": scan_result.get("configured_roots") or [],
                    "search_roots": scan_result.get("search_roots") or [],
                    "drive_roots": scan_result.get("drive_roots") or [],
                }
                if candidates:
                    preview = ", ".join(item.get("path", "") for item in candidates[:3])
                    log_debug(
                        f"Weixin.exe pid={proc.info.get('pid')} open_files 未命中，目录扫描找到 {len(candidates)} 个候选，优先使用: {preview}"
                    )
                    data_dir_candidates = [str(item.get("path") or "") for item in candidates if item.get("path")]
                    data_dir = data_dir_candidates[0] if data_dir_candidates else ""
                    data_dir_source = "scan_candidates"
                else:
                    log_debug(
                        f"Weixin.exe pid={proc.info.get('pid')} open_files 未命中，目录扫描也没有找到可用的 v4 data_dir"
                    )
                    data_dir_source = "scan_not_found"

            candidate = {
                "pid": int(proc.info.get("pid") or 0),
                "name": name,
                "data_dir": data_dir,
                "data_dir_candidates": data_dir_candidates,
                "data_dir_source": data_dir_source,
                "unc_data_dir": unc_data_dir,
                "scan_context": scan_context,
            }
            if data_dir:
                return candidate
            if fallback is None:
                fallback = candidate
        except Exception:
            continue

    return fallback


def run_command(args, timeout=180, env_overrides: dict | None = None):
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    env = os.environ.copy()
    if env_overrides:
        for key, value in env_overrides.items():
            if value is None:
                continue
            env[str(key)] = str(value)
    return subprocess.run(
        args,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="ignore",
        check=False,
        timeout=timeout,
        creationflags=creationflags,
        env=env,
    )
def find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def http_get_json(url: str):
    req = urllib.request.Request(url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=15) as resp:
        return json.loads(resp.read().decode("utf-8"))


def wait_for_server(base_url: str, timeout_seconds: int = 20):
    last_error = ""
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        try:
            http_get_json(f"{base_url}/api/v1/session?format=json&limit=1")
            return
        except Exception as exc:
            last_error = str(exc)
            time.sleep(0.5)
    raise RuntimeError(f"chatlog HTTP 服务启动超时: {last_error}")


def image_mime_to_ext(mime: str) -> str:
    return {
        "image/jpeg": ".jpg",
        "image/png": ".png",
        "image/gif": ".gif",
        "image/bmp": ".bmp",
        "image/webp": ".webp",
    }.get(mime.lower(), ".jpg")


def fetch_image_payload(base_url: str, contents: dict):
    keys = []
    for field in ("imgfile", "thumb", "md5"):
        value = contents.get(field)
        if isinstance(value, str) and value and value not in keys:
            keys.append(value)

    for key in keys:
        url = f"{base_url}/image/{urllib.parse.quote(key, safe='')}"
        req = urllib.request.Request(url, headers={"User-Agent": "wx-monitor"})
        try:
            with urllib.request.urlopen(req, timeout=20) as resp:
                length = resp.headers.get("Content-Length")
                if length and int(length) > MAX_IMAGE_BYTES:
                    continue

                data = resp.read(MAX_IMAGE_BYTES + 1)
                if len(data) > MAX_IMAGE_BYTES:
                    continue

                mime = resp.headers.get_content_type() or "image/jpeg"
                return {
                    "mime": mime,
                    "name": f"chatlog_image{image_mime_to_ext(mime)}",
                    "data": data,
                }
        except Exception:
            continue
    return None


def parse_message_time(value) -> int:
    if isinstance(value, (int, float)):
        return int(value)
    if not isinstance(value, str) or not value:
        return 0

    try:
        return int(datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp())
    except Exception:
        return 0


def fallback_message_content(msg_type: int, content: str) -> str:
    if content:
        return content[:500]
    return {
        3: "[图片]",
        34: "[语音]",
        43: "[视频]",
        49: "[文件/链接]",
        10000: "[系统消息]",
    }.get(msg_type, "")


def normalize_chatlog_messages(base_url: str, messages: list[dict]):
    normalized = []
    seen = set()

    for item in messages:
        if not isinstance(item, dict):
            continue

        talker = str(item.get("talker") or "")
        seq = str(item.get("seq") or "")
        dedupe_key = (talker, seq)
        if dedupe_key in seen:
            continue
        seen.add(dedupe_key)

        msg_type = int(item.get("type") or 0)
        msg_sub_type = int(item.get("subType") or 0)
        content = str(item.get("content") or "")
        is_sender = bool(item.get("isSelf"))
        talker_name = str(item.get("talkerName") or talker)
        sender_name = str(item.get("senderName") or item.get("sender") or talker_name)
        create_time = parse_message_time(item.get("time"))
        contents = item.get("contents") if isinstance(item.get("contents"), dict) else {}

        record = {
            "wxid": talker,
            "content": fallback_message_content(msg_type, content),
            "create_time": create_time,
            "is_sender": is_sender,
            "nickname": talker_name,
            "sender": "我" if is_sender else sender_name,
            "msg_type": msg_type,
            "msg_sub_type": msg_sub_type,
            "media_type": "",
            "media_mime": "",
            "media_name": "",
            "media_data": "",
        }

        if msg_type == 3:
            media = fetch_image_payload(base_url, contents)
            if media:
                record["media_type"] = "image"
                record["media_mime"] = media["mime"]
                record["media_name"] = media["name"]
                record["media_data"] = b64encode(media["data"]).decode("ascii")

        normalized.append(record)

    normalized.sort(key=lambda item: item["create_time"])
    if len(normalized) > MAX_MESSAGES:
        normalized = normalized[-MAX_MESSAGES:]
    return normalized


def export_v4_messages(
    data_dir: str,
    decrypt_dir: Path,
    pid: int = 0,
    unc_data_dir: str = "",
    data_dir_candidates: list[str] | None = None,
    data_dir_source: str = "",
    scan_context: dict | None = None,
):
    candidate_dirs = []
    for candidate in (data_dir_candidates or []):
        normalized = normalize_windows_path(candidate)
        if normalized and normalized not in [normalize_windows_path(item) for item in candidate_dirs]:
            candidate_dirs.append(candidate)
    if data_dir and normalize_windows_path(data_dir) not in [normalize_windows_path(item) for item in candidate_dirs]:
        candidate_dirs.insert(0, data_dir)

    scan_context = scan_context or {}
    configured_roots = scan_context.get("configured_roots") or []
    search_roots = scan_context.get("search_roots") or []
    drive_roots = scan_context.get("drive_roots") or []

    if not candidate_dirs:
        emit_runtime_event(
            "client_v4_data_dir_result",
            {
                "success": False,
                "reason": "data_dir_missing",
                "pid": pid,
                "source": data_dir_source or "scan_not_found",
                "configured_root_count": len(configured_roots),
                "configured_roots": configured_roots[:20],
                "search_root_count": len(search_roots),
                "search_roots": search_roots[:20],
                "drive_root_count": len(drive_roots),
                "drive_roots": drive_roots[:20],
            },
        )
        raise RuntimeError("已检测到 Weixin.exe，但没有读取到聊天数据目录（data_dir）")

    emit_runtime_event(
        "client_v4_data_dir_result",
        {
            "success": True,
            "pid": pid,
            "data_dir": candidate_dirs[0],
            "source": data_dir_source or "scan_candidates",
            "candidate_count": len(candidate_dirs),
            "candidate_dirs": candidate_dirs[:20],
            "configured_root_count": len(configured_roots),
            "configured_roots": configured_roots[:20],
            "search_root_count": len(search_roots),
            "search_roots": search_roots[:20],
            "drive_root_count": len(drive_roots),
            "drive_roots": drive_roots[:20],
        },
    )

    selected_data_dir = candidate_dirs[0]
    selected_wxid = Path(selected_data_dir).name if selected_data_dir else ""
    selected_db_storage_dir = str(Path(selected_data_dir) / "db_storage")
    if not Path(selected_db_storage_dir).is_dir():
        selected_db_storage_dir = selected_data_dir
    server_url, server_token = get_runtime_server_config()

    export_path = decrypt_dir / EXPORT_JSON_NAME
    contact_export_path = decrypt_dir / CONTACT_EXPORT_JSON_NAME
    favorite_export_path = decrypt_dir / FAVORITE_EXPORT_JSON_NAME
    stale_dir_names = ("session", "message", "contact", "Contact")
    for stale_dir_name in stale_dir_names:
        stale_dir = decrypt_dir / stale_dir_name
        if stale_dir.exists():
            shutil.rmtree(stale_dir, ignore_errors=True)

    key_scan_result = try_get_cached_key_scan_result(
        pid=pid,
        selected_wxid=selected_wxid,
        selected_data_dir=selected_data_dir,
        selected_db_storage_dir=selected_db_storage_dir,
    )
    key_source = "memory_cache" if key_scan_result else "wechat_decrypt_memory_scan"

    if key_scan_result:
        emit_runtime_event(
            "client_chatlog_key_attempt",
            {
                "pid": pid,
                "helper_used": False,
                "source": "memory_cache",
                "attempt_index": 1,
                "attempt_total": 2,
                "candidate_count": len(candidate_dirs),
                "data_dir": selected_data_dir,
                "db_storage_dir": selected_db_storage_dir,
                "cache_hit": True,
            },
        )
        emit_runtime_event(
            "client_chatlog_key_result",
            {
                "success": True,
                "pid": pid,
                "helper_used": False,
                "source": "memory_cache",
                "has_key": True,
                "selected_data_dir": selected_data_dir,
                "db_storage_dir": selected_db_storage_dir,
                "selected_wxid": selected_wxid,
                "selection_reason": "runtime_cache_pid_match",
                "matched_db_count": int(key_scan_result.get("matched_count") or 0),
                "missing_db_count": int(key_scan_result.get("missing_count") or 0),
                "duration_seconds": 0.0,
                "cache_hit": True,
            },
        )

    if key_scan_result is None:
        emit_runtime_event(
            "client_chatlog_key_attempt",
            {
                "pid": pid,
                "helper_used": False,
                "source": "wechat_decrypt_memory_scan",
                "attempt_index": 1,
                "attempt_total": 1,
                "candidate_count": len(candidate_dirs),
                "data_dir": selected_data_dir,
                "db_storage_dir": selected_db_storage_dir,
            },
        )

        try:
            key_scan_result = extract_database_keys_windows(
                selected_db_storage_dir,
                preferred_pid=pid,
                log_fn=log_debug,
            )
        except Exception as exc:
            emit_runtime_event(
                "client_chatlog_key_result",
                {
                    "success": False,
                    "pid": pid,
                    "helper_used": False,
                    "source": "wechat_decrypt_memory_scan",
                    "has_key": False,
                    "attempted_dirs": candidate_dirs[:5],
                    "db_storage_dir": selected_db_storage_dir,
                    "error_message": str(exc),
                },
            )
            raise RuntimeError(f"内存扫描数据库密钥失败: {exc}") from exc

        emit_runtime_event(
            "client_chatlog_key_result",
            {
                "success": True,
                "pid": pid,
                "helper_used": False,
                "source": "wechat_decrypt_memory_scan",
                "has_key": True,
                "selected_data_dir": selected_data_dir,
                "db_storage_dir": selected_db_storage_dir,
                "selected_wxid": selected_wxid,
                "selection_reason": "mtime_priority_candidate",
                "matched_db_count": int(key_scan_result.get("matched_count") or 0),
                "missing_db_count": int(key_scan_result.get("missing_count") or 0),
                "duration_seconds": float(key_scan_result.get("duration_seconds") or 0),
            },
        )
        emit_db_key_cache(
            build_db_key_cache_payload(
                pid=pid,
                wxid=selected_wxid,
                data_dir=selected_data_dir,
                db_storage_dir=selected_db_storage_dir,
                key_scan_result=key_scan_result,
            )
        )

    if server_url and server_token:
        emit_runtime_event(
            "client_disk_pipeline_started",
            {
                "data_dir": selected_data_dir,
                "db_storage_dir": selected_db_storage_dir,
                "message_db_count": int(key_scan_result.get("matched_count") or 0),
                "mode": "ephemeral_disk_v4",
                "will_cleanup_after_upload": True,
            },
        )

    decrypt_result = decrypt_database_tree(
        selected_db_storage_dir,
        str(decrypt_dir),
        key_scan_result.get("keys") or {},
        log_fn=log_debug,
        event_fn=emit_runtime_event,
        wechat_running=bool(detect_wechat_processes()),
    )
    if key_source == "memory_cache" and should_fallback_from_cached_decrypt(decrypt_dir, decrypt_result):
        fallback_reason = "缓存密钥解密结果不完整，回退到内存扫描"
        emit_runtime_event(
            "client_chatlog_key_result",
            {
                "success": False,
                "pid": pid,
                "helper_used": False,
                "source": "memory_cache",
                "has_key": False,
                "db_storage_dir": selected_db_storage_dir,
                "cache_hit": True,
                "cache_fallback": True,
                "error_message": fallback_reason,
            },
        )
        emit_runtime_event(
            "client_chatlog_key_attempt",
            {
                "pid": pid,
                "helper_used": False,
                "source": "wechat_decrypt_memory_scan",
                "attempt_index": 2,
                "attempt_total": 2,
                "candidate_count": len(candidate_dirs),
                "data_dir": selected_data_dir,
                "db_storage_dir": selected_db_storage_dir,
                "fallback_after_cache": True,
            },
        )
        try:
            key_scan_result = extract_database_keys_windows(
                selected_db_storage_dir,
                preferred_pid=pid,
                log_fn=log_debug,
            )
        except Exception as exc:
            emit_runtime_event(
                "client_chatlog_key_result",
                {
                    "success": False,
                    "pid": pid,
                    "helper_used": False,
                    "source": "wechat_decrypt_memory_scan",
                    "has_key": False,
                    "attempted_dirs": candidate_dirs[:5],
                    "db_storage_dir": selected_db_storage_dir,
                    "error_message": str(exc),
                    "fallback_after_cache": True,
                },
            )
            raise RuntimeError(f"缓存密钥失效，内存扫描数据库密钥失败: {exc}") from exc

        emit_runtime_event(
            "client_chatlog_key_result",
            {
                "success": True,
                "pid": pid,
                "helper_used": False,
                "source": "wechat_decrypt_memory_scan",
                "has_key": True,
                "selected_data_dir": selected_data_dir,
                "db_storage_dir": selected_db_storage_dir,
                "selected_wxid": selected_wxid,
                "selection_reason": "cache_fallback_rescan",
                "matched_db_count": int(key_scan_result.get("matched_count") or 0),
                "missing_db_count": int(key_scan_result.get("missing_count") or 0),
                "duration_seconds": float(key_scan_result.get("duration_seconds") or 0),
                "fallback_after_cache": True,
            },
        )
        emit_db_key_cache(
            build_db_key_cache_payload(
                pid=pid,
                wxid=selected_wxid,
                data_dir=selected_data_dir,
                db_storage_dir=selected_db_storage_dir,
                key_scan_result=key_scan_result,
            )
        )
        decrypt_result = decrypt_database_tree(
            selected_db_storage_dir,
            str(decrypt_dir),
            key_scan_result.get("keys") or {},
            log_fn=log_debug,
            event_fn=emit_runtime_event,
            wechat_running=bool(detect_wechat_processes()),
        )
        key_source = "wechat_decrypt_memory_scan"
    if not decrypt_result.get("success"):
        if server_url and server_token:
            emit_runtime_event(
                "client_disk_pipeline_result",
                {
                    "success": False,
                    "result": "decrypt_failed",
                    "message_count": 0,
                    "decrypted_db_count": int(decrypt_result.get("success_count") or 0),
                    "failed_db_count": int(decrypt_result.get("failed_count") or 0),
                    "duration_seconds": float(decrypt_result.get("duration_seconds") or 0),
                    "mode": "ephemeral_disk_v4",
                    "will_cleanup_after_upload": True,
                    "error_message": "wechat-decrypt 解密数据库失败",
                },
            )
        raise RuntimeError("wechat-decrypt 解密数据库失败")

    try:
        exported_messages = export_chatlog_json(
            str(decrypt_dir),
            str(export_path),
            max_messages=MAX_MESSAGES,
            source_data_dir=selected_data_dir,
            preferred_pid=pid,
            log_fn=log_debug,
            event_fn=emit_runtime_event,
        )
    except Exception as exc:
        if key_source == "memory_cache" and is_malformed_sqlite_error(exc):
            fallback_reason = "缓存密钥导出消息时命中 malformed，回退到内存扫描"
            emit_runtime_event(
                "client_chatlog_key_result",
                {
                    "success": False,
                    "pid": pid,
                    "helper_used": False,
                    "source": "memory_cache",
                    "has_key": False,
                    "db_storage_dir": selected_db_storage_dir,
                    "cache_hit": True,
                    "cache_fallback": True,
                    "fallback_stage": "chatlog_export",
                    "error_message": f"{fallback_reason}: {exc}",
                },
            )
            emit_runtime_event(
                "client_chatlog_key_attempt",
                {
                    "pid": pid,
                    "helper_used": False,
                    "source": "wechat_decrypt_memory_scan",
                    "attempt_index": 2,
                    "attempt_total": 2,
                    "candidate_count": len(candidate_dirs),
                    "data_dir": selected_data_dir,
                    "db_storage_dir": selected_db_storage_dir,
                    "fallback_after_cache": True,
                    "fallback_stage": "chatlog_export",
                },
            )
            try:
                key_scan_result = extract_database_keys_windows(
                    selected_db_storage_dir,
                    preferred_pid=pid,
                    log_fn=log_debug,
                )
            except Exception as scan_exc:
                emit_runtime_event(
                    "client_chatlog_key_result",
                    {
                        "success": False,
                        "pid": pid,
                        "helper_used": False,
                        "source": "wechat_decrypt_memory_scan",
                        "has_key": False,
                        "attempted_dirs": candidate_dirs[:5],
                        "db_storage_dir": selected_db_storage_dir,
                        "error_message": str(scan_exc),
                        "fallback_after_cache": True,
                        "fallback_stage": "chatlog_export",
                    },
                )
                raise RuntimeError(f"缓存密钥导出失败，重新扫描数据库密钥仍失败: {scan_exc}") from scan_exc

            emit_runtime_event(
                "client_chatlog_key_result",
                {
                    "success": True,
                    "pid": pid,
                    "helper_used": False,
                    "source": "wechat_decrypt_memory_scan",
                    "has_key": True,
                    "selected_data_dir": selected_data_dir,
                    "db_storage_dir": selected_db_storage_dir,
                    "selected_wxid": selected_wxid,
                    "selection_reason": "cache_fallback_export_rescan",
                    "matched_db_count": int(key_scan_result.get("matched_count") or 0),
                    "missing_db_count": int(key_scan_result.get("missing_count") or 0),
                    "duration_seconds": float(key_scan_result.get("duration_seconds") or 0),
                    "fallback_after_cache": True,
                    "fallback_stage": "chatlog_export",
                },
            )
            emit_db_key_cache(
                build_db_key_cache_payload(
                    pid=pid,
                    wxid=selected_wxid,
                    data_dir=selected_data_dir,
                    db_storage_dir=selected_db_storage_dir,
                    key_scan_result=key_scan_result,
                )
            )
            for stale_dir_name in stale_dir_names:
                stale_dir = decrypt_dir / stale_dir_name
                if stale_dir.exists():
                    shutil.rmtree(stale_dir, ignore_errors=True)
            decrypt_result = decrypt_database_tree(
                selected_db_storage_dir,
                str(decrypt_dir),
                key_scan_result.get("keys") or {},
                log_fn=log_debug,
                event_fn=emit_runtime_event,
                wechat_running=bool(detect_wechat_processes()),
            )
            key_source = "wechat_decrypt_memory_scan"
            if not decrypt_result.get("success"):
                raise RuntimeError("缓存密钥导出失败，重新扫描后再次解密仍失败") from exc

            exported_messages = export_chatlog_json(
                str(decrypt_dir),
                str(export_path),
                max_messages=MAX_MESSAGES,
                source_data_dir=selected_data_dir,
                preferred_pid=pid,
                log_fn=log_debug,
                event_fn=emit_runtime_event,
            )
        else:
            raise
    exported_contacts = export_contacts_json(
        str(decrypt_dir),
        str(contact_export_path),
        log_fn=log_debug,
        event_fn=emit_runtime_event,
    )
    emit_runtime_event(
        "client_contacts_export_result",
        {
            "contact_count": len(exported_contacts),
            "avatar_count": sum(1 for item in exported_contacts if item.get("avatar")),
        },
    )
    exported_favorites = export_favorites_json(
        str(decrypt_dir),
        str(favorite_export_path),
        log_fn=log_debug,
        event_fn=emit_runtime_event,
    )

    if not export_path.exists():
        raise RuntimeError("wechat-decrypt 返回成功，但没有生成 chatlog_export.json")

    if len(exported_messages) > MAX_MESSAGES:
        exported_messages = exported_messages[-MAX_MESSAGES:]
        export_path.write_text(
            json.dumps(exported_messages, ensure_ascii=False),
            encoding="utf-8",
        )

    if not selected_wxid:
        selected_wxid = Path(selected_data_dir).name if selected_data_dir else ""

    write_meta(
        decrypt_dir,
        selected_data_dir,
        "chatlog_v4",
        wxid=selected_wxid,
    )

    if server_url and server_token:
        emit_runtime_event(
            "client_disk_pipeline_result",
            {
                "success": True,
                "result": "exported",
                "message_count": len(exported_messages),
                "decrypted_db_count": int(decrypt_result.get("success_count") or 0),
                "failed_db_count": int(decrypt_result.get("failed_count") or 0),
                "duration_seconds": float(decrypt_result.get("duration_seconds") or 0),
                "mode": "ephemeral_disk_v4",
                "will_cleanup_after_upload": True,
            },
        )

    emit_runtime_event(
        "client_wechat_decrypt_export_result",
        {
            "success": True,
            "pid": pid,
            "data_dir": selected_data_dir,
            "db_storage_dir": selected_db_storage_dir,
            "wxid": selected_wxid,
            "message_count": len(exported_messages),
            "matched_db_count": int(key_scan_result.get("matched_count") or 0),
            "decrypted_db_count": int(decrypt_result.get("success_count") or 0),
            "missing_db_count": int(key_scan_result.get("missing_count") or 0),
            "mode": "ephemeral_disk_v4" if (server_url and server_token) else "wechat_decrypt_sqlcipher4",
        },
    )


def main():
    if len(sys.argv) > 1:
        decrypt_dir = Path(sys.argv[1])
    else:
        decrypt_dir = Path("./wechat_data/decrypted")

    decrypt_dir.mkdir(parents=True, exist_ok=True)
    reset_output_artifacts(decrypt_dir)

    try:
        v4_instance = detect_v4_instance()
        if v4_instance:
            emit_login_status(True)
            export_v4_messages(
                v4_instance.get("data_dir") or "",
                decrypt_dir,
                int(v4_instance.get("pid") or 0),
                v4_instance.get("unc_data_dir") or "",
                v4_instance.get("data_dir_candidates") or [],
                v4_instance.get("data_dir_source") or "",
                v4_instance.get("scan_context") or {},
            )
            return

        running = detect_wechat_processes()
        emit_login_status(bool(running))
        if running:
            emit_extract_failed(
                f"检测到微信进程 {', '.join(running)}，但当前桌宠仅支持新版 Weixin 4.x 自动链路",
                running_processes=running,
            )
        else:
            emit_extract_failed("未检测到已登录的新版 Weixin 4.x")
        sys.exit(1)
    except ImportError as exc:
        emit_login_status(False)
        emit_extract_failed(
            f"缺少依赖: {exc}",
            exception_type=type(exc).__name__,
        )
        sys.exit(1)
    except Exception as exc:
        running = detect_wechat_processes()
        emit_login_status(bool(running))
        emit_extract_failed(
            f"解密失败: {exc}",
            logged_in=bool(running),
            exception_type=type(exc).__name__,
        )
        sys.exit(1)


if __name__ == "__main__":
    main()
