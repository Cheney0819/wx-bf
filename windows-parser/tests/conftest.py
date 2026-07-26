import hashlib
import json
import sqlite3
import subprocess
import sys
import shutil
from pathlib import Path

import pytest


PARSER_ROOT = Path(__file__).parents[1]
if str(PARSER_ROOT) not in sys.path:
    sys.path.insert(0, str(PARSER_ROOT))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(128 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_job(
    root: Path,
    databases: list[tuple[str, Path]],
    *,
    schema_version: int = 1,
    maximum_messages: int = 5000,
) -> Path:
    input_root = root / "input"
    output_root = root / "output"
    input_root.mkdir(parents=True, exist_ok=True)
    payload = {
        "schemaVersion": schema_version,
        "jobId": "job-fixture",
        "sourceSetId": "source-fixture",
        "inputRoot": str(input_root),
        "outputRoot": str(output_root),
        "databases": [
            {
                "generationId": hashlib.sha256(relative.encode()).hexdigest(),
                "relativePath": relative,
                "path": str(path),
                "sha256": sha256(path),
            }
            for relative, path in databases
        ],
        "maximumMessages": maximum_messages,
    }
    job_path = root / "job.json"
    job_path.write_text(json.dumps(payload), encoding="utf-8")
    return job_path


def run_parser(job_path: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(PARSER_ROOT / "wx_parser.py"), "--job", str(job_path)],
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )


def create_message_database(path: Path, count: int = 3) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    chat_hash = hashlib.md5(b"alice").hexdigest()
    with sqlite3.connect(path) as connection:
        connection.execute("CREATE TABLE Name2Id(user_name TEXT)")
        connection.execute("INSERT INTO Name2Id(rowid, user_name) VALUES(1, 'alice')")
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
        rows = [
            (1, 1, 100, 1, "hello", 0),
            (2, 3, 101, 0, "", 0),
            (3, 34, 102, 1, "", 0),
        ]
        if count != 3:
            rows = [
                (index, 1, index, 1, f"message-{index}", 0)
                for index in range(count)
            ]
        connection.executemany(
            f'INSERT INTO "Msg_{chat_hash}" VALUES(?, ?, ?, ?, ?, ?)',
            rows,
        )
        if count == 3:
            connection.execute(
                "CREATE TABLE VoiceInfo(local_id INTEGER, voice_data BLOB, create_time INTEGER)"
            )
            connection.execute(
                "INSERT INTO VoiceInfo VALUES(3, ?, 102)",
                (b"voice-bytes",),
            )


def create_contact_database(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with sqlite3.connect(path) as connection:
        connection.execute(
            "CREATE TABLE contact(username TEXT, alias TEXT, remark TEXT, nick_name TEXT)"
        )
        connection.execute(
            "INSERT INTO contact VALUES('alice', 'alice_alias', 'Alice Remark', 'Alice Nick')"
        )


def create_favorite_database(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with sqlite3.connect(path) as connection:
        connection.execute(
            "CREATE TABLE Favorites(id INTEGER, title TEXT, summary TEXT, type TEXT, update_time INTEGER)"
        )
        connection.execute(
            "INSERT INTO Favorites VALUES(7, 'Saved title', 'Saved summary', 'link', 99)"
        )


@pytest.fixture
def readable_set(tmp_path: Path) -> tuple[Path, Path]:
    job_root = tmp_path / "job"
    input_root = job_root / "input"
    fixture_root = PARSER_ROOT / "tests" / "fixtures" / "readable-set"
    shutil.copytree(fixture_root, input_root)
    message = input_root / "message" / "message_0.db"
    contact = input_root / "contact" / "contact.db"
    favorite = input_root / "favorite" / "favorite.db"
    job = write_job(
        job_root,
        [
            ("contact/contact.db", contact),
            ("favorite/favorite.db", favorite),
            ("message/message_0.db", message),
        ],
    )
    return job_root, job
