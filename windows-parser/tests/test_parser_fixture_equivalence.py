import json
from pathlib import Path

from conftest import PARSER_ROOT, run_parser


def test_readable_fixture_matches_frozen_schema_v1_result(readable_set: tuple[Path, Path]) -> None:
    job_root, job = readable_set

    completed = run_parser(job)
    actual = json.loads((job_root / "output" / "result.json").read_text(encoding="utf-8"))
    expected = json.loads(
        (PARSER_ROOT / "tests" / "fixtures" / "expected" / "result-v1.json")
        .read_text(encoding="utf-8")
    )

    assert completed.returncode == 0
    assert json.loads(completed.stdout) == {
        "schemaVersion": 1,
        "resultPath": str(job_root / "output" / "result.json"),
        "jobId": "job-fixture",
        "sourceSetId": "source-fixture",
    }
    assert actual == expected
