"""
微信聊天记录提取脚本 - 运行在你的 Windows 电脑上
功能: 定时解密微信数据库，提取新消息，推送到服务器
"""
import os
import json
import time
import hashlib
import uuid
import csv
import io
import subprocess
import requests
import schedule
from pathlib import Path
from datetime import datetime

# ============ 配置区 ============
SERVER_URL = "https://wx.junjiee.online/api/messages"  # 后端接收地址
SERVER_TOKEN = "wx_monitor_2026"  # 改成一个随机字符串，两端要一致
PUSH_INTERVAL = 60  # 每隔多少秒检查一次新消息
# ================================

# PyWxDump 提取的消息存储路径
EXTRACT_DIR = Path("./wechat_data")
LAST_HASH_FILE = Path("./last_hash.txt")
EVENTS_URL = SERVER_URL.replace("/api/messages", "/api/events")
STATUS_URL = SERVER_URL.replace("/api/messages", "/api/status")
CLIENT_SESSION_ID = f"client-py-{uuid.uuid4().hex[:12]}"


def post_event(event_name, payload=None):
    """上报客户端事件"""
    try:
        requests.post(
            EVENTS_URL,
            json={
                "token": SERVER_TOKEN,
                "source": "client_py",
                "session_id": CLIENT_SESSION_ID,
                "event_name": event_name,
                "payload": payload or {},
            },
            timeout=5,
        )
    except Exception:
        pass


def post_status(wechat_logged_in, decrypt_ok=None, error=None):
    """上报运行状态"""
    body = {
        "token": SERVER_TOKEN,
        "wechat_logged_in": bool(wechat_logged_in),
    }
    if decrypt_ok is not None:
        body["decrypt_ok"] = bool(decrypt_ok)
    if error:
        body["error"] = str(error)

    try:
        requests.post(STATUS_URL, json=body, timeout=5)
    except Exception:
        pass


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


def get_wechat_data():
    """使用 PyWxDump 提取微信聊天记录"""
    is_logged_in = False
    try:
        from pywxdump import WX_OFFS, decrypt, get_core_db, get_wx_info

        infos = get_wx_info(WX_OFFS)
        if not infos:
            running = detect_wechat_processes()
            if "Weixin.exe" in running:
                reason = "检测到 Weixin.exe 正在运行，但当前解密链路可能暂不兼容该版本微信"
            elif "WeChat.exe" in running:
                reason = "检测到 WeChat.exe 正在运行，但未获取到登录信息，可能是权限不足或版本兼容问题"
            else:
                reason = "微信未登录或未运行"
            print(f"[!] {reason}")
            post_status(bool(running), decrypt_ok=False, error=reason)
            post_event("client_wechat_login_status", {"logged_in": bool(running)})
            post_event("client_extract_failed", {"stage": "login_check", "reason": reason})
            return None

        is_logged_in = True
        post_status(True)
        post_event("client_wechat_login_status", {"logged_in": True})

        info = next(
            (
                item for item in infos
                if isinstance(item, dict)
                and item.get("key")
                and item.get("wx_dir")
            ),
            None,
        )
        if not info:
            running = detect_wechat_processes()
            reason = (
                f"检测到微信进程 {', '.join(running)}，但未获取到微信密钥或目录"
                if running
                else "未获取到微信密钥或目录"
            )
            print(f"[!] {reason}")
            post_status(bool(running), decrypt_ok=False, error=reason)
            post_event("client_wechat_login_status", {"logged_in": bool(running)})
            post_event("client_extract_failed", {"stage": "wechat_info", "reason": reason})
            return None

        post_status(True)
        post_event("client_wechat_login_status", {"logged_in": True})

        wx_dir = str(info["wx_dir"])
        key = str(info["key"])
        core_db_result = get_core_db(wx_dir, ["MSG"])
        if not isinstance(core_db_result, tuple) or len(core_db_result) != 2:
            print(f"[!] 获取数据库路径失败: {core_db_result}")
            post_status(True, decrypt_ok=False, error="core_db_lookup_failed")
            post_event("client_extract_failed", {"stage": "core_db_lookup", "reason": "core_db_lookup_failed"})
            return None

        ok, db_entries = core_db_result
        if not ok or not db_entries:
            print("[!] 未获取到 MSG 数据库路径")
            post_status(True, decrypt_ok=False, error="msg_db_missing")
            post_event("client_extract_failed", {"stage": "core_db_lookup", "reason": "msg_db_missing"})
            return None

        # 解密数据库
        decrypt_dir = EXTRACT_DIR / "decrypted"
        decrypt_dir.mkdir(parents=True, exist_ok=True)

        success_count = 0
        for entry in db_entries:
            db_path = Path(entry["db_path"])
            out_path = decrypt_dir / db_path.name
            ok, result = decrypt(key, str(db_path), str(out_path))
            if ok:
                success_count += 1
            else:
                print(f"[!] 解密失败: {db_path.name}: {result}")

        if success_count == 0:
            post_status(True, decrypt_ok=False, error="decrypt_failed")
            post_event("client_extract_failed", {"stage": "decrypt", "reason": "decrypt_failed"})
            return None

        post_status(True, decrypt_ok=True)
        post_event("client_decrypt_finished", {"decrypt_dir": str(decrypt_dir), "db_dir": wx_dir})

        return decrypt_dir

    except ImportError:
        print("[!] 请先安装 pywxdump: pip install pywxdump")
        post_status(False, decrypt_ok=False, error="pywxdump_missing")
        post_event("client_extract_failed", {"stage": "import", "reason": "pywxdump_missing"})
        return None
    except Exception as e:
        print(f"[!] 提取失败: {e}")
        post_status(is_logged_in, decrypt_ok=False, error=str(e))
        post_event("client_extract_failed", {"stage": "extract", "error_message": str(e), "logged_in": is_logged_in})
        return None


def extract_messages(db_dir):
    """从解密后的数据库提取消息"""
    try:
        import sqlite3

        messages = []
        for db_file in db_dir.glob("MSG*.db"):
            conn = sqlite3.connect(str(db_file))
            cursor = conn.cursor()

            # 查询最近的消息
            cursor.execute("""
                SELECT
                    StrTalker as wxid,
                    StrContent as content,
                    CreateTime as create_time,
                    IsSender as is_sender
                FROM MSG
                ORDER BY CreateTime DESC
                LIMIT 800
            """)

            for row in cursor.fetchall():
                wxid, content, create_time, is_sender = row
                if content and len(content.strip()) > 0:
                    messages.append({
                        "wxid": wxid,
                        "content": content[:500],  # 限制长度
                        "create_time": create_time,
                        "is_sender": bool(is_sender),
                        "nickname": wxid,
                        "sender": "我" if is_sender else wxid,
                    })

            conn.close()

        messages.sort(key=lambda x: x["create_time"], reverse=True)
        return sorted(messages[:800], key=lambda x: x["create_time"])

    except Exception as e:
        print(f"[!] 数据库读取失败: {e}")
        post_status(True, decrypt_ok=False, error=str(e))
        post_event("client_extract_failed", {"stage": "sqlite_read", "error_message": str(e)})
        return []


def get_content_hash(messages):
    """计算消息内容的hash，用于检测是否有新消息"""
    content = json.dumps(
        [(m["wxid"], m["create_time"], m["content"]) for m in messages[-50:]],
        ensure_ascii=False
    )
    return hashlib.md5(content.encode()).hexdigest()


def push_to_server(messages):
    """推送到服务器"""
    started_at = time.time()
    post_event("client_push_started", {"message_count": len(messages)})
    try:
        resp = requests.post(
            SERVER_URL,
            json={"messages": messages, "token": SERVER_TOKEN},
            timeout=10
        )
        if resp.status_code == 200:
            result = resp.json()
            print(f"[OK] 推送成功，共 {len(messages)} 条消息")
            post_event(
                "client_push_result",
                {
                    "success": True,
                    "status_code": resp.status_code,
                    "message_count": len(messages),
                    "added_count": result.get("added", 0),
                    "duration_ms": int((time.time() - started_at) * 1000),
                },
            )
        else:
            print(f"[!] 推送失败: {resp.status_code} {resp.text}")
            post_event(
                "client_push_failed",
                {
                    "status_code": resp.status_code,
                    "response_text": resp.text[:300],
                    "message_count": len(messages),
                    "duration_ms": int((time.time() - started_at) * 1000),
                },
            )
    except Exception as e:
        print(f"[!] 网络错误: {e}")
        post_event(
            "client_push_failed",
            {
                "status_code": 0,
                "error_message": str(e),
                "message_count": len(messages),
                "duration_ms": int((time.time() - started_at) * 1000),
            },
        )


def check_and_push():
    """主任务: 检查新消息并推送"""
    print(f"\n[{datetime.now().strftime('%H:%M:%S')}] 检查新消息...")
    scan_started = time.time()
    post_event("client_scan_started", {"interval_seconds": PUSH_INTERVAL})

    db_dir = get_wechat_data()
    if not db_dir:
        post_event("client_scan_finished", {"duration_ms": int((time.time() - scan_started) * 1000), "result": "no_db_dir"})
        return

    messages = extract_messages(db_dir)
    if not messages:
        print("[*] 没有找到消息")
        post_event("client_scan_finished", {"duration_ms": int((time.time() - scan_started) * 1000), "result": "no_messages", "message_count": 0})
        return

    # 检查是否有新消息
    current_hash = get_content_hash(messages)
    last_hash = ""
    if LAST_HASH_FILE.exists():
        last_hash = LAST_HASH_FILE.read_text().strip()

    if current_hash != last_hash:
        print(f"[*] 发现新消息，准备推送...")
        push_to_server(messages)
        LAST_HASH_FILE.write_text(current_hash)
        post_event(
            "client_scan_finished",
            {
                "duration_ms": int((time.time() - scan_started) * 1000),
                "result": "pushed",
                "message_count": len(messages),
                "has_new_messages": True,
            },
        )
    else:
        print("[*] 没有新消息")
        post_event(
            "client_scan_finished",
            {
                "duration_ms": int((time.time() - scan_started) * 1000),
                "result": "no_new_messages",
                "message_count": len(messages),
                "has_new_messages": False,
            },
        )


def main():
    print("=" * 50)
    print("  微信聊天记录监控 - 客户端")
    print("=" * 50)
    print(f"  服务器: {SERVER_URL}")
    print(f"  检查间隔: {PUSH_INTERVAL}秒")
    print(f"  按 Ctrl+C 停止")
    print("=" * 50)

    # 立即执行一次
    check_and_push()

    # 定时执行
    schedule.every(PUSH_INTERVAL).seconds.do(check_and_push)

    while True:
        schedule.run_pending()
        time.sleep(1)


if __name__ == "__main__":
    main()
