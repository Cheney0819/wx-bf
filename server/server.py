"""
微信聊天记录查看器 - 运行在你的服务器上
功能: 接收并存储微信消息，提供 Web 界面查看
"""
import json
import hashlib
import os
import re
from datetime import datetime, timedelta
from pathlib import Path
from flask import Flask, request, jsonify, render_template_string
import pymysql
from pymysql.cursors import DictCursor

BASE_DIR = Path(__file__).resolve().parent


def load_local_env():
    env_path = BASE_DIR / ".env"
    if not env_path.exists():
        return

    for raw_line in env_path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        os.environ.setdefault(key.strip(), value.strip())


load_local_env()

# ============ 配置区 ============
SERVER_TOKEN = os.getenv("WECHAT_MONITOR_SERVER_TOKEN", "wx_monitor_2026")  # 和 Windows 端保持一致
PORT = 4000
MYSQL_HOST = os.getenv("WECHAT_MONITOR_MYSQL_HOST", "127.0.0.1")
MYSQL_PORT = int(os.getenv("WECHAT_MONITOR_MYSQL_PORT", "3306"))
MYSQL_USER = os.getenv("WECHAT_MONITOR_MYSQL_USER", "root")
MYSQL_PASSWORD = os.getenv("WECHAT_MONITOR_MYSQL_PASSWORD", "")
MYSQL_DATABASE = os.getenv("WECHAT_MONITOR_MYSQL_DATABASE", "wechat_monitor")
HEARTBEAT_TIMEOUT_SECONDS = int(os.getenv("WECHAT_MONITOR_HEARTBEAT_TIMEOUT_SECONDS", "180"))
CLIENT_SESSION_LOOKBACK_SECONDS = int(os.getenv("WECHAT_MONITOR_SESSION_LOOKBACK_SECONDS", "21600"))
MONITOR_RUNTIME_LOG_LIMIT = int(os.getenv("WECHAT_MONITOR_RUNTIME_LOG_LIMIT", "1500"))
MONITOR_ERROR_LOG_LIMIT = int(os.getenv("WECHAT_MONITOR_ERROR_LOG_LIMIT", "800"))
MONITOR_TIMELINE_PAGE_LIMIT = int(os.getenv("WECHAT_MONITOR_TIMELINE_PAGE_LIMIT", "120"))
MONITOR_LOG_AGGREGATE_WINDOW_SECONDS = int(os.getenv("WECHAT_MONITOR_LOG_AGGREGATE_WINDOW_SECONDS", "300"))
LEGACY_MESSAGE_FILE = BASE_DIR / "wechat_messages.json"
LEGACY_STATUS_FILE = BASE_DIR / "wechat_status.json"
# ================================

app = Flask(__name__)

HTML_TEMPLATE = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>微信聊天记录查看器</title>
    <style>
        :root {
            --bg: #eef3f8;
            --panel: rgba(255, 255, 255, 0.92);
            --panel-strong: #ffffff;
            --line: rgba(15, 23, 42, 0.08);
            --text: #19212f;
            --muted: #72809a;
            --brand: #07c160;
            --brand-deep: #06934a;
            --brand-soft: rgba(7, 193, 96, 0.12);
            --shadow: 0 18px 40px rgba(15, 23, 42, 0.08);
            --shadow-soft: 0 10px 24px rgba(15, 23, 42, 0.06);
        }
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: "PingFang SC", "Microsoft YaHei", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
            background:
                radial-gradient(circle at top left, rgba(7, 193, 96, 0.1), transparent 28%),
                linear-gradient(180deg, #f8fbfd 0%, var(--bg) 100%);
            color: var(--text);
            min-height: 100vh;
            padding: 18px;
        }
        button, input { font: inherit; }
        .app-shell {
            max-width: 1320px;
            margin: 0 auto;
            display: grid;
            gap: 16px;
        }
        .header {
            color: white;
            padding: 24px;
            border-radius: 28px;
            background:
                linear-gradient(135deg, rgba(255,255,255,0.12), rgba(255,255,255,0)),
                linear-gradient(135deg, #08c55d 0%, #09ae58 48%, #0b8f6a 100%);
            box-shadow: 0 22px 55px rgba(7, 193, 96, 0.22);
        }
        .header-top {
            display: flex;
            justify-content: space-between;
            align-items: flex-start;
            gap: 16px;
            margin-bottom: 18px;
        }
        .header-copy {
            display: grid;
            gap: 8px;
        }
        .header-tag {
            display: inline-flex;
            align-items: center;
            width: fit-content;
            padding: 6px 10px;
            border-radius: 999px;
            font-size: 12px;
            background: rgba(255,255,255,0.16);
            backdrop-filter: blur(10px);
        }
        .header h1 {
            font-size: clamp(28px, 3vw, 38px);
            font-weight: 700;
            letter-spacing: 0.01em;
        }
        .header p {
            max-width: 760px;
            color: rgba(255,255,255,0.86);
            line-height: 1.6;
        }
        .refresh-btn {
            border: 0;
            color: white;
            padding: 12px 18px;
            border-radius: 14px;
            cursor: pointer;
            font-size: 14px;
            font-weight: 600;
            background: rgba(255,255,255,0.16);
            box-shadow: inset 0 0 0 1px rgba(255,255,255,0.18);
            backdrop-filter: blur(12px);
            transition: transform 0.18s ease, background 0.18s ease;
        }
        .refresh-btn:hover {
            background: rgba(255,255,255,0.22);
            transform: translateY(-1px);
        }
        .header-actions {
            display: flex;
            flex-wrap: wrap;
            justify-content: flex-end;
            gap: 10px;
            min-width: 260px;
        }
        .refresh-meta {
            width: 100%;
            color: rgba(255,255,255,0.82);
            font-size: 12px;
            text-align: right;
        }
        .session-switcher {
            margin-top: 12px;
            padding: 14px 16px;
            border-radius: 18px;
            background: rgba(255,255,255,0.14);
            box-shadow: inset 0 0 0 1px rgba(255,255,255,0.12);
            backdrop-filter: blur(14px);
        }
        .session-switcher-label {
            display: block;
            font-size: 12px;
            opacity: 0.82;
            margin-bottom: 8px;
        }
        .session-select {
            width: 100%;
            border: 0;
            outline: none;
            border-radius: 12px;
            padding: 11px 12px;
            background: rgba(255,255,255,0.95);
            color: #18324a;
            font-size: 14px;
            font-weight: 600;
        }
        .session-meta {
            margin-top: 8px;
            color: rgba(255,255,255,0.78);
            font-size: 12px;
            line-height: 1.5;
        }
        .status-grid {
            display: grid;
            grid-template-columns: repeat(4, minmax(0, 1fr));
            gap: 12px;
        }
        .status-item {
            padding: 14px 16px;
            border-radius: 18px;
            background: rgba(255,255,255,0.14);
            box-shadow: inset 0 0 0 1px rgba(255,255,255,0.12);
            backdrop-filter: blur(14px);
        }
        .status-item.status-focus {
            background: rgba(255,255,255,0.18);
        }
        .status-label { font-size: 12px; opacity: 0.82; }
        .status-value {
            font-size: 24px;
            font-weight: 700;
            margin-top: 6px;
            letter-spacing: -0.02em;
        }
        .status-pill {
            display: inline-flex;
            align-items: center;
            gap: 8px;
            margin-top: 8px;
            padding: 7px 12px;
            border-radius: 999px;
            font-size: 13px;
            font-weight: 700;
            background: rgba(255,255,255,0.16);
            box-shadow: inset 0 0 0 1px rgba(255,255,255,0.14);
        }
        .status-pill.is-online,
        .status-pill.is-ok {
            color: #dfffe9;
        }
        .status-pill.is-warning {
            color: #fff3cb;
        }
        .status-pill.is-offline,
        .status-pill.is-error {
            color: #ffe1e1;
        }
        .status-meta {
            margin-top: 8px;
            font-size: 12px;
            line-height: 1.5;
            color: rgba(255,255,255,0.82);
        }
        .status-dot {
            display: inline-block;
            width: 8px;
            height: 8px;
            border-radius: 50%;
            margin-right: 5px;
        }
        .status-dot.online { background: #4ade80; }
        .status-dot.offline { background: #f87171; }
        .status-dot.warning { background: #fbbf24; }

        .main-card {
            background: var(--panel);
            border: 1px solid rgba(255,255,255,0.78);
            border-radius: 28px;
            box-shadow: var(--shadow);
            overflow: hidden;
            backdrop-filter: blur(16px);
        }
        .filter-bar {
            background: rgba(255,255,255,0.72);
            padding: 18px 20px;
            border-bottom: 1px solid var(--line);
            display: flex;
            gap: 12px;
            flex-wrap: wrap;
            align-items: center;
        }
        .filter-btn {
            min-height: 40px;
            padding: 0 14px;
            border: 1px solid var(--line);
            border-radius: 999px;
            background: var(--panel-strong);
            cursor: pointer;
            font-size: 13px;
            color: var(--muted);
            transition: all 0.2s ease;
        }
        .filter-btn:hover { border-color: rgba(7, 193, 96, 0.35); color: var(--brand-deep); }
        .filter-btn.active {
            background: var(--brand);
            color: white;
            border-color: var(--brand);
            box-shadow: 0 10px 24px rgba(7, 193, 96, 0.22);
        }
        .search-box {
            flex: 1;
            min-width: 240px;
            min-height: 44px;
            padding: 0 14px;
            border: 1px solid var(--line);
            border-radius: 14px;
            font-size: 14px;
            background: white;
            color: var(--text);
            outline: none;
        }
        .search-box:focus {
            border-color: rgba(7, 193, 96, 0.42);
            box-shadow: 0 0 0 4px rgba(7, 193, 96, 0.08);
        }

        .tabs {
            display: flex;
            gap: 8px;
            padding: 12px 16px 0;
            background: transparent;
        }
        .tab {
            padding: 12px 16px;
            cursor: pointer;
            border-radius: 16px 16px 0 0;
            font-size: 14px;
            color: var(--muted);
            transition: all 0.2s ease;
        }
        .tab:hover { color: var(--brand-deep); }
        .tab.active {
            color: var(--brand-deep);
            background: rgba(255,255,255,0.66);
            box-shadow: inset 0 -2px 0 var(--brand);
        }

        .tab-content { display: none; }
        .tab-content.active { display: block; }

        .chat-list {
            padding: 16px;
            display: grid;
            gap: 12px;
        }
        .chat-contact {
            background: rgba(255,255,255,0.84);
            border: 1px solid var(--line);
            border-radius: 22px;
            overflow: hidden;
            box-shadow: var(--shadow-soft);
            transition: transform 0.18s ease, border-color 0.18s ease, box-shadow 0.18s ease;
        }
        .chat-contact:hover {
            transform: translateY(-1px);
            border-color: rgba(7, 193, 96, 0.22);
            box-shadow: 0 14px 32px rgba(15, 23, 42, 0.08);
        }
        .contact-header {
            padding: 16px 18px;
            background: transparent;
            cursor: pointer;
            display: flex;
            justify-content: space-between;
            align-items: center;
            gap: 14px;
        }
        .contact-name {
            font-weight: 700;
            font-size: 15px;
        }
        .contact-preview {
            font-size: 13px;
            color: var(--muted);
            margin-top: 6px;
            line-height: 1.5;
        }
        .contact-meta {
            text-align: right;
            flex-shrink: 0;
            color: var(--muted);
        }
        .contact-time { font-size: 12px; }
        .contact-count {
            display: inline-flex;
            align-items: center;
            justify-content: center;
            margin-top: 8px;
            padding: 4px 10px;
            border-radius: 999px;
            background: var(--brand-soft);
            color: var(--brand-deep);
            font-size: 12px;
            font-weight: 600;
        }
        .messages { display: none; padding: 10px 15px; }
        .messages.show { display: block; }
        .msg {
            padding: 8px 0;
            border-bottom: 1px solid #f0f0f0;
            font-size: 14px;
            line-height: 1.5;
        }
        .msg:last-child { border-bottom: none; }
        .msg-time { font-size: 11px; color: #999; margin-bottom: 2px; }
        .msg-sender { font-weight: 500; }
        .msg-sender.other { color: #07c160; }
        .msg-sender.me { color: #576b95; }
        .msg-content { word-break: break-all; }
        .empty {
            text-align: center;
            padding: 72px 24px;
            color: var(--muted);
            font-size: 14px;
        }
        .library-list {
            padding: 16px;
            display: grid;
            gap: 12px;
        }
        .library-card {
            display: flex;
            align-items: flex-start;
            gap: 14px;
            background: rgba(255,255,255,0.88);
            border: 1px solid var(--line);
            border-radius: 22px;
            padding: 16px 18px;
            box-shadow: var(--shadow-soft);
        }
        .library-avatar-img {
            width: 52px;
            height: 52px;
            border-radius: 16px;
            object-fit: cover;
            background: #f3f4f6;
            flex-shrink: 0;
        }
        .library-avatar-fallback {
            width: 52px;
            height: 52px;
            border-radius: 16px;
            background: linear-gradient(135deg, #08c55d, #0b8f6a);
            color: white;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            font-weight: 700;
            flex-shrink: 0;
        }
        .library-main {
            flex: 1;
            min-width: 0;
            display: grid;
            gap: 6px;
        }
        .library-title {
            display: flex;
            flex-wrap: wrap;
            align-items: center;
            gap: 8px;
        }
        .library-name {
            font-size: 15px;
            font-weight: 700;
            color: var(--text);
        }
        .library-tag {
            display: inline-flex;
            align-items: center;
            padding: 4px 10px;
            border-radius: 999px;
            background: var(--brand-soft);
            color: var(--brand-deep);
            font-size: 12px;
            font-weight: 600;
        }
        .library-meta {
            display: flex;
            flex-wrap: wrap;
            gap: 8px 10px;
            color: var(--muted);
            font-size: 12px;
        }
        .favorite-grid {
            padding: 16px;
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
            gap: 14px;
        }
        .favorite-card {
            background: rgba(255,255,255,0.9);
            border: 1px solid var(--line);
            border-radius: 22px;
            padding: 18px;
            box-shadow: var(--shadow-soft);
            display: grid;
            gap: 10px;
        }
        .favorite-head {
            display: flex;
            justify-content: space-between;
            gap: 10px;
            align-items: flex-start;
        }
        .favorite-title {
            font-size: 15px;
            font-weight: 700;
            color: var(--text);
            line-height: 1.5;
        }
        .favorite-summary {
            color: #475569;
            font-size: 13px;
            line-height: 1.6;
            white-space: pre-wrap;
            word-break: break-word;
        }
        .favorite-meta {
            display: flex;
            flex-wrap: wrap;
            gap: 8px;
            color: var(--muted);
            font-size: 12px;
        }
        .favorite-json {
            margin-top: 4px;
            padding: 10px 12px;
            border-radius: 14px;
            background: #f8fafc;
            color: #475569;
            font-size: 12px;
            line-height: 1.6;
            word-break: break-word;
        }

        .error-list { padding: 16px; }
        .error-item {
            background: #fff7f7;
            border: 1px solid rgba(248, 113, 113, 0.22);
            border-left: 4px solid #f87171;
            padding: 14px 16px;
            margin-bottom: 10px;
            border-radius: 16px;
        }
        .error-time { font-size: 11px; color: #999; }
        .error-msg { font-size: 13px; color: #dc2626; margin-top: 4px; }
        .event-list { padding: 16px; display: grid; gap: 10px; }
        .event-item {
            background: rgba(255,255,255,0.88);
            border: 1px solid var(--line);
            border-left: 4px solid var(--brand);
            padding: 14px 16px;
            border-radius: 16px;
            box-shadow: var(--shadow-soft);
        }
        .event-head {
            display: flex;
            justify-content: space-between;
            gap: 12px;
            color: var(--text);
            font-size: 13px;
            font-weight: 700;
        }
        .event-meta {
            margin-top: 6px;
            color: var(--muted);
            font-size: 12px;
        }
        .event-payload {
            margin-top: 8px;
            color: #334155;
            font-size: 12px;
            line-height: 1.5;
            word-break: break-all;
        }

        .detail-panel { padding: 18px; }
        .detail-card {
            background: rgba(255,255,255,0.88);
            border: 1px solid var(--line);
            border-radius: 22px;
            padding: 20px;
            box-shadow: var(--shadow-soft);
        }
        .detail-card h3 {
            margin-bottom: 14px;
            font-size: 16px;
        }
        .detail-row {
            display: flex;
            justify-content: space-between;
            gap: 20px;
            padding: 12px 0;
            border-bottom: 1px solid var(--line);
            font-size: 13px;
        }
        .detail-label { color: var(--muted); }
        .detail-value { font-weight: 500; }

        .chat-view { display: none; padding: 16px; }
        .chat-panel {
            overflow: hidden;
            border-radius: 24px;
            background: var(--panel-strong);
            border: 1px solid var(--line);
            box-shadow: var(--shadow-soft);
        }
        .chat-toolbar {
            color: white;
            padding: 16px 18px;
            background: linear-gradient(135deg, #08c55d 0%, #07b758 100%);
        }
        .chat-toolbar-main {
            display: flex;
            align-items: center;
            gap: 10px;
        }
        .chat-back {
            cursor: pointer;
            font-size: 18px;
            width: 34px;
            height: 34px;
            border-radius: 10px;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            background: rgba(255,255,255,0.16);
        }
        .chat-toolbar-meta {
            display: flex;
            gap: 8px;
            margin-top: 12px;
            align-items: center;
            flex-wrap: wrap;
        }
        .toolbar-btn {
            padding: 8px 12px;
            border: none;
            border-radius: 10px;
            background: rgba(255,255,255,0.16);
            color: white;
            cursor: pointer;
            font-size: 12px;
        }
        .toolbar-note {
            font-size: 12px;
            color: rgba(255,255,255,0.86);
        }
        .chat-bubble-wrap {
            display: flex;
            margin-bottom: 12px;
            gap: 10px;
            align-items: flex-start;
        }
        .chat-bubble-wrap.me { flex-direction: row-reverse; }
        .chat-avatar {
            width: 40px;
            height: 40px;
            border-radius: 14px;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 14px;
            font-weight: 700;
            flex-shrink: 0;
            margin-top: 2px;
        }
        .chat-avatar.other { background: #07c160; color: white; }
        .chat-avatar.me { background: #576b95; color: white; }
        .chat-bubble {
            max-width: min(72vw, 420px);
            min-width: 72px;
            padding: 12px 14px;
            border-radius: 18px;
            font-size: 15px;
            line-height: 1.5;
            word-break: normal;
            overflow-wrap: break-word;
            position: relative;
            white-space: pre-wrap;
            text-align: left;
            box-shadow: 0 10px 24px rgba(15, 23, 42, 0.06);
        }
        .chat-bubble.other {
            background: white;
            color: #333;
            border: 1px solid rgba(15, 23, 42, 0.06);
        }
        .chat-bubble.other::before {
            content: '';
            position: absolute;
            left: -6px;
            top: 16px;
            border: 6px solid transparent;
            border-right-color: white;
            border-left: none;
        }
        .chat-bubble.me {
            background: #95ec69;
            color: #333;
        }
        .chat-bubble.me::before {
            content: '';
            position: absolute;
            right: -6px;
            top: 16px;
            border: 6px solid transparent;
            border-left-color: #95ec69;
            border-right: none;
        }
        .chat-image {
            display: block;
            max-width: min(280px, 58vw);
            max-height: 360px;
            border-radius: 12px;
            object-fit: contain;
            background: white;
            box-shadow: 0 10px 24px rgba(15, 23, 42, 0.12);
        }
        .chat-image-note {
            color: var(--muted);
            font-size: 12px;
            line-height: 1.6;
        }
        .chat-time-group {
            text-align: center;
            margin: 20px 0 12px;
        }
        .chat-time-group span {
            background: rgba(15, 23, 42, 0.08);
            color: #7b879b;
            padding: 5px 12px;
            border-radius: 999px;
            font-size: 12px;
        }
        .chat-messages {
            padding: 18px;
            background: linear-gradient(180deg, #f4f6f8 0%, #eceff3 100%);
            min-height: 480px;
            max-height: 62vh;
            overflow-y: auto;
        }
        .custom-date-picker { position: relative; }
        .date-input {
            padding: 8px 12px;
            border: none;
            border-radius: 10px;
            font-size: 13px;
            width: 144px;
            cursor: pointer;
            background: white;
            color: #333;
        }
        .date-dropdown {
            display: none;
            position: absolute;
            top: 100%;
            left: 0;
            background: white;
            border-radius: 18px;
            box-shadow: 0 20px 40px rgba(15, 23, 42, 0.14);
            z-index: 100;
            width: 280px;
            max-height: 320px;
            overflow-y: auto;
            margin-top: 6px;
            border: 1px solid rgba(15, 23, 42, 0.08);
        }
        .date-dropdown-head {
            padding: 12px 14px;
            border-bottom: 1px solid var(--line);
            display: flex;
            justify-content: space-between;
            align-items: center;
        }
        .date-nav-btn {
            border: none;
            background: none;
            font-size: 16px;
            cursor: pointer;
            padding: 4px 8px;
        }
        .date-week {
            display: grid;
            grid-template-columns: repeat(7,1fr);
            padding: 8px;
            text-align: center;
            font-size: 12px;
            color: #999;
        }
        .date-grid {
            display: grid;
            grid-template-columns: repeat(7,1fr);
            padding: 4px 8px 8px;
            gap: 2px;
        }
        @media (max-width: 960px) {
            body { padding: 12px; }
            .status-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
        }
        @media (max-width: 680px) {
            .header-top {
                flex-direction: column;
                align-items: stretch;
            }
            .header-actions { min-width: 0; }
            .refresh-btn { flex: 1; }
            .refresh-meta { text-align: left; }
            .tabs {
                overflow-x: auto;
                padding-top: 10px;
            }
            .filter-bar { padding: 14px; }
            .search-box { min-width: 100%; }
            .contact-header { align-items: flex-start; }
            .chat-bubble { max-width: calc(100vw - 120px); }
            .chat-messages {
                min-height: 56vh;
                max-height: 56vh;
            }
        }
    </style>
</head>
<body>
    <div class="app-shell">
        <div class="header">
            <div class="header-top">
                <div class="header-copy">
                    <span class="header-tag">聊天记录总览</span>
                    <h1>微信聊天记录查看器</h1>
                    <p>保留原有接收、筛选、日期过滤、状态和错误日志能力，只把浏览体验整理成更清晰、更像正式产品的界面。</p>
                </div>
                <div class="header-actions">
                    <button class="refresh-btn" onclick="refreshData()">立即刷新</button>
                    <button class="refresh-btn" id="autoRefreshBtn" onclick="toggleAutoRefresh()">自动刷新：开</button>
                    <div class="refresh-meta" id="refreshMeta">每 10 秒自动刷新，等待首次加载</div>
                </div>
            </div>
            <div class="session-switcher">
                <label class="session-switcher-label" for="sessionPicker">当前监控实例</label>
                <select id="sessionPicker" class="session-select" onchange="changeSession(this.value)">
                    <option value="">全部实例</option>
                </select>
                <div class="session-meta" id="sessionMeta">等待客户端会话上报</div>
            </div>
            <div class="status-grid">
                <div class="status-item status-focus">
                    <div class="status-label">连接状态</div>
                    <div class="status-pill is-offline" id="connStatus">
                        <span class="status-dot offline"></span>
                        <span>离线</span>
                    </div>
                    <div class="status-meta" id="connStatusMeta">最近没有收到客户端心跳</div>
                </div>
                <div class="status-item">
                    <div class="status-label">消息总数</div>
                    <div class="status-value" id="totalMsg">0</div>
                    <div class="status-meta">已入库并可在聊天记录里查看</div>
                </div>
                <div class="status-item">
                    <div class="status-label">通讯录人数</div>
                    <div class="status-value" id="totalContacts">0</div>
                    <div class="status-meta">已同步到服务器的联系人数量</div>
                </div>
                <div class="status-item status-focus">
                    <div class="status-label">微信与解密</div>
                    <div class="status-pill is-warning" id="wechatStatus">
                        <span class="status-dot warning"></span>
                        <span>未知</span>
                    </div>
                    <div class="status-meta" id="wechatStatusMeta">等待客户端上报微信登录与解密结果</div>
                    <div class="status-meta" id="decryptStatusHero">上次解密状态：未知</div>
                </div>
            </div>
        </div>

        <div class="main-card">
            <div class="tabs">
                <div class="tab active" onclick="switchTab('messages', this)">聊天记录</div>
                <div class="tab" onclick="switchTab('contacts', this)">通讯录</div>
                <div class="tab" onclick="switchTab('favorites', this)">收藏</div>
                <div class="tab" onclick="switchTab('detail', this)">详细信息</div>
                <div class="tab" onclick="switchTab('events', this)">运行日志</div>
                <div class="tab" onclick="switchTab('errors', this)">错误日志</div>
            </div>

            <div id="tab-messages" class="tab-content active">
                <div class="filter-bar">
                    <input type="text" class="search-box" id="searchBox" placeholder="搜索联系人..." oninput="filterContacts()">
                    <button class="filter-btn active" onclick="setFilter('all', this)">全部</button>
                    <button class="filter-btn" onclick="setFilter('received', this)">收到的</button>
                    <button class="filter-btn" onclick="setFilter('sent', this)">发出的</button>
                </div>
                <div id="contactList" class="chat-list"></div>
                <div id="chatView" class="chat-view">
                    <div class="chat-panel">
                        <div class="chat-toolbar">
                            <div class="chat-toolbar-main">
                                <span class="chat-back" onclick="closeChat()">←</span>
                                <span id="chatName" style="font-weight:600;"></span>
                                <span id="chatCount" style="font-size:12px;opacity:0.82;"></span>
                            </div>
                            <div class="chat-toolbar-meta">
                                <div id="customDatePicker" class="custom-date-picker">
                                    <input type="text" class="date-input" id="dateInput" readonly placeholder="选择日期" onclick="toggleDatePicker()">
                                    <div id="dateDropdown" class="date-dropdown">
                                        <div class="date-dropdown-head">
                                            <button class="date-nav-btn" onclick="changeMonth(-1)">←</button>
                                            <span id="monthLabel" style="font-weight:500;color:#333;"></span>
                                            <button class="date-nav-btn" onclick="changeMonth(1)">→</button>
                                        </div>
                                        <div class="date-week">
                                            <div>日</div><div>一</div><div>二</div><div>三</div><div>四</div><div>五</div><div>六</div>
                                        </div>
                                        <div id="dateGrid" class="date-grid"></div>
                                    </div>
                                </div>
                                <button class="toolbar-btn" onclick="clearDateFilter()">全部</button>
                                <span id="dateInfo" class="toolbar-note"></span>
                            </div>
                        </div>
                        <div id="chatMessages" class="chat-messages"></div>
                    </div>
                </div>
            </div>

            <div id="tab-contacts" class="tab-content">
                <div class="filter-bar">
                    <input type="text" class="search-box" id="contactSearchBox" placeholder="搜索通讯录..." oninput="renderLibraryContacts()">
                </div>
                <div id="libraryContactList" class="library-list">
                    <div class="empty">通讯录还没有同步上来</div>
                </div>
            </div>

            <div id="tab-favorites" class="tab-content">
                <div class="filter-bar">
                    <input type="text" class="search-box" id="favoriteSearchBox" placeholder="搜索收藏标题或内容..." oninput="renderFavorites()">
                </div>
                <div id="favoriteGrid" class="favorite-grid">
                    <div class="empty" style="grid-column:1 / -1;">收藏还没有同步上来</div>
                </div>
            </div>

            <div id="tab-detail" class="tab-content">
                <div class="detail-panel">
                    <div class="detail-card">
                        <h3>监控详情</h3>
                        <div class="detail-row">
                            <span class="detail-label">最后心跳</span>
                            <span class="detail-value" id="lastHeartbeat">-</span>
                        </div>
                        <div class="detail-row">
                            <span class="detail-label">心跳间隔</span>
                            <span class="detail-value" id="heartbeatInterval">60秒</span>
                        </div>
                        <div class="detail-row">
                            <span class="detail-label">微信登录</span>
                            <span class="detail-value" id="wechatLoggedIn">未知</span>
                        </div>
                        <div class="detail-row">
                            <span class="detail-label">上次解密状态</span>
                            <span class="detail-value" id="decryptStatusDetail">未知</span>
                        </div>
                        <div class="detail-row">
                            <span class="detail-label">后台状态</span>
                            <span class="detail-value" id="workerAliveDetail">待判断</span>
                        </div>
                        <div class="detail-row">
                            <span class="detail-label">最近客户端会话</span>
                            <span class="detail-value" id="lastClientSession">-</span>
                        </div>
                        <div class="detail-row">
                            <span class="detail-label">最近一次事件</span>
                            <span class="detail-value" id="lastEventSummary">-</span>
                        </div>
                        <div class="detail-row">
                            <span class="detail-label">最近一次扫描</span>
                            <span class="detail-value" id="lastScanSummary">-</span>
                        </div>
                        <div class="detail-row">
                            <span class="detail-label">最近一次上传</span>
                            <span class="detail-value" id="lastPushSummary">-</span>
                        </div>
                        <div class="detail-row">
                            <span class="detail-label">最后收到消息</span>
                            <span class="detail-value" id="lastMessageTime">-</span>
                        </div>
                        <div class="detail-row">
                            <span class="detail-label">通讯录人数</span>
                            <span class="detail-value" id="contactCountDetail">0</span>
                        </div>
                        <div class="detail-row">
                            <span class="detail-label">收藏数量</span>
                            <span class="detail-value" id="favoriteCountDetail">0</span>
                        </div>
                        <div class="detail-row">
                            <span class="detail-label">最近事件时间</span>
                            <span class="detail-value" id="lastEventAt">-</span>
                        </div>
                        <div class="detail-row">
                            <span class="detail-label">今日消息</span>
                            <span class="detail-value" id="todayMsg">0</span>
                        </div>
                        <div class="detail-row" style="border:none;">
                            <span class="detail-label">服务运行时间</span>
                            <span class="detail-value" id="serverUptime">-</span>
                        </div>
                    </div>
                </div>
            </div>

            <div id="tab-errors" class="tab-content">
                <div class="error-list" id="errorList">
                    <div class="empty">暂无错误</div>
                </div>
            </div>

            <div id="tab-events" class="tab-content">
                <div class="event-list" id="eventList">
                    <div class="empty">暂无运行日志</div>
                </div>
            </div>
        </div>
    </div>

    <script>
        let allData = [];
        let allContacts = [];
        let allFavorites = [];
        let currentFilter = 'all';
        let statusData = {};
        let currentChatName = '';
        let currentChatMsgs = [];
        let sessionOptions = [];
        let selectedSessionId = '';
        let selectedClientSource = '';
        let hasManualSessionSelection = false;
        let availableDates = new Set();
        let currentMonth = new Date();
        let selectedDate = '';
        let autoRefreshEnabled = true;
        let refreshCountdown = 10;
        const refreshIntervalSeconds = 10;

        function buildScopedUrl(path) {
            const params = new URLSearchParams();
            if (selectedSessionId) params.set('session_id', selectedSessionId);
            if (selectedClientSource) params.set('source', selectedClientSource);
            const query = params.toString();
            return query ? `${path}?${query}` : path;
        }

        function sessionOptionValue(sessionId, clientSource) {
            return `${sessionId || ''}||${clientSource || ''}`;
        }

        function syncSessionPicker(sessions, preferredSessionId = '', preferredSource = '') {
            sessionOptions = Array.isArray(sessions) ? sessions : [];
            const picker = document.getElementById('sessionPicker');
            if (!picker) return;

            const knownValues = new Set(
                sessionOptions.map(item => sessionOptionValue(item.session_id, item.client_source))
            );
            const preferredValue = preferredSessionId
                ? sessionOptionValue(preferredSessionId, preferredSource)
                : '';

            if (!hasManualSessionSelection && preferredValue && knownValues.has(preferredValue)) {
                selectedSessionId = preferredSessionId;
                selectedClientSource = preferredSource || '';
            } else if (selectedSessionId) {
                const currentValue = sessionOptionValue(selectedSessionId, selectedClientSource);
                if (!knownValues.has(currentValue)) {
                    if (!hasManualSessionSelection && preferredSessionId) {
                        selectedSessionId = preferredSessionId;
                        selectedClientSource = preferredSource || '';
                    } else {
                        selectedSessionId = '';
                        selectedClientSource = '';
                    }
                }
            } else if (!hasManualSessionSelection && preferredSessionId) {
                selectedSessionId = preferredSessionId;
                selectedClientSource = preferredSource || '';
            }

            const options = ['<option value="">全部实例</option>'].concat(
                sessionOptions.map(item => {
                    const value = sessionOptionValue(item.session_id, item.client_source);
                    const source = item.client_source || 'unknown';
                    const ip = item.client_ip ? ` ｜ ${item.client_ip}` : '';
                    const label = `${item.session_id || '-'} ｜ ${source}${ip}`;
                    return `<option value="${escapeHtml(value)}">${escapeHtml(label)}</option>`;
                })
            );
            picker.innerHTML = options.join('');
            picker.value = selectedSessionId ? sessionOptionValue(selectedSessionId, selectedClientSource) : '';
            updateSessionMeta();
        }

        function updateSessionMeta() {
            const meta = document.getElementById('sessionMeta');
            if (!meta) return;
            if (!selectedSessionId) {
                meta.textContent = `当前查看全部实例，已发现 ${sessionOptions.length} 个客户端会话。`;
                return;
            }
            const current = sessionOptions.find(item =>
                item.session_id === selectedSessionId && (item.client_source || '') === (selectedClientSource || '')
            );
            if (!current) {
                meta.textContent = '当前实例不在最近会话列表里，可能暂时没有新上报。';
                return;
            }
            const parts = [
                `来源：${current.client_source || '-'}`,
                current.client_ip ? `IP：${current.client_ip}` : '',
                current.last_event_at ? `最后事件：${current.last_event_at}` : '',
            ].filter(Boolean);
            meta.textContent = parts.join(' ｜ ');
        }

        async function changeSession(value) {
            hasManualSessionSelection = true;
            if (!value) {
                selectedSessionId = '';
                selectedClientSource = '';
            } else {
                const [sessionId, clientSource] = value.split('||');
                selectedSessionId = sessionId || '';
                selectedClientSource = clientSource || '';
            }
            closeChat();
            updateSessionMeta();
            await refreshData();
        }

        async function refreshData() {
            refreshCountdown = refreshIntervalSeconds;
            await loadAll();
        }

        async function loadAll() {
            await loadStatus();
            await Promise.all([loadData(), loadContactsData(), loadFavoritesData(), loadEvents()]);
            updateRefreshMeta('刚刚已刷新');
        }

        function toggleAutoRefresh() {
            autoRefreshEnabled = !autoRefreshEnabled;
            refreshCountdown = refreshIntervalSeconds;
            document.getElementById('autoRefreshBtn').textContent = `自动刷新：${autoRefreshEnabled ? '开' : '关'}`;
            updateRefreshMeta(autoRefreshEnabled ? '自动刷新已开启' : '自动刷新已暂停');
        }

        function updateRefreshMeta(prefix = '') {
            const meta = document.getElementById('refreshMeta');
            const nextText = autoRefreshEnabled ? `下次 ${refreshCountdown} 秒后刷新` : '自动刷新已暂停';
            meta.textContent = prefix ? `${prefix}，${nextText}` : nextText;
        }

        setInterval(() => {
            if (!autoRefreshEnabled) {
                updateRefreshMeta();
                return;
            }

            refreshCountdown -= 1;
            if (refreshCountdown <= 0) {
                refreshCountdown = refreshIntervalSeconds;
                loadAll();
                return;
            }

            updateRefreshMeta();
        }, 1000);

        async function loadData() {
            try {
                const resp = await fetch(buildScopedUrl('/api/messages'));
                const data = await resp.json();
                allData = data.messages || [];
                document.getElementById('totalMsg').textContent = data.total ?? allData.length;
                
                const today = new Date();
                today.setHours(0,0,0,0);
                const todayTs = today.getTime() / 1000;
                const todayCount = allData.filter(m => m.create_time >= todayTs).length;
                document.getElementById('todayMsg').textContent = todayCount;
                
                if (allData.length > 0) {
                    const lastTime = Math.max(...allData.map(m => m.create_time));
                    document.getElementById('lastMessageTime').textContent = 
                        new Date(lastTime * 1000).toLocaleString('zh-CN');
                } else {
                    document.getElementById('lastMessageTime').textContent = '-';
                }
                
                renderContacts();
            } catch (e) {
                console.error('加载消息失败:', e);
            }
        }

        async function loadContactsData() {
            try {
                const resp = await fetch(buildScopedUrl('/api/contacts'));
                const data = await resp.json();
                allContacts = data.contacts || [];
                document.getElementById('totalContacts').textContent = data.total ?? allContacts.length;
                document.getElementById('contactCountDetail').textContent = data.total ?? allContacts.length;
                renderLibraryContacts();
            } catch (e) {
                console.error('加载通讯录失败:', e);
            }
        }

        async function loadFavoritesData() {
            try {
                const resp = await fetch(buildScopedUrl('/api/favorites'));
                const data = await resp.json();
                allFavorites = data.favorites || [];
                document.getElementById('favoriteCountDetail').textContent = data.total ?? allFavorites.length;
                renderFavorites();
            } catch (e) {
                console.error('加载收藏失败:', e);
            }
        }

        async function loadStatus() {
            try {
                const resp = await fetch(buildScopedUrl('/api/status'));
                statusData = await resp.json();
                syncSessionPicker(
                    statusData.sessions || [],
                    statusData.current_session_id || '',
                    statusData.current_client_source || ''
                );
                
                const heartbeat = statusData.last_heartbeat || 0;
                const now = Math.floor(Date.now() / 1000);
                const heartbeatTimeout = statusData.heartbeat_timeout_seconds || 180;
                const heartbeatTimeoutText = formatDuration(heartbeatTimeout);
                const isOnline = heartbeat > 0 && (now - heartbeat) < heartbeatTimeout;
                const connStatusEl = document.getElementById('connStatus');
                const connStatusMetaEl = document.getElementById('connStatusMeta');
                const wechatStatusEl = document.getElementById('wechatStatus');
                const wechatStatusMetaEl = document.getElementById('wechatStatusMeta');
                const decryptStatusHeroEl = document.getElementById('decryptStatusHero');
                
                connStatusEl.className = `status-pill ${isOnline ? 'is-online' : 'is-offline'}`;
                connStatusEl.innerHTML = isOnline
                    ? '<span class="status-dot online"></span><span>在线</span>'
                    : '<span class="status-dot offline"></span><span>离线</span>';
                connStatusMetaEl.textContent = isOnline
                    ? '客户端仍在按心跳周期上报状态'
                    : `超过 ${heartbeatTimeoutText} 没有收到新的状态心跳`;
                
                wechatStatusEl.className = `status-pill ${statusData.wechat_logged_in ? 'is-online' : 'is-warning'}`;
                wechatStatusEl.innerHTML = statusData.wechat_logged_in
                    ? '<span class="status-dot online"></span><span>微信已登录</span>'
                    : '<span class="status-dot warning"></span><span>微信未登录</span>';
                wechatStatusMetaEl.textContent = statusData.wechat_logged_in
                    ? '采集端检测到微信客户端处于登录状态'
                    : '采集端当前没有检测到微信登录';
                decryptStatusHeroEl.textContent = `上次解密状态：${statusData.decrypt_ok ? '正常' : '异常'}`;
                decryptStatusHeroEl.style.color = statusData.decrypt_ok ? 'rgba(223,255,233,0.9)' : 'rgba(255,225,225,0.92)';

                document.getElementById('wechatLoggedIn').textContent = statusData.wechat_logged_in
                    ? '已登录'
                    : '未登录';

                document.getElementById('decryptStatusDetail').innerHTML = statusData.decrypt_ok
                    ? '正常'
                    : '异常';

                document.getElementById('decryptStatusDetail').style.color = statusData.decrypt_ok
                    ? '#06934a'
                    : '#dc2626';

                document.getElementById('wechatLoggedIn').style.color = statusData.wechat_logged_in
                    ? '#06934a'
                    : '#d97706';

                document.getElementById('workerAliveDetail').textContent = isOnline
                    ? '后台仍在上报'
                    : '超过心跳阈值，疑似已停止';
                document.getElementById('workerAliveDetail').style.color = isOnline
                    ? '#06934a'
                    : '#dc2626';

                const sessionId = statusData.current_session_id || statusData.last_client_session_id || '-';
                const sessionSource = statusData.current_client_source || statusData.last_client_source || '';
                const sessionIp = statusData.current_client_ip || '';
                const sessionAt = statusData.last_client_event_at || '';
                const sessionPieces = [sessionId];
                if (sessionSource) sessionPieces.push(sessionSource);
                if (sessionIp) sessionPieces.push(sessionIp);
                if (sessionAt) sessionPieces.push(sessionAt);
                document.getElementById('lastClientSession').textContent = sessionPieces.join(' ｜ ');

                document.getElementById('lastEventSummary').textContent = statusData.last_event_summary || '-';
                document.getElementById('lastScanSummary').textContent = statusData.last_scan_result_summary || '-';
                document.getElementById('lastPushSummary').textContent = statusData.last_push_result_summary || '-';
                document.getElementById('lastEventAt').textContent = statusData.last_event_at || '-';

                if (heartbeat > 0) {
                    document.getElementById('lastHeartbeat').textContent = 
                        new Date(heartbeat * 1000).toLocaleString('zh-CN');
                }
                
                renderErrors(statusData.errors || []);
            } catch (e) {
                console.error('加载状态失败:', e);
            }
        }

        function renderErrors(errors) {
            const list = document.getElementById('errorList');
            if (errors.length === 0) {
                list.innerHTML = '<div class="empty">暂无错误</div>';
                return;
            }
            list.innerHTML = [...errors].reverse().map(e => `
                <div class="error-item">
                    <div class="error-time">${e.time}</div>
                    <div class="error-msg">${escapeHtml(e.message)}</div>
                </div>
            `).join('');
        }

        async function loadEvents() {
            try {
                const resp = await fetch(buildScopedUrl('/api/events'));
                const data = await resp.json();
                renderEvents(data.events || []);
            } catch (e) {
                console.error('加载运行日志失败:', e);
            }
        }

        function renderEvents(events) {
            const list = document.getElementById('eventList');
            if (events.length === 0) {
                list.innerHTML = '<div class="empty">暂无运行日志</div>';
                return;
            }

            list.innerHTML = events.map(e => `
                <div class="event-item">
                    <div class="event-head">
                        <span>${escapeHtml(e.title || e.event_name)}</span>
                        <span>${escapeHtml(e.created_at)}</span>
                    </div>
                    <div class="event-meta">来源：${escapeHtml(e.source_label || e.source || '-')} ｜ 会话：${escapeHtml(e.session_id || '-')} ｜ IP：${escapeHtml(e.client_ip || '-')}</div>
                    <div class="event-payload">${escapeHtml(e.details || '')}</div>
                </div>
            `).join('');
        }

        function switchTab(name, el) {
            document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
            document.querySelectorAll('.tab-content').forEach(t => t.classList.remove('active'));
            el.classList.add('active');
            document.getElementById('tab-' + name).classList.add('active');
        }

        function filterContacts() {
            renderContacts();
        }

        function setFilter(filter, btn) {
            currentFilter = filter;
            document.querySelectorAll('.filter-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            renderContacts();
        }

        function renderContacts() {
            const search = document.getElementById('searchBox').value.toLowerCase();
            const grouped = {};
            
            allData.forEach(msg => {
                if (currentFilter === 'received' && msg.is_sender) return;
                if (currentFilter === 'sent' && !msg.is_sender) return;
                
                const name = msg.nickname || msg.wxid;
                if (search && !name.toLowerCase().includes(search)) return;
                
                if (!grouped[name]) grouped[name] = [];
                grouped[name].push(msg);
            });

            const list = document.getElementById('contactList');
            if (Object.keys(grouped).length === 0) {
                list.innerHTML = '<div class="empty">暂无消息</div>';
                return;
            }

            list.innerHTML = Object.entries(grouped)
                .sort((a, b) => {
                    const lastA = Math.max(...a[1].map(m => m.create_time));
                    const lastB = Math.max(...b[1].map(m => m.create_time));
                    return lastB - lastA;
                })
                .map(([name, msgs]) => {
                    const lastMsg = msgs[msgs.length - 1];
                    const lastTime = new Date(lastMsg.create_time * 1000).toLocaleTimeString('zh-CN', {hour:'2-digit', minute:'2-digit'});
                    const preview = lastMsg.content.length > 20 ? lastMsg.content.substring(0, 20) + '...' : lastMsg.content;
                    return `
                    <div class="chat-contact" onclick="openChat('${escapeHtml(name)}')">
                        <div class="contact-header">
                            <div>
                                <span class="contact-name">${escapeHtml(name)}</span>
                                <div class="contact-preview">${escapeHtml(preview)}</div>
                            </div>
                            <div class="contact-meta">
                                <div class="contact-time">${lastTime}</div>
                                <div class="contact-count">${msgs.length}条</div>
                            </div>
                        </div>
                    </div>
                `}).join('');
        }

        function renderLibraryContacts() {
            const search = (document.getElementById('contactSearchBox')?.value || '').toLowerCase().trim();
            const list = document.getElementById('libraryContactList');
            const filtered = allContacts.filter(contact => {
                if (!search) return true;
                return [
                    contact.display_name,
                    contact.remark,
                    contact.nick_name,
                    contact.alias,
                    contact.wxid,
                ].some(value => (value || '').toLowerCase().includes(search));
            });

            if (filtered.length === 0) {
                list.innerHTML = '<div class="empty">没有匹配到通讯录联系人</div>';
                return;
            }

            list.innerHTML = filtered.map(contact => {
                const name = contact.display_name || contact.remark || contact.nick_name || contact.alias || contact.wxid || '未命名联系人';
                const remark = contact.remark && contact.remark !== name ? `备注：${contact.remark}` : '';
                const nick = contact.nick_name && contact.nick_name !== name ? `昵称：${contact.nick_name}` : '';
                const alias = contact.alias ? `微信号：${contact.alias}` : '';
                const wxid = contact.wxid ? `wxid：${contact.wxid}` : '';
                const pieces = [remark, nick, alias, wxid].filter(Boolean);

                return `
                    <div class="library-card">
                        ${renderLibraryAvatar(contact.avatar, name)}
                        <div class="library-main">
                            <div class="library-title">
                                <span class="library-name">${escapeHtml(name)}</span>
                                ${contact.avatar ? '<span class="library-tag">有头像</span>' : '<span class="library-tag">无头像</span>'}
                            </div>
                            <div class="library-meta">${pieces.map(item => `<span>${escapeHtml(item)}</span>`).join('')}</div>
                        </div>
                    </div>
                `;
            }).join('');
        }

        function renderFavorites() {
            const search = (document.getElementById('favoriteSearchBox')?.value || '').toLowerCase().trim();
            const list = document.getElementById('favoriteGrid');
            const filtered = allFavorites.filter(item => {
                if (!search) return true;
                return [
                    item.title,
                    item.summary,
                    item.item_type,
                    item.item_sub_type,
                    item.source_table,
                ].some(value => (value || '').toLowerCase().includes(search));
            });

            if (filtered.length === 0) {
                list.innerHTML = '<div class="empty" style="grid-column:1 / -1;">没有匹配到收藏内容</div>';
                return;
            }

            list.innerHTML = filtered.map(item => {
                const updatedAt = formatServerDate(item.updated_at) || formatUnixTime(item.source_updated_at);
                const title = item.title || '[无标题收藏]';
                const summary = item.summary || '';
                const meta = [
                    item.item_type ? `类型：${item.item_type}` : '',
                    item.item_sub_type ? `子类型：${item.item_sub_type}` : '',
                    item.source_table ? `表：${item.source_table}` : '',
                    updatedAt ? `更新时间：${updatedAt}` : '',
                ].filter(Boolean);
                const payload = parseServerJson(item.data_json);
                const payloadPreview = payload ? JSON.stringify(payload, null, 2) : '';

                return `
                    <div class="favorite-card">
                        <div class="favorite-head">
                            <div class="favorite-title">${escapeHtml(title)}</div>
                            <span class="library-tag">收藏</span>
                        </div>
                        <div class="favorite-summary">${escapeHtml(summary || '暂无摘要')}</div>
                        <div class="favorite-meta">${meta.map(text => `<span>${escapeHtml(text)}</span>`).join('')}</div>
                        ${payloadPreview ? `<div class="favorite-json">${escapeHtml(payloadPreview)}</div>` : ''}
                    </div>
                `;
            }).join('');
        }

        function openChat(name) {
            currentChatName = name;
            currentChatMsgs = allData.filter(m => (m.nickname || m.wxid) === name);
            if (currentChatMsgs.length === 0) return;

            document.getElementById('contactList').style.display = 'none';
            document.querySelector('.filter-bar').style.display = 'none';
            document.getElementById('chatView').style.display = 'block';
            document.getElementById('chatName').textContent = name;
            document.getElementById('chatCount').textContent = `${currentChatMsgs.length} 条`;
            
            // 收集有消息的日期
            availableDates = new Set(currentChatMsgs.map(m => {
                const d = new Date(m.create_time * 1000);
                return d.toISOString().split('T')[0];
            }));
            
            // 设置月份到有消息的月份
            const dates = [...availableDates].sort();
            if (dates.length > 0) {
                currentMonth = new Date(dates[dates.length - 1] + 'T00:00:00');
            }
            
            selectedDate = '';
            document.getElementById('dateInput').value = '';
            document.getElementById('dateInfo').textContent = `共 ${currentChatMsgs.length} 条，${dates.length} 天`;
            renderDatePicker();
            renderChatMessages(currentChatMsgs);
        }

        function renderDatePicker() {
            const year = currentMonth.getFullYear();
            const month = currentMonth.getMonth();
            
            document.getElementById('monthLabel').textContent = `${year}年${month + 1}月`;
            
            const firstDay = new Date(year, month, 1).getDay();
            const daysInMonth = new Date(year, month + 1, 0).getDate();
            const today = new Date().toISOString().split('T')[0];
            
            let html = '';
            
            // 填充空白
            for (let i = 0; i < firstDay; i++) {
                html += '<div></div>';
            }
            
            // 日期格子
            for (let day = 1; day <= daysInMonth; day++) {
                const dateStr = `${year}-${String(month + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
                const hasMsg = availableDates.has(dateStr);
                const isToday = dateStr === today;
                const isSelected = dateStr === selectedDate;
                
                let style = 'padding:8px 4px;text-align:center;border-radius:6px;cursor:pointer;font-size:13px;';
                if (hasMsg) {
                    style += 'background:#07c160;color:white;font-weight:600;';
                } else if (isToday) {
                    style += 'border:1px solid #07c160;color:#07c160;';
                } else {
                    style += 'color:#999;';
                }
                if (isSelected && !hasMsg) {
                    style += 'background:#e0e0e0;';
                }
                
                html += `<div style="${style}" onclick="selectDate('${dateStr}')" onmouseover="this.style.opacity='0.8'" onmouseout="this.style.opacity='1'">${day}</div>`;
            }
            
            document.getElementById('dateGrid').innerHTML = html;
        }

        function changeMonth(delta) {
            currentMonth.setMonth(currentMonth.getMonth() + delta);
            renderDatePicker();
        }

        function selectDate(dateStr) {
            selectedDate = dateStr;
            const d = new Date(dateStr + 'T00:00:00');
            document.getElementById('dateInput').value = `${d.getMonth() + 1}月${d.getDate()}日`;
            document.getElementById('dateDropdown').style.display = 'none';
            filterByDate();
        }

        function toggleDatePicker() {
            const dropdown = document.getElementById('dateDropdown');
            dropdown.style.display = dropdown.style.display === 'none' ? 'block' : 'none';
        }

        function filterByDate() {
            if (!selectedDate) {
                renderChatMessages(currentChatMsgs);
                document.getElementById('dateInfo').textContent = `共 ${currentChatMsgs.length} 条`;
                return;
            }
            
            const filtered = currentChatMsgs.filter(m => {
                const msgDate = new Date(m.create_time * 1000).toISOString().split('T')[0];
                return msgDate === selectedDate;
            });
            
            const d = new Date(selectedDate + 'T00:00:00');
            const displayDate = d.toLocaleDateString('zh-CN', {month:'long', day:'numeric', weekday:'long'});
            document.getElementById('dateInfo').textContent = `${displayDate} · ${filtered.length} 条`;
            renderChatMessages(filtered);
        }

        function clearDateFilter() {
            selectedDate = '';
            document.getElementById('dateInput').value = '';
            document.getElementById('dateDropdown').style.display = 'none';
            renderChatMessages(currentChatMsgs);
            document.getElementById('dateInfo').textContent = `共 ${currentChatMsgs.length} 条`;
        }

        // 点击外部关闭下拉
        document.addEventListener('click', function(e) {
            if (!e.target.closest('#customDatePicker')) {
                document.getElementById('dateDropdown').style.display = 'none';
            }
        });

        function renderChatMessages(msgs) {
            const container = document.getElementById('chatMessages');
            if (msgs.length === 0) {
                container.innerHTML = '<div style="text-align:center;padding:40px;color:#999;">该日期暂无消息</div>';
                return;
            }

            let lastDate = '';
            let lastTime = 0;
            
            // 收集每个 wxid 的头像
            const avatarCache = {};
            msgs.forEach(m => {
                if (m.avatar && m.wxid) {
                    avatarCache[m.wxid] = m.avatar;
                }
            });
            
            container.innerHTML = msgs.map(m => {
                const isMe = m.is_sender;
                const initial = isMe ? '我' : currentChatName.charAt(0);
                const msgDate = new Date(m.create_time * 1000);
                const timeStr = msgDate.toLocaleTimeString('zh-CN', {hour:'2-digit', minute:'2-digit'});
                
                // 日期分隔线
                let dateSeparator = '';
                const fullDateStr = msgDate.toLocaleDateString('zh-CN', {year:'numeric', month:'long', day:'numeric', weekday:'long'});
                if (fullDateStr !== lastDate) {
                    lastDate = fullDateStr;
                    dateSeparator = `<div class="chat-time-group"><span>${fullDateStr}</span></div>`;
                }
                
                // 时间间隔超过5分钟显示时间
                let timeLabel = '';
                if (m.create_time - lastTime > 300) {
                    timeLabel = `<div class="chat-time-group"><span>${timeStr}</span></div>`;
                }
                lastTime = m.create_time;
                
                // 头像：有图片用图片，没有用首字母
                let avatarHtml = '';
                const avatarData = avatarCache[m.wxid] || '';
                if (!isMe && avatarData) {
                    avatarHtml = `<img src="data:image/jpeg;base64,${avatarData}" style="width:40px;height:40px;border-radius:4px;object-fit:cover;">`;
                } else {
                    avatarHtml = `<div class="chat-avatar ${isMe ? 'me' : 'other'}">${initial}</div>`;
                }
                
                const contentHtml = renderMessageContent(m);
                return dateSeparator + timeLabel + `
                    <div class="chat-bubble-wrap ${isMe ? 'me' : ''}">
                        ${avatarHtml}
                        <div>
                            <div class="chat-bubble ${isMe ? 'me' : 'other'}">${contentHtml}</div>
                        </div>
                    </div>
                `;
            }).join('');

            container.scrollTop = container.scrollHeight;
        }

        function renderMessageContent(m) {
            if (m.media_type === 'image' && m.media_data) {
                const mime = m.media_mime || 'image/jpeg';
                return `<img class="chat-image" src="data:${escapeHtml(mime)};base64,${m.media_data}" alt="图片消息">`;
            }

            if (m.media_type === 'image') {
                return '<span class="chat-image-note">[图片] 图片文件未能从 Windows 本地读取，详情看运行日志。</span>';
            }

            return escapeHtml(m.content || '');
        }

        function closeChat() {
            document.getElementById('contactList').style.display = 'block';
            document.querySelector('.filter-bar').style.display = 'flex';
            document.getElementById('chatView').style.display = 'none';
        }

        function renderLibraryAvatar(base64, name) {
            if (base64) {
                return `<img class="library-avatar-img" src="data:image/jpeg;base64,${base64}" alt="${escapeHtml(name)}">`;
            }
            return `<div class="library-avatar-fallback">${escapeHtml((name || '?').charAt(0) || '?')}</div>`;
        }

        function parseServerJson(value) {
            if (!value) return null;
            if (typeof value === 'object') return value;
            try {
                return JSON.parse(value);
            } catch {
                return null;
            }
        }

        function formatServerDate(value) {
            if (!value) return '';
            const date = new Date(value);
            if (Number.isNaN(date.getTime())) return '';
            return date.toLocaleString('zh-CN');
        }

        function formatUnixTime(value) {
            if (!value) return '';
            const ts = Number(value);
            if (!Number.isFinite(ts) || ts <= 0) return '';
            const ms = ts > 1e12 ? ts : ts * 1000;
            return new Date(ms).toLocaleString('zh-CN');
        }

        function escapeHtml(text) {
            const div = document.createElement('div');
            div.textContent = text;
            return div.innerHTML;
        }

        function formatDuration(seconds) {
            if (seconds >= 60 && seconds % 60 === 0) {
                return `${seconds / 60} 分钟`;
            }
            return `${seconds} 秒`;
        }

        loadAll();
    </script>
</body>
</html>
"""


def parse_int(value, default, minimum=None, maximum=None):
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        parsed = default

    if minimum is not None:
        parsed = max(minimum, parsed)
    if maximum is not None:
        parsed = min(maximum, parsed)
    return parsed


def normalize_client_source(value, default="unknown"):
    text = str(value or "").strip().lower()
    if not text:
        text = default
    return text[:32]


def normalize_session_id(value):
    text = str(value or "").strip()
    if not text:
        return ""
    text = re.sub(r"\s+", "_", text)
    return text[:120]


def build_legacy_session_id(remote_addr, client_source):
    source = normalize_client_source(client_source, default="unknown")
    addr = str(remote_addr or "").strip() or "unknown"
    safe_addr = re.sub(r"[^0-9A-Za-z:._-]+", "_", addr)
    return f"legacy:{source}:{safe_addr}"[:120]


def resolve_client_context(conn, data=None, remote_addr="", fallback_source="unknown"):
    data = data or {}
    explicit_source = normalize_client_source(data.get("source"), default=fallback_source)
    explicit_session_id = normalize_session_id(data.get("session_id"))
    client_ip = str(remote_addr or "").strip()
    inferred = False

    if explicit_session_id:
        return {
            "session_id": explicit_session_id,
            "client_source": explicit_source,
            "client_ip": client_ip,
            "inferred": inferred,
        }

    lookback_from = datetime.now() - timedelta(seconds=max(CLIENT_SESSION_LOOKBACK_SECONDS, 60))
    with conn.cursor() as cursor:
        cursor.execute(
            """
            SELECT session_id, source, client_ip
            FROM event_logs
            WHERE client_ip = %s
              AND source LIKE 'client%%'
              AND session_id <> ''
              AND created_at >= %s
            ORDER BY id DESC
            LIMIT 1
            """,
            (client_ip, lookback_from),
        )
        row = cursor.fetchone()

    if row:
        inferred = True
        return {
            "session_id": normalize_session_id(row.get("session_id")),
            "client_source": normalize_client_source(row.get("source"), default=explicit_source),
            "client_ip": client_ip or str(row.get("client_ip") or "").strip(),
            "inferred": inferred,
        }

    return {
        "session_id": build_legacy_session_id(client_ip, explicit_source),
        "client_source": explicit_source,
        "client_ip": client_ip,
        "inferred": True,
    }


def parse_scope_filters(args):
    session_id = normalize_session_id(args.get("session_id"))
    client_source = normalize_client_source(args.get("source"), default="") if args.get("source") else ""
    return session_id, client_source


def build_scope_where(session_id="", client_source="", table_alias=""):
    conditions = []
    params = []
    prefix = f"{table_alias}." if table_alias else ""
    if session_id:
        conditions.append(f"{prefix}session_id = %s")
        params.append(session_id)
    if client_source:
        conditions.append(f"{prefix}client_source = %s")
        params.append(client_source)
    return conditions, params


LOG_SOURCE_LABELS = {
    "client_cs": "Windows 桌宠",
    "client_py": "Windows 采集脚本",
    "server": "服务器",
    "web": "网页",
}


SKIPPED_MONITOR_EVENTS = {
    "client_decrypt_progress",
    "client_decrypt_slow",
    "client_push_started",
    "client_push_batch_started",
    "client_push_batch_result",
    "client_memory_pipeline_started",
    "client_memory_db_progress",
    "client_memory_db_released",
    "client_memory_pipeline_result",
    "client_wechat_detected",
    "client_wechat_process_detected",
    "client_media_loaded",
    "server_messages_received",
    "server_contacts_received",
    "server_favorites_received",
    "server_status_updated",
    "codex_domain_check",
}


def build_monitor_log(
    log_type,
    event_name,
    source,
    session_id,
    client_source,
    client_ip,
    title,
    details,
    *,
    severity="info",
    stage_key="",
    payload=None,
    dedupe_key="",
    aggregate_window_seconds=0,
    event_time=None,
):
    normalized_source = normalize_client_source(source, default="unknown")
    normalized_client_source = normalize_client_source(
        client_source or normalized_source,
        default="unknown",
    )
    return {
        "log_type": log_type,
        "event_name": event_name,
        "source": normalized_source,
        "session_id": normalize_session_id(session_id),
        "client_source": normalized_client_source,
        "client_ip": str(client_ip or ""),
        "severity": severity,
        "stage_key": stage_key,
        "title": title,
        "details": details,
        "payload_json": json.dumps(payload or {}, ensure_ascii=False),
        "dedupe_key": dedupe_key[:255],
        "aggregate_window_seconds": int(max(aggregate_window_seconds or 0, 0)),
        "event_time": event_time or datetime.now(),
    }


def compose_monitor_details(details, occurrence_count):
    text = str(details or "").strip()
    if occurrence_count <= 1:
        return text
    if not text:
        return f"最近已汇总 {occurrence_count} 次。"
    return f"{text} 最近已汇总 {occurrence_count} 次。"


def normalize_monitor_event(event_name, source, payload=None, session_id="", client_ip="", client_source=""):
    payload = payload or {}
    if event_name in SKIPPED_MONITOR_EVENTS:
        return []

    details = describe_event(event_name, payload)
    normalized_source = normalize_client_source(source, default="unknown")
    normalized_client_source = normalize_client_source(
        client_source or normalized_source,
        default="unknown",
    )
    logs = []

    def runtime(title, *, stage_key="", severity="info"):
        logs.append(
            build_monitor_log(
                "runtime",
                event_name,
                normalized_source,
                session_id,
                normalized_client_source,
                client_ip,
                title,
                details,
                severity=severity,
                stage_key=stage_key,
                payload=payload,
            )
        )

    def error(title, *, stage_key="", dedupe_suffix="", severity="error", aggregate_window_seconds=None):
        dedupe_parts = [
            normalize_session_id(session_id),
            normalized_client_source,
            event_name,
            stage_key,
            dedupe_suffix or str(payload.get("reason") or payload.get("stage") or payload.get("status_code") or ""),
        ]
        dedupe_key = ":".join(filter(None, dedupe_parts))
        logs.append(
            build_monitor_log(
                "error",
                event_name,
                normalized_source,
                session_id,
                normalized_client_source,
                client_ip,
                title,
                details,
                severity=severity,
                stage_key=stage_key,
                payload=payload,
                dedupe_key=dedupe_key,
                aggregate_window_seconds=aggregate_window_seconds or MONITOR_LOG_AGGREGATE_WINDOW_SECONDS,
            )
        )

    if event_name == "client_scan_started":
        runtime("开始扫描", stage_key="scan_started")
        return logs
    if event_name == "client_v4_data_dir_result":
        runtime("目录识别结果", stage_key="data_dir")
        return logs
    if event_name == "client_chatlog_key_result":
        runtime("拿到数据库密钥" if payload.get("success") else "密钥获取失败", stage_key="chatlog_key")
        if not payload.get("success"):
            error("数据库密钥获取失败", stage_key="chatlog_key")
        return logs
    if event_name in {"client_weflow_wcdb_export_result", "client_wechat_decrypt_export_result"}:
        runtime("数据库解密结果", stage_key="decrypt_export")
        if payload.get("success") is False:
            error("数据库解密失败", stage_key="decrypt_export")
        return logs
    if event_name == "client_contacts_push_result":
        runtime("通讯录上传结果", stage_key="contacts_push")
        if payload.get("success") is False:
            error("通讯录上传失败", stage_key="contacts_push")
        return logs
    if event_name == "client_favorites_export_result":
        runtime("收藏导出结果", stage_key="favorites_export")
        if payload.get("success") is False:
            if payload.get("reason") == "favorite_db_missing":
                error("收藏库缺失", stage_key="favorites_export")
            else:
                error("收藏库读取失败", stage_key="favorites_export")
        return logs
    if event_name == "client_favorites_push_result":
        runtime("收藏上传结果", stage_key="favorites_push")
        if payload.get("success") is False:
            error("收藏上传失败", stage_key="favorites_push")
        return logs
    if event_name == "client_push_result":
        runtime("聊天记录上传结果", stage_key="messages_push")
        return logs
    if event_name == "client_push_failed":
        runtime("聊天记录上传失败", stage_key="messages_push", severity="warning")
        error("聊天记录上传失败", stage_key="messages_push")
        return logs
    if event_name == "client_disk_pipeline_result":
        runtime("临时落盘链路结果", stage_key="disk_pipeline")
        if payload.get("success") is False:
            error("数据库解密失败", stage_key="disk_pipeline")
        return logs
    if event_name == "client_wal_merge_skipped":
        runtime("跳过 WAL 合并", stage_key="wal_merge")
        return logs
    if event_name == "client_decrypt_db_result":
        result = str(payload.get("result") or "").strip()
        if result == "success":
            runtime("单库解密成功", stage_key="decrypt_db")
        elif result == "skipped":
            runtime("单库跳过解密", stage_key="decrypt_db")
        else:
            runtime("单库解密失败", stage_key="decrypt_db", severity="warning")
            error("单库解密失败", stage_key="decrypt_db")
        return logs
    if event_name == "client_decrypt_tree_done":
        runtime("整批数据库解密完成", stage_key="decrypt_tree")
        if int(payload.get("failed_count") or 0) > 0:
            error("整批数据库存在失败项", stage_key="decrypt_tree")
        return logs
    if event_name == "client_extract_failed":
        error("采集或解密失败", stage_key=str(payload.get("stage") or "extract_failed"))
        return logs
    if event_name == "client_media_missing":
        error("图片文件缺失", stage_key="media_missing")
        return logs
    if event_name == "client_media_skipped":
        error("图片未上传", stage_key="media_skipped")
        return logs
    if event_name == "client_wechat_restart_started":
        runtime("开始重启微信", stage_key="wechat_restart")
        return logs
    if event_name == "client_wechat_restart_result":
        runtime("微信重启结果", stage_key="wechat_restart")
        if payload.get("success") is False:
            error("微信重启失败", stage_key="wechat_restart")
        return logs
    if event_name == "client_wechat_login_status":
        runtime("微信登录状态更新", stage_key="wechat_login")
        return logs
    if event_name == "client_scan_finished":
        runtime("本轮结束", stage_key="scan_finished")
        return logs
    if event_name == "server_error_reported":
        error("客户端上报错误", stage_key="server_error")
        return logs
    if event_name == "server_unauthorized":
        error("请求鉴权失败", stage_key="unauthorized")
        return logs
    return logs


def format_monitor_log_row(row):
    payload_json = row.get("payload_json", "{}")
    try:
        payload = json.loads(payload_json or "{}")
    except json.JSONDecodeError:
        payload = {}

    last_event_at = row.get("last_event_at") or row.get("first_event_at") or row.get("created_at")
    updated_at = row.get("updated_at") or row.get("created_at")
    return {
        "id": row.get("id"),
        "log_type": row.get("log_type", "runtime"),
        "event_name": row.get("event_name", ""),
        "title": row.get("title", "") or row.get("event_name", "") or "未知日志",
        "details": row.get("details", ""),
        "source": row.get("source", ""),
        "source_label": LOG_SOURCE_LABELS.get(row.get("source", ""), row.get("source", "") or "未知来源"),
        "session_id": row.get("session_id", ""),
        "client_source": row.get("client_source", ""),
        "client_ip": row.get("client_ip", "") or payload.get("client_ip", ""),
        "severity": row.get("severity", "info"),
        "stage_key": row.get("stage_key", ""),
        "occurrence_count": int(row.get("occurrence_count") or 1),
        "payload_json": payload_json,
        "created_at": last_event_at.strftime("%Y-%m-%d %H:%M:%S") if last_event_at else "",
        "updated_at": updated_at.strftime("%Y-%m-%d %H:%M:%S") if updated_at else "",
    }


def upsert_monitor_log(conn, monitor_log):
    if not monitor_log:
        return None, False

    dedupe_key = str(monitor_log.get("dedupe_key") or "").strip()
    aggregate_window_seconds = int(monitor_log.get("aggregate_window_seconds") or 0)
    event_time = monitor_log.get("event_time") or datetime.now()

    with conn.cursor() as cursor:
        if dedupe_key and aggregate_window_seconds > 0:
            lookback_from = event_time - timedelta(seconds=aggregate_window_seconds)
            cursor.execute(
                """
                SELECT id, occurrence_count
                FROM monitor_logs
                WHERE log_type = %s
                  AND session_id = %s
                  AND client_source = %s
                  AND dedupe_key = %s
                  AND last_event_at >= %s
                ORDER BY id DESC
                LIMIT 1
                """,
                (
                    monitor_log["log_type"],
                    monitor_log["session_id"],
                    monitor_log["client_source"],
                    dedupe_key,
                    lookback_from,
                ),
            )
            existing = cursor.fetchone()
            if existing:
                next_count = int(existing.get("occurrence_count") or 1) + 1
                next_details = compose_monitor_details(monitor_log.get("details", ""), next_count)
                cursor.execute(
                    """
                    UPDATE monitor_logs
                    SET
                        details = %s,
                        occurrence_count = %s,
                        payload_json = %s,
                        client_ip = CASE
                            WHEN %s <> '' THEN %s
                            ELSE client_ip
                        END,
                        last_event_at = %s
                    WHERE id = %s
                    """,
                    (
                        next_details,
                        next_count,
                        monitor_log["payload_json"],
                        monitor_log["client_ip"],
                        monitor_log["client_ip"],
                        event_time,
                        existing["id"],
                    ),
                )
                cursor.execute("SELECT * FROM monitor_logs WHERE id = %s", (existing["id"],))
                return cursor.fetchone(), True

        cursor.execute(
            """
            INSERT INTO monitor_logs (
                log_type, event_name, source, session_id, client_source, client_ip,
                severity, stage_key, title, details, dedupe_key, occurrence_count,
                payload_json, first_event_at, last_event_at
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
            """,
            (
                monitor_log["log_type"],
                monitor_log["event_name"],
                monitor_log["source"],
                monitor_log["session_id"],
                monitor_log["client_source"],
                monitor_log["client_ip"],
                monitor_log["severity"],
                monitor_log["stage_key"],
                monitor_log["title"],
                compose_monitor_details(monitor_log["details"], 1),
                dedupe_key,
                1,
                monitor_log["payload_json"],
                event_time,
                event_time,
            ),
        )
        row_id = cursor.lastrowid
        cursor.execute("SELECT * FROM monitor_logs WHERE id = %s", (row_id,))
        return cursor.fetchone(), False


def prune_monitor_logs(conn):
    with conn.cursor() as cursor:
        for log_type, limit in (
            ("runtime", MONITOR_RUNTIME_LOG_LIMIT),
            ("error", MONITOR_ERROR_LOG_LIMIT),
        ):
            cursor.execute(
                """
                DELETE FROM monitor_logs
                WHERE id NOT IN (
                    SELECT id FROM (
                        SELECT id
                        FROM monitor_logs
                        WHERE log_type = %s
                        ORDER BY id DESC
                        LIMIT %s
                    ) AS recent_logs
                )
                  AND log_type = %s
                """,
                (log_type, max(limit, 1), log_type),
            )


def load_monitor_logs(
    *,
    log_type="runtime",
    session_id="",
    client_source="",
    since_id=0,
    updated_after="",
    limit=None,
):
    limit = parse_int(limit, MONITOR_TIMELINE_PAGE_LIMIT, minimum=1, maximum=500)
    since_id = parse_int(since_id, 0, minimum=0)
    with get_db() as conn:
        with conn.cursor() as cursor:
            base_where = ["log_type = %s"]
            base_params = [log_type]
            if session_id:
                base_where.append("session_id = %s")
                base_params.append(session_id)
            if client_source:
                base_where.append("client_source = %s")
                base_params.append(client_source)

            cursor.execute(
                f"""
                SELECT *
                FROM monitor_logs
                WHERE {' AND '.join(base_where)}
                  AND id > %s
                ORDER BY id DESC
                LIMIT %s
                """,
                (*base_params, since_id, limit),
            )
            new_rows = list(cursor.fetchall())

            updated_rows = []
            if updated_after:
                cursor.execute(
                    f"""
                    SELECT *
                    FROM monitor_logs
                    WHERE {' AND '.join(base_where)}
                      AND updated_at > %s
                      AND id <= %s
                    ORDER BY id DESC
                    LIMIT %s
                    """,
                    (*base_params, updated_after, since_id or 9223372036854775807, limit),
                )
                updated_rows = list(cursor.fetchall())

            if since_id <= 0:
                cursor.execute(
                    f"""
                    SELECT *
                    FROM monitor_logs
                    WHERE {' AND '.join(base_where)}
                    ORDER BY id DESC
                    LIMIT %s
                    """,
                    (*base_params, limit),
                )
                new_rows = list(cursor.fetchall())
                updated_rows = []

            cursor.execute(
                f"""
                SELECT id
                FROM monitor_logs
                WHERE {' AND '.join(base_where)}
                ORDER BY id DESC
                LIMIT 1
                """,
                base_params,
            )
            latest_row = cursor.fetchone()

    latest_id = int((latest_row or {}).get("id") or 0)
    return {
        "events": [format_monitor_log_row(row) for row in reversed(new_rows)],
        "updated_events": [format_monitor_log_row(row) for row in reversed(updated_rows)],
        "latest_id": latest_id,
        "synced_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "log_type": log_type,
    }


def load_messages(limit=5000, offset=0, session_id="", client_source=""):
    limit = parse_int(limit, 5000, minimum=1, maximum=10000)
    offset = parse_int(offset, 0, minimum=0)
    where_clauses, params = build_scope_where(session_id=session_id, client_source=client_source)
    with get_db() as conn:
        with conn.cursor() as cursor:
            where_sql = f"WHERE {' AND '.join(where_clauses)}" if where_clauses else ""
            cursor.execute(
                f"""
                SELECT
                    session_id, client_source, client_ip,
                    wxid, nickname, sender, content, create_time, is_sender,
                    avatar, msg_type, msg_sub_type,
                    media_type, media_mime, media_name, media_data
                FROM messages
                {where_sql}
                ORDER BY create_time ASC, id ASC
                LIMIT %s OFFSET %s
                """,
                (*params, limit, offset),
            )
            return list(cursor.fetchall())


def count_messages(session_id="", client_source=""):
    where_clauses, params = build_scope_where(session_id=session_id, client_source=client_source)
    with get_db() as conn:
        with conn.cursor() as cursor:
            where_sql = f"WHERE {' AND '.join(where_clauses)}" if where_clauses else ""
            cursor.execute(f"SELECT COUNT(*) AS total FROM messages {where_sql}", params)
            return cursor.fetchone()["total"]


def load_contacts(limit=2000, offset=0, session_id="", client_source=""):
    limit = parse_int(limit, 2000, minimum=1, maximum=10000)
    offset = parse_int(offset, 0, minimum=0)
    where_clauses, params = build_scope_where(session_id=session_id, client_source=client_source)
    with get_db() as conn:
        with conn.cursor() as cursor:
            where_sql = f"WHERE {' AND '.join(where_clauses)}" if where_clauses else ""
            cursor.execute(
                f"""
                SELECT
                    session_id, client_source, client_ip,
                    wxid, alias, remark, nick_name, display_name,
                    avatar, source_updated_at, extra_json, updated_at
                FROM contacts
                {where_sql}
                ORDER BY
                    CASE WHEN display_name <> '' THEN 0 ELSE 1 END,
                    display_name ASC,
                    wxid ASC
                LIMIT %s OFFSET %s
                """,
                (*params, limit, offset),
            )
            return list(cursor.fetchall())


def count_contacts(session_id="", client_source=""):
    where_clauses, params = build_scope_where(session_id=session_id, client_source=client_source)
    with get_db() as conn:
        with conn.cursor() as cursor:
            where_sql = f"WHERE {' AND '.join(where_clauses)}" if where_clauses else ""
            cursor.execute(f"SELECT COUNT(*) AS total FROM contacts {where_sql}", params)
            return cursor.fetchone()["total"]


def load_favorites(limit=1000, offset=0, session_id="", client_source=""):
    limit = parse_int(limit, 1000, minimum=1, maximum=5000)
    offset = parse_int(offset, 0, minimum=0)
    where_clauses, params = build_scope_where(session_id=session_id, client_source=client_source)
    with get_db() as conn:
        with conn.cursor() as cursor:
            where_sql = f"WHERE {' AND '.join(where_clauses)}" if where_clauses else ""
            cursor.execute(
                f"""
                SELECT
                    session_id, client_source, client_ip,
                    source_table, source_id, title, summary,
                    item_type, item_sub_type, source_updated_at,
                    data_json, updated_at
                FROM favorites
                {where_sql}
                ORDER BY source_updated_at DESC, id DESC
                LIMIT %s OFFSET %s
                """,
                (*params, limit, offset),
            )
            return list(cursor.fetchall())


def count_favorites(session_id="", client_source=""):
    where_clauses, params = build_scope_where(session_id=session_id, client_source=client_source)
    with get_db() as conn:
        with conn.cursor() as cursor:
            where_sql = f"WHERE {' AND '.join(where_clauses)}" if where_clauses else ""
            cursor.execute(f"SELECT COUNT(*) AS total FROM favorites {where_sql}", params)
            return cursor.fetchone()["total"]


def save_messages(data):
    with get_db() as conn:
        added = bulk_insert_messages(conn, data)
        conn.commit()
        return added


def load_client_sessions(limit=20):
    limit = parse_int(limit, 20, minimum=1, maximum=100)
    with get_db() as conn:
        with conn.cursor() as cursor:
            cursor.execute(
                """
                SELECT
                    session_id, client_source, client_ip,
                    last_heartbeat, decrypt_ok, wechat_logged_in,
                    last_error, last_event_name, last_event_at,
                    updated_at
                FROM client_sessions
                ORDER BY
                    COALESCE(last_event_at, updated_at) DESC,
                    updated_at DESC
                LIMIT %s
                """,
                (limit,),
            )
            rows = list(cursor.fetchall())

    sessions = []
    for row in rows:
        sessions.append(
            {
                "session_id": row.get("session_id", ""),
                "client_source": row.get("client_source", ""),
                "client_ip": row.get("client_ip", ""),
                "last_heartbeat": int(row.get("last_heartbeat") or 0),
                "decrypt_ok": bool(row.get("decrypt_ok")) if row.get("decrypt_ok") is not None else False,
                "wechat_logged_in": bool(row.get("wechat_logged_in")) if row.get("wechat_logged_in") is not None else False,
                "last_error": row.get("last_error") or "",
                "last_event_name": row.get("last_event_name") or "",
                "last_event_at": row["last_event_at"].strftime("%Y-%m-%d %H:%M:%S") if row.get("last_event_at") else "",
                "updated_at": row["updated_at"].strftime("%Y-%m-%d %H:%M:%S") if row.get("updated_at") else "",
            }
        )
    return sessions


def load_status(session_id="", client_source=""):
    with get_db() as conn:
        with conn.cursor() as cursor:
            cursor.execute(
                """
                SELECT last_heartbeat, decrypt_ok, wechat_logged_in, updated_at
                FROM monitor_status
                WHERE id = 1
                """
            )
            row = cursor.fetchone()
            session_where, session_params = build_scope_where(session_id=session_id, client_source=client_source)
            session_sql = f"WHERE {' AND '.join(session_where)}" if session_where else ""
            cursor.execute(
                f"""
                SELECT
                    session_id, client_source, client_ip,
                    last_heartbeat, decrypt_ok, wechat_logged_in,
                    last_error, last_event_name, last_event_at,
                    updated_at
                FROM client_sessions
                {session_sql}
                ORDER BY
                    COALESCE(last_event_at, updated_at) DESC,
                    updated_at DESC
                LIMIT 1
                """,
                session_params,
            )
            selected_session = cursor.fetchone()
            cursor.execute(
                """
                SELECT
                    session_id, client_source, client_ip,
                    last_heartbeat, decrypt_ok, wechat_logged_in,
                    last_error, last_event_name, last_event_at,
                    updated_at
                FROM client_sessions
                ORDER BY
                    COALESCE(last_event_at, updated_at) DESC,
                    updated_at DESC
                LIMIT 1
                """
            )
            latest_session = cursor.fetchone()
            current_session = selected_session or latest_session
            current_session_id = normalize_session_id((current_session or {}).get("session_id"))
            current_client_source = normalize_client_source((current_session or {}).get("client_source"), default="") if current_session else ""
            monitor_where = []
            monitor_params = []
            if current_session_id:
                monitor_where.append("session_id = %s")
                monitor_params.append(current_session_id)
            if current_client_source:
                monitor_where.append("client_source = %s")
                monitor_params.append(current_client_source)
            monitor_sql = f"AND {' AND '.join(monitor_where)}" if monitor_where else ""

            cursor.execute(
                f"""
                SELECT *
                FROM monitor_logs
                WHERE log_type = 'runtime'
                {monitor_sql}
                ORDER BY id DESC
                LIMIT 120
                """,
                monitor_params,
            )
            runtime_logs = list(cursor.fetchall())
            cursor.execute(
                f"""
                SELECT *
                FROM monitor_logs
                WHERE log_type = 'error'
                {monitor_sql}
                ORDER BY id DESC
                LIMIT 50
                """,
                monitor_params,
            )
            error_logs = list(cursor.fetchall())
            sessions = load_client_sessions()

    if not row:
        return {
            "last_heartbeat": 0,
            "errors": [],
            "decrypt_ok": False,
            "wechat_logged_in": False,
            "heartbeat_timeout_seconds": HEARTBEAT_TIMEOUT_SECONDS,
            "worker_alive": False,
            "last_event_at": "",
            "last_event_summary": "",
            "last_scan_result_summary": "",
            "last_push_result_summary": "",
            "last_client_session_id": "",
            "last_client_source": "",
            "last_client_event_at": "",
            "current_session_id": "",
            "current_client_source": "",
            "current_client_ip": "",
            "sessions": [],
        }

    now_ts = int(datetime.now().timestamp())
    session_heartbeat = int((current_session or {}).get("last_heartbeat") or 0)
    heartbeat = session_heartbeat or (row.get("last_heartbeat") or 0)
    worker_alive = heartbeat > 0 and (now_ts - int(heartbeat)) < HEARTBEAT_TIMEOUT_SECONDS
    runtime_items = [format_monitor_log_row(row) for row in runtime_logs]
    error_items = [format_monitor_log_row(row) for row in error_logs]
    last_event = runtime_items[0] if runtime_items else None
    last_scan_event = next((item for item in runtime_items if item.get("stage_key") == "scan_finished"), None)
    last_push_event = next(
        (
            item
            for item in runtime_items
            if item.get("stage_key") in {"messages_push", "favorites_push", "contacts_push"}
        ),
        None,
    )
    last_client_event = last_event

    return {
        "last_heartbeat": heartbeat,
        "decrypt_ok": bool((current_session or {}).get("decrypt_ok")) if current_session and current_session.get("decrypt_ok") is not None else bool(row.get("decrypt_ok")),
        "wechat_logged_in": bool((current_session or {}).get("wechat_logged_in")) if current_session and current_session.get("wechat_logged_in") is not None else bool(row.get("wechat_logged_in")),
        "heartbeat_timeout_seconds": HEARTBEAT_TIMEOUT_SECONDS,
        "worker_alive": worker_alive,
        "last_event_at": (last_event or {}).get("created_at", ""),
        "last_event_summary": (last_event or {}).get("details", ""),
        "last_scan_result_summary": (last_scan_event or {}).get("details", ""),
        "last_push_result_summary": (last_push_event or {}).get("details", ""),
        "last_client_session_id": current_session_id or (last_client_event or {}).get("session_id", ""),
        "last_client_source": (last_client_event or {}).get("source", ""),
        "last_client_event_at": (last_client_event or {}).get("created_at", ""),
        "current_session_id": current_session_id,
        "current_client_source": current_client_source,
        "current_client_ip": (current_session or {}).get("client_ip", "") or (last_client_event or {}).get("client_ip", ""),
        "sessions": sessions,
        "errors": [
            {
                "id": item.get("id"),
                "time": item.get("created_at", ""),
                "message": item.get("title", ""),
                "details": item.get("details", ""),
                "session_id": item.get("session_id", ""),
                "client_source": item.get("client_source", ""),
                "client_ip": item.get("client_ip", ""),
                "occurrence_count": item.get("occurrence_count", 1),
            }
            for item in reversed(error_items)
        ],
    }


def load_events(limit=100, session_id="", client_source=""):
    return load_monitor_logs(
        log_type="runtime",
        session_id=session_id,
        client_source=client_source,
        limit=limit,
    )["events"]


def format_event(row):
    event_name = row.get("event_name", "")
    source = row.get("source", "")
    payload_json = row.get("payload_json", "{}")
    try:
        payload = json.loads(payload_json or "{}")
    except json.JSONDecodeError:
        payload = {}

    event_titles = {
        "client_scan_started": "开始扫描微信",
        "client_scan_finished": "本轮扫描结束",
        "client_wechat_detected": "检测到当前微信进程",
        "client_wechat_restart_started": "开始重启微信",
        "client_wechat_restart_result": "微信重启结果",
        "client_wechat_process_detected": "检测到新的微信进程",
        "client_wechat_login_status": "微信登录状态更新",
        "client_decrypt_started": "开始执行解密",
        "client_decrypt_progress": "解密仍在进行",
        "client_decrypt_slow": "解密耗时偏长",
        "client_decrypt_finished": "微信数据库解密成功",
        "client_extract_failed": "采集或解密失败",
        "client_v4_data_dir_result": "v4 聊天目录识别结果",
        "client_chatlog_key_attempt": "数据库密钥尝试结果",
        "client_chatlog_key_result": "数据库密钥获取结果",
        "client_disk_pipeline_started": "临时落盘链路开始",
        "client_disk_pipeline_result": "临时落盘链路结果",
        "client_incremental_filter_result": "增量过滤结果",
        "client_disk_cleanup_started": "开始清理临时明文",
        "client_disk_cleanup_result": "临时明文清理结果",
        "client_memory_pipeline_started": "内存直传开始",
        "client_memory_db_progress": "内存解密进度",
        "client_memory_db_released": "内存数据库已释放",
        "client_memory_pipeline_result": "内存直传结果",
        "client_weflow_wcdb_export_attempt": "开始解密数据库",
        "client_weflow_wcdb_export_result": "数据库解密结果",
        "client_wechat_decrypt_export_result": "数据库解密结果",
        "client_weflow_result": "WeFlow 本地服务结果",
        "client_push_started": "开始上传聊天记录",
        "client_push_batch_started": "上传批次开始",
        "client_push_batch_result": "上传批次结果",
        "client_push_result": "聊天记录上传成功",
        "client_push_failed": "聊天记录上传失败",
        "client_contacts_export_result": "联系人导出结果",
        "client_contacts_push_result": "联系人上传结果",
        "client_wal_merge_skipped": "跳过 WAL 合并",
        "client_decrypt_db_result": "单库解密结果",
        "client_decrypt_tree_done": "整批解密结果",
        "client_favorites_export_result": "收藏导出结果",
        "client_favorites_push_result": "收藏上传结果",
        "client_avatar_scan_result": "头像扫描结果",
        "client_media_missing": "图片文件未找到",
        "client_media_loaded": "图片文件已读取",
        "client_media_skipped": "图片未上传",
        "server_messages_received": "服务器收到聊天记录",
        "server_contacts_received": "服务器收到联系人",
        "server_favorites_received": "服务器收到收藏",
        "server_status_updated": "服务器收到状态上报",
        "server_error_reported": "客户端上报错误",
        "server_unauthorized": "上传密钥不正确",
    }

    title = event_titles.get(event_name, event_name or "未知事件")
    details = describe_event(event_name, payload)
    source_label = {
        "client_cs": "Windows 桌宠",
        "client_py": "Windows 采集脚本",
        "server": "服务器",
        "web": "网页",
    }.get(source, source or "未知来源")

    return {
        "event_name": event_name,
        "title": title,
        "details": details,
        "source": source,
        "source_label": source_label,
        "session_id": row.get("session_id", ""),
        "client_ip": row.get("client_ip", "") or payload.get("client_ip", ""),
        "payload_json": payload_json,
        "created_at": row["created_at"].strftime("%Y-%m-%d %H:%M:%S") if row.get("created_at") else "",
    }


def describe_event(event_name, payload):
    stage_map = {
        "check_and_push": "整轮扫描与上传",
        "config": "配置检查",
        "startup": "WeFlow 启动",
        "bootstrap": "WeFlow 后台预配置",
        "history_sync": "WeFlow 历史同步",
        "decrypt_bootstrap": "解密器启动准备",
        "decrypt_process": "解密执行过程",
        "decrypt_dir": "解密目录读取",
        "sqlite_read": "解密后数据库读取",
        "chatlog_export": "chatlog 导出结果读取",
    }
    reason_map = {
        "decrypt_exe_missing": "发布包内没有找到解密程序",
        "decrypt_timeout": "解密超时",
        "decrypt_hard_timeout": "超过硬超时仍未完成，已终止",
        "decrypt_dir_missing": "没有找到解密输出目录",
        "data_dir_missing": "没有识别到聊天数据目录",
        "local_image_path_not_found": "没有找到本地图片文件",
        "file_too_large": "图片文件过大",
        "missing_access_token": "没有配置 WeFlow 访问令牌",
        "weflow_exe_missing": "发布包内没有找到 WeFlow 程序",
        "service_unavailable": "WeFlow 本地服务没有成功启动",
        "bootstrap_not_ready": "当前没有拿到可供 WeFlow 静默启动的完整配置",
        "decrypt_meta_missing": "本地解密元数据没有生成出来",
        "decrypt_meta_incomplete": "本地解密元数据不完整，缺少目录、账号或密钥",
        "weflow_db_path_missing": "没有推导出 WeFlow 所需的数据库根目录",
        "write_weflow_config_failed": "写入 WeFlow 本地配置失败",
        "wechat_executable_missing": "没有拿到可重启的微信程序路径",
        "wechat_restart_launch_failed": "重新启动微信失败",
        "wechat_new_process_timeout": "自动重启微信后，等待新微信进程超时",
        "favorite_db_missing": "没有找到收藏数据库",
        "favorite_db_read_failed": "读取收藏数据库失败",
        "no_favorites_exported": "本轮没有可上传的收藏",
    }

    def summarize_paths(values, limit=4):
        items = []
        for value in values or []:
            text = str(value or "").strip()
            if text and text not in items:
                items.append(text)

        if not items:
            return ""

        shown = items[:limit]
        text = "；".join(shown)
        if len(items) > limit:
            text += f"；另外还有 {len(items) - limit} 个"
        return text

    if event_name == "client_scan_started":
        return f"后台开始检查微信，扫描间隔 {payload.get('interval_seconds', '-')} 秒。"
    if event_name == "client_wechat_detected":
        return (
            f"检测到当前微信进程：{payload.get('process_name', '-')}"
            f"（PID {payload.get('pid', '-')})。"
        )
    if event_name == "client_wechat_restart_started":
        trigger_error = normalize_reason_text(payload.get("trigger_error", ""))
        if trigger_error:
            return (
                f"当前这轮快速采集没有顺利完成，准备自动重启微信后重试；"
                f"旧 PID {payload.get('previous_pid', '-')}"
                f"；快速尝试窗口 {payload.get('quick_try_seconds', '-')} 秒；"
                f"触发原因：{trigger_error}"
            )
        return (
            f"当前这轮快速采集没有顺利完成，准备自动重启微信后重试；"
            f"旧 PID {payload.get('previous_pid', '-')}"
            f"；快速尝试窗口 {payload.get('quick_try_seconds', '-')} 秒。"
        )
    if event_name == "client_wechat_restart_result":
        if payload.get("success"):
            return (
                f"微信已成功重启，新 PID {payload.get('pid', '-')}"
                f"；等待耗时 {payload.get('wait_elapsed_ms', 0)} 毫秒。"
            )
        return f"微信重启失败：{payload.get('error_message') or payload.get('reason') or '未知原因'}"
    if event_name == "client_wechat_process_detected":
        return (
            f"已捕获新的微信进程：{payload.get('process_name', '-')}"
            f"（PID {payload.get('pid', '-')})，"
            f"探测耗时 {payload.get('wait_elapsed_ms', 0)} 毫秒。"
        )
    if event_name == "client_wechat_login_status":
        return "检测到微信已登录。" if payload.get("logged_in") else "没有检测到微信登录。"
    if event_name == "client_decrypt_started":
        return (
            f"已启动本地解密进程，PID {payload.get('pid', '-')}"
            f"；软超时 {payload.get('soft_timeout_seconds', '-')} 秒，"
            f"硬超时 {payload.get('hard_timeout_seconds', '-')} 秒。"
        )
    if event_name == "client_decrypt_progress":
        if payload.get("stage") == "timeout_extended_after_key_ready":
            return (
                f"已确认拿到数据库密钥，继续沿用当前微信进程完成导出；"
                f"本轮超时窗口已从 {payload.get('original_hard_timeout_seconds', '-')}"
                f" 秒延长到 {payload.get('hard_timeout_seconds', '-')} 秒。"
            )
        return (
            f"解密仍在进行中，已运行 {payload.get('elapsed_seconds', 0)} 秒，"
            f"软超时 {payload.get('soft_timeout_seconds', '-')} 秒，"
            f"硬超时 {payload.get('hard_timeout_seconds', '-')} 秒。"
        )
    if event_name == "client_decrypt_slow":
        return (
            f"解密已运行 {payload.get('elapsed_seconds', 0)} 秒，已超过软超时"
            f" {payload.get('soft_timeout_seconds', '-')} 秒；继续等待，"
            f"直到硬超时 {payload.get('hard_timeout_seconds', '-')} 秒。"
        )
    if event_name == "client_decrypt_finished":
        return f"解密完成，输出目录：{payload.get('decrypt_dir', '-')}"
    if event_name == "client_extract_failed":
        stage = stage_map.get(payload.get("stage"), payload.get("stage") or "未知阶段")
        reason_code = payload.get("reason")
        reason_text = reason_map.get(reason_code, reason_code) or ""
        error_message = str(payload.get("error_message") or "").strip()
        if reason_text and error_message and error_message != reason_text and error_message != reason_code:
            return f"失败阶段：{stage}；原因：{reason_text}；详情：{error_message}"
        reason = reason_text or error_message or "未提供错误原因"
        return f"失败阶段：{stage}；原因：{reason}"
    if event_name == "client_v4_data_dir_result":
        if payload.get("success"):
            source = payload.get("source") or "unknown"
            source_text = {
                "open_files": "微信进程已打开数据库",
                "scan_candidates": "磁盘候选目录扫描",
            }.get(source, source)
            parts = [
                f"已识别到 v4 聊天目录：{payload.get('data_dir', '-')}",
                f"来源：{source_text}",
            ]
            candidate_text = summarize_paths(payload.get("candidate_dirs"), limit=3)
            if candidate_text:
                parts.append(
                    f"候选目录共 {payload.get('candidate_count', 0)} 个；前几项：{candidate_text}"
                )
            configured_text = summarize_paths(payload.get("configured_roots"), limit=2)
            if configured_text:
                parts.append(
                    f"微信配置根路径共 {payload.get('configured_root_count', 0)} 个；{configured_text}"
                )
            search_root_text = summarize_paths(payload.get("search_roots"), limit=3)
            if search_root_text:
                parts.append(
                    f"本轮扫描根路径共 {payload.get('search_root_count', 0)} 个；{search_root_text}"
                )
            return "。".join(parts) + "。"
        source = payload.get("source") or "unknown"
        source_text = {
            "scan_not_found": "已做目录扫描但没有命中",
            "open_files": "微信进程句柄识别",
        }.get(source, source)
        parts = [
            f"未识别到 v4 聊天目录；原因：{reason_map.get(payload.get('reason'), payload.get('reason', '未知原因'))}",
            f"当前阶段：{source_text}",
        ]
        configured_text = summarize_paths(payload.get("configured_roots"), limit=2)
        if configured_text:
            parts.append(
                f"微信配置根路径共 {payload.get('configured_root_count', 0)} 个；{configured_text}"
            )
        search_root_text = summarize_paths(payload.get("search_roots"), limit=4)
        if search_root_text:
            parts.append(
                f"本轮扫描根路径共 {payload.get('search_root_count', 0)} 个；{search_root_text}"
            )
        drive_root_text = summarize_paths(payload.get("drive_roots"), limit=6)
        if drive_root_text:
            parts.append(
                f"参与兜底扫描的盘符共 {payload.get('drive_root_count', 0)} 个；{drive_root_text}"
            )
        return "。".join(parts) + "。"
    if event_name == "client_chatlog_key_attempt":
        attempt_index = payload.get("attempt_index", 0)
        attempt_total = payload.get("attempt_total", 0)
        data_dir = payload.get("data_dir", "-")
        if payload.get("has_key"):
            return f"第 {attempt_index}/{attempt_total} 次尝试已拿到密钥，目录：{data_dir}"
        reason = payload.get("error_message") or f"退出码 {payload.get('exit_code', '-')}"
        return f"第 {attempt_index}/{attempt_total} 次尝试未拿到密钥，目录：{data_dir}；原因：{reason}"
    if event_name == "client_chatlog_key_result":
        if payload.get("success"):
            return f"数据库密钥获取成功，密钥长度 {payload.get('key_length', '-')}，使用目录：{payload.get('selected_data_dir', '-')}"
        return f"数据库密钥获取失败：{payload.get('error_message', '未知原因')}"
    if event_name == "client_disk_pipeline_started":
        return (
            f"已切换到临时落盘链路；账号目录：{payload.get('data_dir', '-')}"
            f"；数据库根目录：{payload.get('db_storage_dir', '-')}"
            f"；上传后会自动清理明文。"
        )
    if event_name == "client_disk_pipeline_result":
        result_map = {
            "exported": "临时落盘导出完成",
            "decrypt_failed": "临时落盘解密失败",
        }
        result = result_map.get(payload.get("result"), payload.get("result", "未知结果"))
        return (
            f"{result}；导出消息 {payload.get('message_count', 0)} 条，"
            f"成功解密数据库 {payload.get('decrypted_db_count', 0)} 个，"
            f"失败 {payload.get('failed_db_count', 0)} 个。"
        )
    if event_name == "client_wal_merge_skipped":
        reason = str(payload.get("reason") or "").strip()
        db_rel = payload.get("db_rel", "-")
        if reason == "wechat_running":
            return f"检测到微信进程仍在运行，已跳过 {db_rel} 的 WAL 合并；当前只使用主库，可能缺少最新几条数据。"
        if reason == "source_modified":
            return f"{db_rel} 在解密阶段仍有写入，已跳过 WAL 合并，避免把明文库合坏。"
        return f"{db_rel} 已跳过 WAL 合并；原因：{normalize_reason_text(reason or '未知原因')}"
    if event_name == "client_decrypt_db_result":
        db_rel = payload.get("db_rel", "-")
        result = str(payload.get("result") or "").strip()
        if result == "success":
            wal_applied = int(payload.get("wal_applied") or 0)
            wal_present = bool(payload.get("wal_present"))
            if wal_applied > 0:
                return f"{db_rel} 解密成功，并已合并 WAL {wal_applied} 页。"
            if wal_present:
                return f"{db_rel} 解密成功，但本轮没有把 WAL 页面真正合进去。"
            return f"{db_rel} 解密成功，当前只使用主库内容。"
        if result == "skipped":
            return f"{db_rel} 本轮跳过解密；原因：{normalize_reason_text(payload.get('reason') or '没有拿到对应密钥')}"
        return f"{db_rel} 解密失败；原因：{normalize_reason_text(payload.get('reason') or 'HMAC 校验或库解密失败')}"
    if event_name == "client_decrypt_tree_done":
        return (
            f"整批数据库处理完成；成功 {payload.get('success_count', 0)} 个，"
            f"失败 {payload.get('failed_count', 0)} 个，"
            f"跳过 {payload.get('skipped_count', 0)} 个，"
            f"WAL 已合并 {payload.get('wal_applied_count', 0)} 个，"
            f"跳过 {payload.get('wal_skipped_count', 0)} 个。"
        )
    if event_name == "client_incremental_filter_result":
        return (
            f"本轮共读取 {payload.get('extracted_count', 0)} 条；"
            f"需要上传 {payload.get('new_count', 0)} 条；"
            f"跳过已同步 {payload.get('skipped_count', 0)} 条。"
        )
    if event_name == "client_disk_cleanup_started":
        return f"开始清理临时明文目录：{payload.get('decrypt_dir', '-')}"
    if event_name == "client_disk_cleanup_result":
        if payload.get("success"):
            return f"临时明文清理完成；删除 {payload.get('removed_count', 0)} 项。"
        return (
            f"临时明文清理存在失败；已删除 {payload.get('removed_count', 0)} 项，"
            f"失败 {payload.get('failed_count', 0)} 项。"
        )
    if event_name == "client_memory_pipeline_started":
        return (
            f"已切换到内存直传链路；账号目录：{payload.get('data_dir', '-')}"
            f"；数据库根目录：{payload.get('db_storage_dir', '-')}"
            f"；本轮不落盘明文。"
        )
    if event_name == "client_memory_db_progress":
        stage = payload.get("stage") or "unknown"
        if stage == "contact_map":
            return f"正在内存读取联系人库：{payload.get('db_rel', '-')}"
        if stage == "contact_map_failed":
            return (
                f"联系人库内存读取失败，已降级继续处理消息库：{payload.get('db_rel', '-')}"
                f"；原因：{normalize_reason_text(payload.get('error_message', '未知原因'))}"
            )
        if stage == "decrypting_message_db":
            return (
                f"正在内存解密消息库 {payload.get('db_index', '-')}/{payload.get('db_total', '-')}"
                f"：{payload.get('db_rel', '-')}"
            )
        if stage == "decrypt_failed":
            return f"某个消息库内存解密失败：{payload.get('db_rel', '-')}"
        if stage == "open_failed":
            return (
                f"某个消息库内存读取失败：{payload.get('db_rel', '-')}"
                f"；原因：{normalize_reason_text(payload.get('error_message', '未知原因'))}"
            )
        return json.dumps(payload or {}, ensure_ascii=False)
    if event_name == "client_memory_db_released":
        return (
            f"已释放消息库 {payload.get('db_index', '-')}/{payload.get('db_total', '-')}"
            f"：{payload.get('db_rel', '-')}"
        )
    if event_name == "client_memory_pipeline_result":
        result_map = {
            "pushed": "内存直传完成",
            "no_messages": "没有读取到可上传的聊天记录",
            "push_failed": "内存直传失败",
            "memory_export_failed": "内存解密读取失败",
        }
        result = result_map.get(payload.get("result"), payload.get("result", "未知结果"))
        return (
            f"{result}；消息 {payload.get('message_count', 0)} 条，"
            f"上传 {payload.get('uploaded_count', 0)} 条，新增 {payload.get('added_count', 0)} 条；"
            f"峰值内存库大小 {payload.get('peak_db_bytes', 0)} 字节。"
        )
    if event_name == "client_weflow_wcdb_export_attempt":
        return (
            f"已拿到数据库密钥，开始解密并读取数据库；目录：{payload.get('data_dir', '-')}"
            f"；会话上限 {payload.get('session_limit', '-')}"
            f"；每会话消息上限 {payload.get('message_limit', '-')}"
            f"。"
        )
    if event_name == "client_weflow_wcdb_export_result":
        if payload.get("success"):
            text = (
                f"数据库解密完成：会话 {payload.get('session_count', 0)} 个，"
                f"导出消息 {payload.get('message_count', 0)} 条。"
            )
            duration_ms = payload.get("duration_ms")
            warnings = payload.get("warnings") or []
            if duration_ms:
                text += f" 耗时 {duration_ms} ms。"
            if warnings:
                text += f" 附带警告 {len(warnings)} 条。"
            return text

        text = f"数据库解密失败：{payload.get('error_message', '未知原因')}"
        dll_path = payload.get("dll_path")
        session_db_path = payload.get("session_db_path")
        warnings = payload.get("warnings") or []
        tried_paths = payload.get("init_protection_tried_paths") or []
        if dll_path:
            text += f" 动态库：{dll_path}。"
        if session_db_path:
            text += f" session.db：{session_db_path}。"
        if warnings:
            text += f" 警告 {len(warnings)} 条。"
        if tried_paths:
            text += f" InitProtection 共尝试 {len(tried_paths)} 个路径。"
        return text
    if event_name == "client_wechat_decrypt_export_result":
        if payload.get("success"):
            return (
                f"数据库解密导出完成：账号目录 {payload.get('data_dir', '-')}"
                f"；消息 {payload.get('message_count', 0)} 条；"
                f"命中数据库 {payload.get('matched_db_count', 0)} 个；"
                f"成功解密 {payload.get('decrypted_db_count', 0)} 个。"
            )
        return f"数据库解密导出失败：{payload.get('error_message', '未知原因')}"
    if event_name == "client_weflow_result":
        if payload.get("success"):
            if payload.get("action") == "launched":
                return f"WeFlow 已由桌宠自动拉起：{payload.get('exe_path', '-')}"
            if payload.get("action") == "seed_config":
                return (
                    f"WeFlow 后台预配置完成：已写入账号 {payload.get('wxid', '-')}"
                    f" 的静默启动配置，数据库根目录：{payload.get('db_path', '-')}"
                )
            if payload.get("stage") == "history_sync":
                return (
                    f"WeFlow 历史同步完成：会话 {payload.get('session_count', 0)} 个，"
                    f"读取 {payload.get('message_count', 0)} 条，"
                    f"上传 {payload.get('uploaded_count', 0)} 条，"
                    f"新增入库 {payload.get('added_count', 0)} 条。"
                )
            return f"WeFlow 可用，已读取 {payload.get('message_count', 0)} 条消息。"
        stage = stage_map.get(payload.get("stage"), payload.get("stage") or "未知阶段")
        reason = (
            reason_map.get(payload.get("reason"), payload.get("reason"))
            or payload.get("error_message")
            or "未知原因"
        )
        if payload.get("reason") == "weflow_exe_missing":
            return f"WeFlow 启动失败：安装包里没有带上 WeFlow 程序。查找路径：{payload.get('exe_path', '-')}"
        if payload.get("reason") == "service_unavailable":
            return f"WeFlow 启动后仍未连通本地服务。地址：{payload.get('base_url', '-')}"
        if payload.get("reason") == "missing_access_token":
            return "WeFlow 无法使用：本地 API 访问令牌为空。"
        return f"WeFlow 阶段：{stage}；结果：{reason}"
    if event_name == "client_push_started":
        return f"准备上传 {payload.get('message_count', 0)} 条聊天记录。"
    if event_name == "client_push_batch_started":
        return (
            f"开始上传第 {payload.get('batch_index', '-')}/{payload.get('batch_total', '-')}"
            f" 批，当前批次 {payload.get('message_count', 0)} 条。"
        )
    if event_name == "client_push_batch_result":
        if payload.get("success"):
            return (
                f"上传批次成功：第 {payload.get('batch_index', '-')}/{payload.get('batch_total', '-')}"
                f" 批；累计上传 {payload.get('uploaded_count', 0)} 条，新增 {payload.get('added_count', 0)} 条。"
            )
        return (
            f"上传批次失败：第 {payload.get('batch_index', '-')}/{payload.get('batch_total', '-')}"
            f" 批；原因：{payload.get('error_message', '未知原因')}"
        )
    if event_name == "client_push_result":
        return f"上传成功，本次发送 {payload.get('message_count', 0)} 条，新增入库 {payload.get('added_count', 0)} 条。"
    if event_name == "client_push_failed":
        reason = payload.get("error_message") or payload.get("response_text") or payload.get("status_code") or "未知原因"
        return f"上传失败：{reason}"
    if event_name == "client_contacts_export_result":
        return (
            f"联系人导出完成：共 {payload.get('contact_count', 0)} 个联系人，"
            f"带头像 {payload.get('avatar_count', 0)} 个。"
        )
    if event_name == "client_contacts_push_result":
        return (
            f"联系人上传完成：本次发送 {payload.get('contact_count', 0)} 个，"
            f"服务端更新 {payload.get('changed_count', 0)} 个。"
        )
    if event_name == "client_favorites_export_result":
        if payload.get("success") is False:
            reason = reason_map.get(payload.get("reason"), payload.get("reason")) or "未知原因"
            error_message = payload.get("error_message")
            if error_message:
                return f"收藏导出失败：{reason}；详情：{normalize_reason_text(error_message)}"
            return f"收藏导出失败：{reason}"
        if int(payload.get("favorite_count", 0) or 0) == 0:
            table_count = int(payload.get("table_count", 0) or 0)
            table_summaries = payload.get("table_summaries") or []
            if table_count > 0 and table_summaries:
                preview = []
                for item in table_summaries[:3]:
                    if not isinstance(item, dict):
                        continue
                    preview.append(
                        f"{item.get('table_name', '?')}({item.get('sample_row_count', 0)}行)"
                    )
                preview_text = "，".join(preview)
                if preview_text:
                    return (
                        f"收藏导出完成：本轮没有读到任何收藏；"
                        f"已扫描 {table_count} 张表，前几张：{preview_text}。"
                    )
            return "收藏导出完成：本轮没有读到任何收藏。"
        return f"收藏导出完成：共 {payload.get('favorite_count', 0)} 条收藏。"
    if event_name == "client_favorites_push_result":
        if payload.get("skipped"):
            return "收藏上传跳过：本轮没有可上传的收藏。"
        if payload.get("success") is False:
            reason = payload.get("error_message") or payload.get("response_text") or payload.get("status_code") or "未知原因"
            return f"收藏上传失败：{normalize_reason_text(reason)}"
        return (
            f"收藏上传完成：本次发送 {payload.get('favorite_count', 0)} 条，"
            f"服务端更新 {payload.get('changed_count', 0)} 条。"
        )
    if event_name == "client_avatar_scan_result":
        return (
            f"头像扫描完成：命中 {payload.get('avatar_count', 0)} 个联系人头像，"
            f"扫描表 {payload.get('table_count', 0)} 个。"
        )
    if event_name == "client_media_missing":
        return "识别到图片消息，但没有从 Windows 本地消息字段中找到可读取的图片文件路径。"
    if event_name == "client_media_loaded":
        return (
            f"图片已读取：{payload.get('media_name', '-')}"
            f"；大小 {payload.get('media_size', 0)} 字节。"
        )
    if event_name == "client_media_skipped":
        if payload.get("reason") == "file_too_large":
            return f"图片太大，已跳过：{payload.get('file_name', '-')}，大小 {payload.get('file_size', 0)} 字节。"
        if payload.get("reason") == "read_failed":
            return (
                f"Windows 本地图片读取失败："
                f"{normalize_reason_text(payload.get('error_message', '未知原因'))}"
            )
        return f"图片未上传：{payload.get('reason', '未知原因')}"
    if event_name == "client_scan_finished":
        result_map = {
            "pushed": "发现新消息并已上传",
            "no_new_messages": "没有发现新消息",
            "no_messages": "没有读取到聊天记录",
            "push_failed": "消息上传失败",
            "decrypt_failed": "微信数据库解密失败",
            "wechat_not_logged_in": "微信未登录",
        }
        result = result_map.get(payload.get("result"), payload.get("result", "未知结果"))
        return f"{result}；本轮读取 {payload.get('message_count', 0)} 条；耗时 {payload.get('duration_ms', 0)} 毫秒。"
    if event_name == "server_messages_received":
        return f"服务器收到 {payload.get('received_count', 0)} 条，新增 {payload.get('added_count', 0)} 条，重复 {payload.get('duplicate_count', 0)} 条。"
    if event_name == "server_contacts_received":
        return f"服务器收到 {payload.get('received_count', 0)} 个联系人，更新 {payload.get('changed_count', 0)} 个。"
    if event_name == "server_favorites_received":
        return f"服务器收到 {payload.get('received_count', 0)} 条收藏，更新 {payload.get('changed_count', 0)} 条。"
    if event_name == "server_status_updated":
        login_text = "微信已登录" if payload.get("wechat_logged_in") else "微信未登录"
        decrypt_text = "解密正常" if payload.get("decrypt_ok") else "解密异常"
        return f"{login_text}，{decrypt_text}。"
    if event_name == "server_error_reported":
        return f"错误内容：{payload.get('error_message', '未知错误')}"
    if event_name == "server_unauthorized":
        return "有请求被拒绝，通常是 token（上传密钥）不一致。"
    return json.dumps(payload or {}, ensure_ascii=False)


def save_status(data):
    with get_db() as conn:
        upsert_status(
            conn,
            last_heartbeat=data.get("last_heartbeat", 0),
            decrypt_ok=bool(data.get("decrypt_ok", False)),
            wechat_logged_in=bool(data.get("wechat_logged_in", False)),
        )

        errors = data.get("errors", [])
        if errors:
            for error in errors:
                insert_error_log(conn, error.get("message", ""), error.get("time"))
        conn.commit()


def get_db():
    return pymysql.connect(
        host=MYSQL_HOST,
        port=MYSQL_PORT,
        user=MYSQL_USER,
        password=MYSQL_PASSWORD,
        database=MYSQL_DATABASE,
        charset="utf8mb4",
        cursorclass=DictCursor,
        autocommit=False,
    )


def ensure_database():
    bootstrap = pymysql.connect(
        host=MYSQL_HOST,
        port=MYSQL_PORT,
        user=MYSQL_USER,
        password=MYSQL_PASSWORD,
        charset="utf8mb4",
        autocommit=True,
    )
    try:
        with bootstrap.cursor() as cursor:
            cursor.execute(
                f"CREATE DATABASE IF NOT EXISTS `{MYSQL_DATABASE}` "
                "CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci"
            )
    finally:
        bootstrap.close()


def init_db():
    ensure_database()
    with get_db() as conn:
        with conn.cursor() as cursor:
            cursor.execute(
                """
                CREATE TABLE IF NOT EXISTS messages (
                    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    session_id VARCHAR(120) NOT NULL DEFAULT '',
                    client_source VARCHAR(32) NOT NULL DEFAULT '',
                    client_ip VARCHAR(64) NOT NULL DEFAULT '',
                    wxid VARCHAR(255) NOT NULL,
                    nickname VARCHAR(255) NOT NULL DEFAULT '',
                    sender VARCHAR(255) NOT NULL DEFAULT '',
                    content TEXT NOT NULL,
                    content_hash CHAR(64) NOT NULL,
                    create_time BIGINT NOT NULL,
                    is_sender TINYINT(1) NOT NULL DEFAULT 0,
                    avatar LONGTEXT NULL,
                    msg_type INT NOT NULL DEFAULT 0,
                    msg_sub_type INT NOT NULL DEFAULT 0,
                    media_type VARCHAR(32) NOT NULL DEFAULT '',
                    media_mime VARCHAR(80) NOT NULL DEFAULT '',
                    media_name VARCHAR(255) NOT NULL DEFAULT '',
                    media_data LONGTEXT NULL,
                    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE KEY uniq_message_session (session_id, wxid, create_time, content_hash),
                    KEY idx_messages_session_id (session_id),
                    KEY idx_messages_client_source (client_source),
                    KEY idx_messages_create_time (create_time),
                    KEY idx_messages_nickname (nickname),
                    KEY idx_messages_wxid (wxid)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
                """
            )
            cursor.execute(
                """
                CREATE TABLE IF NOT EXISTS contacts (
                    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    session_id VARCHAR(120) NOT NULL DEFAULT '',
                    client_source VARCHAR(32) NOT NULL DEFAULT '',
                    client_ip VARCHAR(64) NOT NULL DEFAULT '',
                    wxid VARCHAR(255) NOT NULL,
                    alias VARCHAR(255) NOT NULL DEFAULT '',
                    remark VARCHAR(255) NOT NULL DEFAULT '',
                    nick_name VARCHAR(255) NOT NULL DEFAULT '',
                    display_name VARCHAR(255) NOT NULL DEFAULT '',
                    avatar LONGTEXT NULL,
                    source_updated_at BIGINT NOT NULL DEFAULT 0,
                    extra_json JSON NULL,
                    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                        ON UPDATE CURRENT_TIMESTAMP,
                    UNIQUE KEY uniq_contact_session (session_id, wxid),
                    KEY idx_contacts_session_id (session_id),
                    KEY idx_contacts_client_source (client_source),
                    KEY idx_contacts_display_name (display_name)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
                """
            )
            cursor.execute(
                """
                CREATE TABLE IF NOT EXISTS favorites (
                    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    session_id VARCHAR(120) NOT NULL DEFAULT '',
                    client_source VARCHAR(32) NOT NULL DEFAULT '',
                    client_ip VARCHAR(64) NOT NULL DEFAULT '',
                    source_table VARCHAR(255) NOT NULL DEFAULT '',
                    source_id VARCHAR(255) NOT NULL DEFAULT '',
                    title VARCHAR(255) NOT NULL DEFAULT '',
                    summary TEXT NULL,
                    item_type VARCHAR(80) NOT NULL DEFAULT '',
                    item_sub_type VARCHAR(80) NOT NULL DEFAULT '',
                    source_updated_at BIGINT NOT NULL DEFAULT 0,
                    data_json JSON NULL,
                    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                        ON UPDATE CURRENT_TIMESTAMP,
                    UNIQUE KEY uniq_favorite_session (session_id, source_table, source_id),
                    KEY idx_favorites_session_id (session_id),
                    KEY idx_favorites_client_source (client_source),
                    KEY idx_favorites_updated_at (source_updated_at)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
                """
            )
            cursor.execute(
                """
                CREATE TABLE IF NOT EXISTS monitor_status (
                    id TINYINT NOT NULL PRIMARY KEY,
                    last_heartbeat BIGINT NOT NULL DEFAULT 0,
                    decrypt_ok TINYINT(1) NOT NULL DEFAULT 0,
                    wechat_logged_in TINYINT(1) NOT NULL DEFAULT 0,
                    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                        ON UPDATE CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
                """
            )
            cursor.execute(
                """
                CREATE TABLE IF NOT EXISTS monitor_errors (
                    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    session_id VARCHAR(120) NOT NULL DEFAULT '',
                    client_source VARCHAR(32) NOT NULL DEFAULT '',
                    client_ip VARCHAR(64) NOT NULL DEFAULT '',
                    message TEXT NOT NULL,
                    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    KEY idx_monitor_errors_session (session_id),
                    KEY idx_monitor_errors_created_at (created_at)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
                """
            )
            cursor.execute(
                """
                CREATE TABLE IF NOT EXISTS event_logs (
                    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    event_name VARCHAR(120) NOT NULL,
                    source VARCHAR(32) NOT NULL,
                    session_id VARCHAR(120) NOT NULL DEFAULT '',
                    client_ip VARCHAR(64) NOT NULL DEFAULT '',
                    payload_json JSON NULL,
                    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    KEY idx_event_name (event_name),
                    KEY idx_event_source (source),
                    KEY idx_event_session_id (session_id),
                    KEY idx_event_client_ip (client_ip),
                    KEY idx_event_created_at (created_at)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
                """
            )
            cursor.execute(
                """
                CREATE TABLE IF NOT EXISTS monitor_logs (
                    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    log_type VARCHAR(16) NOT NULL,
                    event_name VARCHAR(120) NOT NULL,
                    source VARCHAR(32) NOT NULL DEFAULT '',
                    session_id VARCHAR(120) NOT NULL DEFAULT '',
                    client_source VARCHAR(32) NOT NULL DEFAULT '',
                    client_ip VARCHAR(64) NOT NULL DEFAULT '',
                    severity VARCHAR(16) NOT NULL DEFAULT 'info',
                    stage_key VARCHAR(64) NOT NULL DEFAULT '',
                    title VARCHAR(255) NOT NULL DEFAULT '',
                    details TEXT NULL,
                    dedupe_key VARCHAR(255) NOT NULL DEFAULT '',
                    occurrence_count INT NOT NULL DEFAULT 1,
                    payload_json JSON NULL,
                    first_event_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    last_event_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                        ON UPDATE CURRENT_TIMESTAMP,
                    KEY idx_monitor_logs_type_id (log_type, id),
                    KEY idx_monitor_logs_session (session_id),
                    KEY idx_monitor_logs_client_source (client_source),
                    KEY idx_monitor_logs_dedupe (dedupe_key),
                    KEY idx_monitor_logs_updated_at (updated_at),
                    KEY idx_monitor_logs_last_event_at (last_event_at)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
                """
            )
            cursor.execute(
                """
                CREATE TABLE IF NOT EXISTS client_sessions (
                    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    session_id VARCHAR(120) NOT NULL,
                    client_source VARCHAR(32) NOT NULL DEFAULT '',
                    client_ip VARCHAR(64) NOT NULL DEFAULT '',
                    last_heartbeat BIGINT NOT NULL DEFAULT 0,
                    decrypt_ok TINYINT(1) NULL DEFAULT NULL,
                    wechat_logged_in TINYINT(1) NULL DEFAULT NULL,
                    last_error TEXT NULL,
                    last_event_name VARCHAR(120) NOT NULL DEFAULT '',
                    last_event_at DATETIME NULL,
                    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                        ON UPDATE CURRENT_TIMESTAMP,
                    UNIQUE KEY uniq_client_session (session_id, client_source),
                    KEY idx_client_sessions_ip (client_ip),
                    KEY idx_client_sessions_updated_at (updated_at)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
                """
            )
            try:
                cursor.execute(
                    """
                    ALTER TABLE monitor_status
                    ADD COLUMN wechat_logged_in TINYINT(1) NOT NULL DEFAULT 0
                    """
                )
            except Exception:
                pass
            for column_sql in [
                "ADD COLUMN session_id VARCHAR(120) NOT NULL DEFAULT ''",
                "ADD COLUMN client_source VARCHAR(32) NOT NULL DEFAULT ''",
                "ADD COLUMN client_ip VARCHAR(64) NOT NULL DEFAULT ''",
                "ADD COLUMN media_type VARCHAR(32) NOT NULL DEFAULT ''",
                "ADD COLUMN media_mime VARCHAR(80) NOT NULL DEFAULT ''",
                "ADD COLUMN media_name VARCHAR(255) NOT NULL DEFAULT ''",
                "ADD COLUMN media_data LONGTEXT NULL",
            ]:
                try:
                    cursor.execute(f"ALTER TABLE messages {column_sql}")
                except Exception:
                    pass
            for column_sql in [
                "ADD COLUMN session_id VARCHAR(120) NOT NULL DEFAULT ''",
                "ADD COLUMN client_source VARCHAR(32) NOT NULL DEFAULT ''",
                "ADD COLUMN client_ip VARCHAR(64) NOT NULL DEFAULT ''",
                "ADD COLUMN source_updated_at BIGINT NOT NULL DEFAULT 0",
                "ADD COLUMN extra_json JSON NULL",
            ]:
                try:
                    cursor.execute(f"ALTER TABLE contacts {column_sql}")
                except Exception:
                    pass
            for column_sql in [
                "ADD COLUMN session_id VARCHAR(120) NOT NULL DEFAULT ''",
                "ADD COLUMN client_source VARCHAR(32) NOT NULL DEFAULT ''",
                "ADD COLUMN client_ip VARCHAR(64) NOT NULL DEFAULT ''",
                "ADD COLUMN item_type VARCHAR(80) NOT NULL DEFAULT ''",
                "ADD COLUMN item_sub_type VARCHAR(80) NOT NULL DEFAULT ''",
                "ADD COLUMN source_updated_at BIGINT NOT NULL DEFAULT 0",
                "ADD COLUMN data_json JSON NULL",
            ]:
                try:
                    cursor.execute(f"ALTER TABLE favorites {column_sql}")
                except Exception:
                    pass
            for column_sql in [
                "ADD COLUMN session_id VARCHAR(120) NOT NULL DEFAULT ''",
                "ADD COLUMN client_source VARCHAR(32) NOT NULL DEFAULT ''",
                "ADD COLUMN client_ip VARCHAR(64) NOT NULL DEFAULT ''",
            ]:
                try:
                    cursor.execute(f"ALTER TABLE monitor_errors {column_sql}")
                except Exception:
                    pass
            try:
                cursor.execute("ALTER TABLE event_logs ADD COLUMN client_ip VARCHAR(64) NOT NULL DEFAULT ''")
            except Exception:
                pass
            for column_sql in [
                "ADD COLUMN source VARCHAR(32) NOT NULL DEFAULT ''",
                "ADD COLUMN session_id VARCHAR(120) NOT NULL DEFAULT ''",
                "ADD COLUMN client_source VARCHAR(32) NOT NULL DEFAULT ''",
                "ADD COLUMN client_ip VARCHAR(64) NOT NULL DEFAULT ''",
                "ADD COLUMN severity VARCHAR(16) NOT NULL DEFAULT 'info'",
                "ADD COLUMN stage_key VARCHAR(64) NOT NULL DEFAULT ''",
                "ADD COLUMN title VARCHAR(255) NOT NULL DEFAULT ''",
                "ADD COLUMN details TEXT NULL",
                "ADD COLUMN dedupe_key VARCHAR(255) NOT NULL DEFAULT ''",
                "ADD COLUMN occurrence_count INT NOT NULL DEFAULT 1",
                "ADD COLUMN payload_json JSON NULL",
                "ADD COLUMN first_event_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP",
                "ADD COLUMN last_event_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP",
                "ADD COLUMN updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP",
            ]:
                try:
                    cursor.execute(f"ALTER TABLE monitor_logs {column_sql}")
                except Exception:
                    pass
            for alter_sql in [
                "ALTER TABLE messages DROP INDEX uniq_message",
                "ALTER TABLE contacts DROP INDEX uniq_contact_wxid",
                "ALTER TABLE favorites DROP INDEX uniq_favorite_item",
            ]:
                try:
                    cursor.execute(alter_sql)
                except Exception:
                    pass
            for alter_sql in [
                "ALTER TABLE messages ADD UNIQUE KEY uniq_message_session (session_id, wxid, create_time, content_hash)",
                "ALTER TABLE messages ADD KEY idx_messages_session_id (session_id)",
                "ALTER TABLE messages ADD KEY idx_messages_client_source (client_source)",
                "ALTER TABLE contacts ADD UNIQUE KEY uniq_contact_session (session_id, wxid)",
                "ALTER TABLE contacts ADD KEY idx_contacts_session_id (session_id)",
                "ALTER TABLE contacts ADD KEY idx_contacts_client_source (client_source)",
                "ALTER TABLE favorites ADD UNIQUE KEY uniq_favorite_session (session_id, source_table, source_id)",
                "ALTER TABLE favorites ADD KEY idx_favorites_session_id (session_id)",
                "ALTER TABLE favorites ADD KEY idx_favorites_client_source (client_source)",
                "ALTER TABLE monitor_errors ADD KEY idx_monitor_errors_session (session_id)",
                "ALTER TABLE monitor_errors ADD KEY idx_monitor_errors_created_at (created_at)",
                "ALTER TABLE event_logs ADD KEY idx_event_session_id (session_id)",
                "ALTER TABLE event_logs ADD KEY idx_event_client_ip (client_ip)",
                "ALTER TABLE monitor_logs ADD KEY idx_monitor_logs_type_id (log_type, id)",
                "ALTER TABLE monitor_logs ADD KEY idx_monitor_logs_session (session_id)",
                "ALTER TABLE monitor_logs ADD KEY idx_monitor_logs_client_source (client_source)",
                "ALTER TABLE monitor_logs ADD KEY idx_monitor_logs_dedupe (dedupe_key)",
                "ALTER TABLE monitor_logs ADD KEY idx_monitor_logs_updated_at (updated_at)",
                "ALTER TABLE monitor_logs ADD KEY idx_monitor_logs_last_event_at (last_event_at)",
            ]:
                try:
                    cursor.execute(alter_sql)
                except Exception:
                    pass
            cursor.execute(
                """
                UPDATE messages
                SET content_hash = SHA2(
                    CONCAT_WS(
                        CHAR(31),
                        COALESCE(content, ''),
                        COALESCE(CAST(is_sender AS CHAR), '0'),
                        COALESCE(CAST(msg_type AS CHAR), '0'),
                        COALESCE(CAST(msg_sub_type AS CHAR), '0'),
                        COALESCE(media_type, ''),
                        COALESCE(media_name, ''),
                        COALESCE(sender, '')
                    ),
                    256
                )
                """
            )
            cursor.execute(
                """
                INSERT INTO monitor_status (id, last_heartbeat, decrypt_ok, wechat_logged_in)
                VALUES (1, 0, 0, 0)
                ON DUPLICATE KEY UPDATE id = id
                """
            )
        conn.commit()


def message_hash(message):
    parts = [
        str(message.get("content", "") or ""),
        str(int(message.get("is_sender", 0) or 0)),
        str(int(message.get("msg_type", 0) or 0)),
        str(int(message.get("msg_sub_type", 0) or 0)),
        str(message.get("media_type", "") or ""),
        str(message.get("media_name", "") or ""),
        str(message.get("sender", "") or ""),
    ]
    return hashlib.sha256("\x1f".join(parts).encode("utf-8")).hexdigest()


def normalize_message(message, context=None):
    context = context or {}
    return {
        "session_id": normalize_session_id(message.get("session_id") or context.get("session_id")),
        "client_source": normalize_client_source(message.get("client_source") or message.get("source") or context.get("client_source"), default="unknown"),
        "client_ip": str(message.get("client_ip") or context.get("client_ip") or ""),
        "wxid": str(message.get("wxid", "")),
        "nickname": str(message.get("nickname") or message.get("wxid") or ""),
        "sender": str(message.get("sender", "")),
        "content": str(message.get("content", "")),
        "create_time": int(message.get("create_time", 0) or 0),
        "is_sender": 1 if message.get("is_sender") else 0,
        "avatar": message.get("avatar"),
        "msg_type": int(message.get("msg_type", 0) or 0),
        "msg_sub_type": int(message.get("msg_sub_type", 0) or 0),
        "media_type": str(message.get("media_type", "") or ""),
        "media_mime": str(message.get("media_mime", "") or ""),
        "media_name": str(message.get("media_name", "") or ""),
        "media_data": message.get("media_data"),
    }


def bulk_insert_messages(conn, messages, context=None):
    normalized = [normalize_message(message, context=context) for message in messages if message.get("wxid")]
    if not normalized:
        return 0

    rows = [
        (
            msg["session_id"],
            msg["client_source"],
            msg["client_ip"],
            msg["wxid"],
            msg["nickname"],
            msg["sender"],
            msg["content"],
            message_hash(msg),
            msg["create_time"],
            msg["is_sender"],
            msg["avatar"],
            msg["msg_type"],
            msg["msg_sub_type"],
            msg["media_type"],
            msg["media_mime"],
            msg["media_name"],
            msg["media_data"],
        )
        for msg in normalized
    ]

    with conn.cursor() as cursor:
        cursor.executemany(
            """
            INSERT IGNORE INTO messages (
                session_id, client_source, client_ip,
                wxid, nickname, sender, content, content_hash,
                create_time, is_sender, avatar, msg_type, msg_sub_type,
                media_type, media_mime, media_name, media_data
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
            """,
            rows,
        )
        return cursor.rowcount


def normalize_contact(contact, context=None):
    context = context or {}
    wxid = str(contact.get("wxid") or contact.get("username") or "").strip()
    alias = str(contact.get("alias") or "").strip()
    remark = str(contact.get("remark") or "").strip()
    nick_name = str(contact.get("nick_name") or contact.get("nickname") or "").strip()
    display_name = str(contact.get("display_name") or remark or nick_name or alias or wxid).strip()

    return {
        "session_id": normalize_session_id(contact.get("session_id") or context.get("session_id")),
        "client_source": normalize_client_source(contact.get("client_source") or contact.get("source") or context.get("client_source"), default="unknown"),
        "client_ip": str(contact.get("client_ip") or context.get("client_ip") or ""),
        "wxid": wxid,
        "alias": alias,
        "remark": remark,
        "nick_name": nick_name,
        "display_name": display_name,
        "avatar": contact.get("avatar"),
        "source_updated_at": int(contact.get("source_updated_at", 0) or 0),
        "extra_json": contact.get("extra_json"),
    }


def bulk_upsert_contacts(conn, contacts, context=None):
    normalized = [normalize_contact(contact, context=context) for contact in contacts if (contact.get("wxid") or contact.get("username"))]
    if not normalized:
        return 0

    rows = [
        (
            item["session_id"],
            item["client_source"],
            item["client_ip"],
            item["wxid"],
            item["alias"],
            item["remark"],
            item["nick_name"],
            item["display_name"],
            item["avatar"],
            item["source_updated_at"],
            json.dumps(item["extra_json"], ensure_ascii=False) if item["extra_json"] is not None else None,
        )
        for item in normalized
    ]

    with conn.cursor() as cursor:
        cursor.executemany(
            """
            INSERT INTO contacts (
                session_id, client_source, client_ip,
                wxid, alias, remark, nick_name, display_name,
                avatar, source_updated_at, extra_json
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
            ON DUPLICATE KEY UPDATE
                client_source = VALUES(client_source),
                client_ip = CASE
                    WHEN VALUES(client_ip) <> '' THEN VALUES(client_ip)
                    ELSE contacts.client_ip
                END,
                alias = VALUES(alias),
                remark = VALUES(remark),
                nick_name = VALUES(nick_name),
                display_name = VALUES(display_name),
                avatar = CASE
                    WHEN VALUES(avatar) IS NOT NULL AND VALUES(avatar) <> '' THEN VALUES(avatar)
                    ELSE contacts.avatar
                END,
                source_updated_at = GREATEST(contacts.source_updated_at, VALUES(source_updated_at)),
                extra_json = VALUES(extra_json)
            """,
            rows,
        )
        return cursor.rowcount


def normalize_favorite(favorite, context=None):
    context = context or {}
    source_table = str(favorite.get("source_table") or favorite.get("table_name") or "").strip()
    source_id = str(favorite.get("source_id") or favorite.get("id") or "").strip()
    title = str(favorite.get("title") or "").strip()
    summary = str(favorite.get("summary") or favorite.get("content") or "").strip()
    item_type = str(favorite.get("item_type") or favorite.get("type") or "").strip()
    item_sub_type = str(favorite.get("item_sub_type") or favorite.get("sub_type") or "").strip()
    source_updated_at = int(favorite.get("source_updated_at", 0) or 0)
    data_json = favorite.get("data_json")

    return {
        "session_id": normalize_session_id(favorite.get("session_id") or context.get("session_id")),
        "client_source": normalize_client_source(favorite.get("client_source") or favorite.get("source") or context.get("client_source"), default="unknown"),
        "client_ip": str(favorite.get("client_ip") or context.get("client_ip") or ""),
        "source_table": source_table,
        "source_id": source_id,
        "title": title,
        "summary": summary,
        "item_type": item_type,
        "item_sub_type": item_sub_type,
        "source_updated_at": source_updated_at,
        "data_json": data_json,
    }


def bulk_upsert_favorites(conn, favorites, context=None):
    normalized = [
        normalize_favorite(favorite, context=context)
        for favorite in favorites
        if (favorite.get("source_id") or favorite.get("id")) and (favorite.get("source_table") or favorite.get("table_name"))
    ]
    if not normalized:
        return 0

    rows = [
        (
            item["session_id"],
            item["client_source"],
            item["client_ip"],
            item["source_table"],
            item["source_id"],
            item["title"],
            item["summary"],
            item["item_type"],
            item["item_sub_type"],
            item["source_updated_at"],
            json.dumps(item["data_json"], ensure_ascii=False) if item["data_json"] is not None else None,
        )
        for item in normalized
    ]

    with conn.cursor() as cursor:
        cursor.executemany(
            """
            INSERT INTO favorites (
                session_id, client_source, client_ip,
                source_table, source_id, title, summary,
                item_type, item_sub_type, source_updated_at, data_json
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
            ON DUPLICATE KEY UPDATE
                client_source = VALUES(client_source),
                client_ip = CASE
                    WHEN VALUES(client_ip) <> '' THEN VALUES(client_ip)
                    ELSE favorites.client_ip
                END,
                title = VALUES(title),
                summary = VALUES(summary),
                item_type = VALUES(item_type),
                item_sub_type = VALUES(item_sub_type),
                source_updated_at = GREATEST(favorites.source_updated_at, VALUES(source_updated_at)),
                data_json = VALUES(data_json)
            """,
            rows,
        )
        return cursor.rowcount


def upsert_status(conn, last_heartbeat, decrypt_ok, wechat_logged_in=False):
    with conn.cursor() as cursor:
        cursor.execute(
            """
            INSERT INTO monitor_status (id, last_heartbeat, decrypt_ok, wechat_logged_in)
            VALUES (1, %s, %s, %s)
            ON DUPLICATE KEY UPDATE
                last_heartbeat = VALUES(last_heartbeat),
                decrypt_ok = VALUES(decrypt_ok),
                wechat_logged_in = VALUES(wechat_logged_in)
            """,
            (int(last_heartbeat or 0), 1 if decrypt_ok else 0, 1 if wechat_logged_in else 0),
        )


def upsert_client_session(
    conn,
    session_id,
    client_source,
    client_ip="",
    last_heartbeat=0,
    decrypt_ok=None,
    wechat_logged_in=None,
    last_error="",
    last_event_name="",
    last_event_at=None,
):
    session_id = normalize_session_id(session_id)
    client_source = normalize_client_source(client_source, default="unknown")
    if not session_id:
        return

    with conn.cursor() as cursor:
        cursor.execute(
            """
            INSERT INTO client_sessions (
                session_id, client_source, client_ip,
                last_heartbeat, decrypt_ok, wechat_logged_in,
                last_error, last_event_name, last_event_at
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s)
            ON DUPLICATE KEY UPDATE
                client_ip = CASE
                    WHEN VALUES(client_ip) <> '' THEN VALUES(client_ip)
                    ELSE client_sessions.client_ip
                END,
                last_heartbeat = GREATEST(client_sessions.last_heartbeat, VALUES(last_heartbeat)),
                decrypt_ok = IFNULL(VALUES(decrypt_ok), client_sessions.decrypt_ok),
                wechat_logged_in = IFNULL(VALUES(wechat_logged_in), client_sessions.wechat_logged_in),
                last_error = CASE
                    WHEN VALUES(last_error) <> '' THEN VALUES(last_error)
                    ELSE client_sessions.last_error
                END,
                last_event_name = CASE
                    WHEN VALUES(last_event_name) <> '' THEN VALUES(last_event_name)
                    ELSE client_sessions.last_event_name
                END,
                last_event_at = CASE
                    WHEN VALUES(last_event_at) IS NOT NULL THEN VALUES(last_event_at)
                    ELSE client_sessions.last_event_at
                END
            """,
            (
                session_id,
                client_source,
                str(client_ip or ""),
                int(last_heartbeat or 0),
                None if decrypt_ok is None else (1 if decrypt_ok else 0),
                None if wechat_logged_in is None else (1 if wechat_logged_in else 0),
                str(last_error or ""),
                str(last_event_name or ""),
                last_event_at,
            ),
        )


def insert_error_log(conn, message, created_at=None, session_id="", client_source="", client_ip=""):
    if not message:
        return

    if created_at:
        try:
            parsed_time = datetime.strptime(created_at, "%Y-%m-%d %H:%M:%S")
        except ValueError:
            parsed_time = datetime.now()
    else:
        parsed_time = datetime.now()

    with conn.cursor() as cursor:
        cursor.execute(
            """
            INSERT INTO monitor_errors (session_id, client_source, client_ip, message, created_at)
            VALUES (%s, %s, %s, %s, %s)
            """,
            (
                normalize_session_id(session_id),
                normalize_client_source(client_source, default="unknown"),
                str(client_ip or ""),
                message,
                parsed_time,
            ),
        )
        cursor.execute(
            """
            DELETE FROM monitor_errors
            WHERE id NOT IN (
                SELECT id FROM (
                    SELECT id
                    FROM monitor_errors
                    ORDER BY id DESC
                    LIMIT 50
                ) AS recent_errors
            )
            """
        )


def log_event(conn, event_name, source, payload=None, session_id="", created_at=None, client_ip=""):
    if not event_name or not source:
        return

    event_time = created_at or datetime.now()
    payload_text = json.dumps(payload or {}, ensure_ascii=False)

    with conn.cursor() as cursor:
        cursor.execute(
            """
            INSERT INTO event_logs (event_name, source, session_id, client_ip, payload_json, created_at)
            VALUES (%s, %s, %s, %s, %s, %s)
            """,
            (event_name, source, normalize_session_id(session_id), str(client_ip or ""), payload_text, event_time),
        )


def migrate_legacy_json():
    messages = []
    status = {"last_heartbeat": 0, "decrypt_ok": False, "errors": []}

    if LEGACY_MESSAGE_FILE.exists():
        messages = json.loads(LEGACY_MESSAGE_FILE.read_text(encoding="utf-8"))

    if LEGACY_STATUS_FILE.exists():
        status = json.loads(LEGACY_STATUS_FILE.read_text(encoding="utf-8"))

    if not messages and not status.get("last_heartbeat") and not status.get("errors"):
        return

    with get_db() as conn:
        with conn.cursor() as cursor:
            cursor.execute("SELECT COUNT(*) AS total FROM messages")
            message_count = cursor.fetchone()["total"]
            cursor.execute("SELECT COUNT(*) AS total FROM monitor_errors")
            error_count = cursor.fetchone()["total"]

        if message_count == 0 and messages:
            bulk_insert_messages(conn, messages)

        if message_count == 0 or error_count == 0:
            upsert_status(
                conn,
                last_heartbeat=status.get("last_heartbeat", 0),
                decrypt_ok=bool(status.get("decrypt_ok", False)),
                wechat_logged_in=bool(status.get("wechat_logged_in", False)),
            )

        if error_count == 0:
            for error in status.get("errors", []):
                insert_error_log(conn, error.get("message", ""), error.get("time"))

        conn.commit()


@app.route("/")
def index():
    return render_template_string(HTML_TEMPLATE)


@app.route("/api/messages", methods=["GET"])
def get_messages():
    limit = parse_int(request.args.get("limit"), 5000, minimum=1, maximum=10000)
    offset = parse_int(request.args.get("offset"), 0, minimum=0)
    session_id, client_source = parse_scope_filters(request.args)
    return jsonify({
        "messages": load_messages(limit=limit, offset=offset, session_id=session_id, client_source=client_source),
        "total": count_messages(session_id=session_id, client_source=client_source),
        "limit": limit,
        "offset": offset,
        "session_id": session_id,
        "client_source": client_source,
    })


@app.route("/api/messages", methods=["POST"])
def receive_messages():
    data = request.json or {}
    if data.get("token") != SERVER_TOKEN:
        with get_db() as conn:
            log_event(
                conn,
                "server_unauthorized",
                "server",
                {"path": "/api/messages", "remote_addr": request.remote_addr},
                client_ip=request.remote_addr,
            )
            conn.commit()
        return jsonify({"error": "unauthorized"}), 401

    new_messages = data.get("messages", [])
    if not new_messages:
        return jsonify({"ok": True, "count": 0})

    with get_db() as conn:
        client_context = resolve_client_context(conn, data=data, remote_addr=request.remote_addr, fallback_source="client_cs")
        added = bulk_insert_messages(conn, new_messages, context=client_context)
        with conn.cursor() as cursor:
            cursor.execute(
                """
                SELECT COUNT(*) AS total
                FROM messages
                WHERE session_id = %s AND client_source = %s
                """,
                (client_context["session_id"], client_context["client_source"]),
            )
            total = cursor.fetchone()["total"]
        upsert_client_session(
            conn,
            client_context["session_id"],
            client_context["client_source"],
            client_ip=client_context["client_ip"],
        )
        log_event(
            conn,
            "server_messages_received",
            "server",
            {
                "received_count": len(new_messages),
                "added_count": added,
                "duplicate_count": max(len(new_messages) - added, 0),
                "client_ip": request.remote_addr,
            },
            session_id=client_context["session_id"],
            client_ip=client_context["client_ip"],
        )
        conn.commit()

    print(f"[{datetime.now().strftime('%H:%M:%S')}] 收到 {len(new_messages)} 条，新增 {added} 条")
    return jsonify({
        "ok": True,
        "total": total,
        "added": added,
        "session_id": client_context["session_id"],
        "client_source": client_context["client_source"],
        "scope_inferred": client_context["inferred"],
    })


@app.route("/api/contacts", methods=["GET"])
def get_contacts():
    limit = parse_int(request.args.get("limit"), 2000, minimum=1, maximum=10000)
    offset = parse_int(request.args.get("offset"), 0, minimum=0)
    session_id, client_source = parse_scope_filters(request.args)
    return jsonify({
        "contacts": load_contacts(limit=limit, offset=offset, session_id=session_id, client_source=client_source),
        "total": count_contacts(session_id=session_id, client_source=client_source),
        "limit": limit,
        "offset": offset,
        "session_id": session_id,
        "client_source": client_source,
    })


@app.route("/api/contacts", methods=["POST"])
def receive_contacts():
    data = request.json or {}
    if data.get("token") != SERVER_TOKEN:
        with get_db() as conn:
            log_event(
                conn,
                "server_unauthorized",
                "server",
                {"path": "/api/contacts", "remote_addr": request.remote_addr},
                client_ip=request.remote_addr,
            )
            conn.commit()
        return jsonify({"error": "unauthorized"}), 401

    new_contacts = data.get("contacts", [])
    if not new_contacts:
        return jsonify({"ok": True, "count": 0})

    with get_db() as conn:
        client_context = resolve_client_context(conn, data=data, remote_addr=request.remote_addr, fallback_source="client_cs")
        changed = bulk_upsert_contacts(conn, new_contacts, context=client_context)
        with conn.cursor() as cursor:
            cursor.execute(
                """
                SELECT COUNT(*) AS total
                FROM contacts
                WHERE session_id = %s AND client_source = %s
                """,
                (client_context["session_id"], client_context["client_source"]),
            )
            total = cursor.fetchone()["total"]
        upsert_client_session(
            conn,
            client_context["session_id"],
            client_context["client_source"],
            client_ip=client_context["client_ip"],
        )
        log_event(
            conn,
            "server_contacts_received",
            "server",
            {
                "received_count": len(new_contacts),
                "changed_count": changed,
                "client_ip": request.remote_addr,
            },
            session_id=client_context["session_id"],
            client_ip=client_context["client_ip"],
        )
        conn.commit()

    return jsonify({
        "ok": True,
        "total": total,
        "changed": changed,
        "session_id": client_context["session_id"],
        "client_source": client_context["client_source"],
        "scope_inferred": client_context["inferred"],
    })


@app.route("/api/favorites", methods=["GET"])
def get_favorites():
    limit = parse_int(request.args.get("limit"), 1000, minimum=1, maximum=5000)
    offset = parse_int(request.args.get("offset"), 0, minimum=0)
    session_id, client_source = parse_scope_filters(request.args)
    return jsonify({
        "favorites": load_favorites(limit=limit, offset=offset, session_id=session_id, client_source=client_source),
        "total": count_favorites(session_id=session_id, client_source=client_source),
        "limit": limit,
        "offset": offset,
        "session_id": session_id,
        "client_source": client_source,
    })


@app.route("/api/favorites", methods=["POST"])
def receive_favorites():
    data = request.json or {}
    if data.get("token") != SERVER_TOKEN:
        with get_db() as conn:
            log_event(
                conn,
                "server_unauthorized",
                "server",
                {"path": "/api/favorites", "remote_addr": request.remote_addr},
                client_ip=request.remote_addr,
            )
            conn.commit()
        return jsonify({"error": "unauthorized"}), 401

    new_favorites = data.get("favorites", [])
    if not new_favorites:
        return jsonify({"ok": True, "count": 0})

    with get_db() as conn:
        client_context = resolve_client_context(conn, data=data, remote_addr=request.remote_addr, fallback_source="client_cs")
        changed = bulk_upsert_favorites(conn, new_favorites, context=client_context)
        with conn.cursor() as cursor:
            cursor.execute(
                """
                SELECT COUNT(*) AS total
                FROM favorites
                WHERE session_id = %s AND client_source = %s
                """,
                (client_context["session_id"], client_context["client_source"]),
            )
            total = cursor.fetchone()["total"]
        upsert_client_session(
            conn,
            client_context["session_id"],
            client_context["client_source"],
            client_ip=client_context["client_ip"],
        )
        log_event(
            conn,
            "server_favorites_received",
            "server",
            {
                "received_count": len(new_favorites),
                "changed_count": changed,
                "client_ip": request.remote_addr,
            },
            session_id=client_context["session_id"],
            client_ip=client_context["client_ip"],
        )
        conn.commit()

    return jsonify({
        "ok": True,
        "total": total,
        "changed": changed,
        "session_id": client_context["session_id"],
        "client_source": client_context["client_source"],
        "scope_inferred": client_context["inferred"],
    })


@app.route("/api/stats")
def stats():
    session_id, client_source = parse_scope_filters(request.args)
    messages = load_messages(limit=5000, session_id=session_id, client_source=client_source)
    return jsonify({
        "total": len(messages),
        "contacts": count_contacts(session_id=session_id, client_source=client_source),
        "favorites": count_favorites(session_id=session_id, client_source=client_source),
        "last_update": max((m["create_time"] for m in messages), default=0),
        "session_id": session_id,
        "client_source": client_source,
    })


@app.route("/api/status", methods=["GET"])
def get_status():
    session_id, client_source = parse_scope_filters(request.args)
    return jsonify(load_status(session_id=session_id, client_source=client_source))


@app.route("/api/status", methods=["POST"])
def update_status():
    data = request.json or {}
    if data.get("token") != SERVER_TOKEN:
        with get_db() as conn:
            log_event(
                conn,
                "server_unauthorized",
                "server",
                {"path": "/api/status", "remote_addr": request.remote_addr},
                client_ip=request.remote_addr,
            )
            conn.commit()
        return jsonify({"error": "unauthorized"}), 401

    with get_db() as conn:
        client_context = resolve_client_context(conn, data=data, remote_addr=request.remote_addr, fallback_source="client_cs")
        current_status = load_status(session_id=client_context["session_id"], client_source=client_context["client_source"])
        decrypt_ok = data["decrypt_ok"] if "decrypt_ok" in data else current_status.get("decrypt_ok", False)
        wechat_logged_in = data["wechat_logged_in"] if "wechat_logged_in" in data else current_status.get("wechat_logged_in", False)

        upsert_status(
            conn,
            last_heartbeat=int(datetime.now().timestamp()),
            decrypt_ok=decrypt_ok,
            wechat_logged_in=wechat_logged_in,
        )
        upsert_client_session(
            conn,
            client_context["session_id"],
            client_context["client_source"],
            client_ip=client_context["client_ip"],
            last_heartbeat=int(datetime.now().timestamp()),
            decrypt_ok=bool(decrypt_ok),
            wechat_logged_in=bool(wechat_logged_in),
            last_error=str(data.get("error") or ""),
        )

        if "error" in data:
            insert_error_log(
                conn,
                data["error"],
                session_id=client_context["session_id"],
                client_source=client_context["client_source"],
                client_ip=client_context["client_ip"],
            )
            log_event(
                conn,
                "server_error_reported",
                "server",
                {
                    "client_ip": request.remote_addr,
                    "error_message": data["error"],
                },
                session_id=client_context["session_id"],
                client_ip=client_context["client_ip"],
            )

        log_event(
            conn,
            "server_status_updated",
            "server",
            {
                "decrypt_ok": bool(decrypt_ok),
                "wechat_logged_in": bool(wechat_logged_in),
                "client_ip": request.remote_addr,
                "has_error": "error" in data,
            },
            session_id=client_context["session_id"],
            client_ip=client_context["client_ip"],
        )

        conn.commit()
    return jsonify({
        "ok": True,
        "session_id": client_context["session_id"],
        "client_source": client_context["client_source"],
        "scope_inferred": client_context["inferred"],
    })


@app.route("/api/events", methods=["POST"])
def collect_event():
    data = request.json or {}
    source = (data.get("source") or "").strip() or "unknown"
    event_name = (data.get("event_name") or "").strip()
    payload = data.get("payload") or {}
    session_id = (data.get("session_id") or "").strip()

    if not event_name:
        return jsonify({"error": "missing_event_name"}), 400

    if data.get("token") != SERVER_TOKEN:
        with get_db() as conn:
            log_event(
                conn,
                "server_unauthorized",
                "server",
                {"path": "/api/events", "remote_addr": request.remote_addr, "source": source},
                session_id=session_id,
                client_ip=request.remote_addr,
            )
            for monitor_log in normalize_monitor_event(
                "server_unauthorized",
                "server",
                {"path": "/api/events", "remote_addr": request.remote_addr, "source": source},
                session_id=session_id,
                client_ip=request.remote_addr,
                client_source="server",
            ):
                upsert_monitor_log(conn, monitor_log)
            prune_monitor_logs(conn)
            conn.commit()
        return jsonify({"error": "unauthorized"}), 401

    with get_db() as conn:
        client_context = {
            "session_id": normalize_session_id(session_id) or build_legacy_session_id(request.remote_addr, source),
            "client_source": normalize_client_source(source, default="unknown"),
            "client_ip": str(request.remote_addr or ""),
            "inferred": not bool(normalize_session_id(session_id)),
        }
        log_event(
            conn,
            event_name,
            client_context["client_source"],
            payload,
            session_id=client_context["session_id"],
            client_ip=client_context["client_ip"],
        )
        for monitor_log in normalize_monitor_event(
            event_name,
            client_context["client_source"],
            payload,
            session_id=client_context["session_id"],
            client_ip=client_context["client_ip"],
            client_source=client_context["client_source"],
        ):
            upsert_monitor_log(conn, monitor_log)
        prune_monitor_logs(conn)
        upsert_client_session(
            conn,
            client_context["session_id"],
            client_context["client_source"],
            client_ip=client_context["client_ip"],
            last_event_name=event_name,
            last_event_at=datetime.now(),
        )
        conn.commit()
    return jsonify({
        "ok": True,
        "session_id": client_context["session_id"],
        "client_source": client_context["client_source"],
    })


@app.route("/api/events", methods=["GET"])
def get_events():
    session_id, client_source = parse_scope_filters(request.args)
    limit = parse_int(request.args.get("limit"), 100, minimum=1, maximum=1000)
    return jsonify({
        "events": load_events(limit=limit, session_id=session_id, client_source=client_source),
        "limit": limit,
        "session_id": session_id,
        "client_source": client_source,
    })


if __name__ == "__main__":
    init_db()
    migrate_legacy_json()
    print("=" * 50)
    print("  微信聊天记录查看器 - 服务器端")
    print("=" * 50)
    print(f"  访问地址: http://0.0.0.0:{PORT}")
    print("=" * 50)
    app.run(host="0.0.0.0", port=PORT, debug=False)
