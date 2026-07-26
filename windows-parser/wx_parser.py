import argparse
import json
import sys
from pathlib import Path

from parser_contract import (
    CancellationState,
    ParserCancelled,
    ParserContractError,
    load_job,
    write_result_atomic,
)
from parser_core import parse_job


def main(arguments: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--job", required=True)
    try:
        options, extras = parser.parse_known_args(arguments)
        if extras:
            raise ParserContractError("unexpected_arguments")
        cancellation = CancellationState()
        cancellation.install_signal_handlers()
        job = load_job(Path(options.job))
        cancellation.set_cancellation_path(job.input_root.parent / "cancel.request")
        cancellation.throw_if_cancelled()
        document = parse_job(job, cancellation)
        cancellation.throw_if_cancelled()
        result_path = write_result_atomic(document, job.output_root)
        completion = {
            "schemaVersion": 1,
            "resultPath": str(result_path),
            "jobId": job.job_id,
            "sourceSetId": job.source_set_id,
        }
        sys.stdout.write(json.dumps(completion, separators=(",", ":")))
        sys.stdout.write("\n")
        return 0
    except ParserCancelled:
        sys.stderr.write("parser_cancelled\n")
        return 130
    except (ParserContractError, OSError, ValueError) as exc:
        code = str(exc)[:256] or "parser_error"
        sys.stderr.write(code + "\n")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
