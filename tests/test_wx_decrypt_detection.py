import ast
from pathlib import Path


SOURCE = Path(__file__).parents[1] / "windows" / "wx_decrypt.py"


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


def load_detector(namespace: dict) -> object:
    return load_function("inspect_v4_instance", namespace)


def test_legacy_detector_keeps_the_previous_working_path() -> None:
    class Process:
        info = {
            "pid": 2468,
            "name": "Weixin.exe",
            "cmdline": ["Weixin.exe"],
        }

    class Psutil:
        @staticmethod
        def process_iter(_attributes):
            return [Process()]

    detector = load_function(
        "detect_v4_instance",
        {
            "psutil": Psutil(),
            "detect_v4_data_dir_from_open_files": lambda _proc: r"D:\\WeChat\\wxid_test",
            "detect_v4_unc_data_dir_from_open_files": lambda _proc: "",
            "collect_v4_data_dir_candidates": lambda: {},
            "log_debug": lambda _message: None,
        },
    )

    assert detector()["pid"] == 2468


def test_detects_main_weixin_process_with_startup_flags() -> None:
    class Process:
        info = {
            "pid": 1234,
            "name": "Weixin.exe",
            "cmdline": ["Weixin.exe", "--enable-features=ModernStartup"],
        }

    class Psutil:
        @staticmethod
        def process_iter(_attributes):
            return [Process()]

    detector = load_detector(
        {
            "psutil": Psutil(),
            "detect_v4_data_dir_from_open_files": lambda _proc: r"D:\\WeChat\\wxid_test",
            "detect_v4_unc_data_dir_from_open_files": lambda _proc: "",
            "collect_v4_data_dir_candidates": lambda: {},
            "log_debug": lambda _message: None,
        }
    )

    instance, diagnostics = detector()
    assert instance["pid"] == 1234
    assert diagnostics["weixin_processes"] == [
        {"pid": 1234, "name": "Weixin.exe", "data_dir_found": True, "data_dir_source": "open_files"}
    ]


def test_continues_after_a_process_enumeration_error() -> None:
    class BrokenProcess:
        @property
        def info(self):
            raise PermissionError("Access is denied")

    class WorkingProcess:
        info = {
            "pid": 5678,
            "name": "Weixin.exe",
        }

    class Psutil:
        @staticmethod
        def process_iter(_attributes):
            return [BrokenProcess(), WorkingProcess()]

    detector = load_detector(
        {
            "psutil": Psutil(),
            "detect_v4_data_dir_from_open_files": lambda _proc: r"D:\\WeChat\\wxid_test",
            "detect_v4_unc_data_dir_from_open_files": lambda _proc: "",
            "collect_v4_data_dir_candidates": lambda: {},
            "log_debug": lambda _message: None,
        }
    )

    instance, diagnostics = detector()
    assert instance["pid"] == 5678
    assert diagnostics["errors"][0]["error_type"] == "PermissionError"


def test_detection_summary_includes_the_first_error() -> None:
    formatter = load_function("format_v4_detection_summary", {})
    summary = formatter(
        {
            "process_count": 310,
            "weixin_processes": [{"pid": 1234}],
            "errors": [
                {
                    "pid": 1234,
                    "error_type": "PermissionError",
                    "error_message": "Access is denied",
                }
            ],
        }
    )

    assert "PermissionError: Access is denied" in summary


def test_skips_an_inaccessible_directory() -> None:
    class BrokenDirectory:
        def exists(self):
            raise OSError(1337, "安全 ID 结构无效", r"D:\\WpSystem\\Documents")

    checker = load_function("is_accessible_directory", {"Path": Path, "log_debug": lambda _message: None})

    assert checker(BrokenDirectory()) is False


def test_skips_a_directory_when_glob_raises() -> None:
    class BrokenDirectory:
        def glob(self, _pattern):
            raise OSError(1337, "安全 ID 结构无效", r"D:\\WpSystem\\Documents")

    globber = load_function("iter_path_glob_safely", {"Path": Path, "log_debug": lambda _message: None})

    assert list(globber(BrokenDirectory(), "**/*.db")) == []


if __name__ == "__main__":
    test_legacy_detector_keeps_the_previous_working_path()
    test_detects_main_weixin_process_with_startup_flags()
    test_continues_after_a_process_enumeration_error()
    test_detection_summary_includes_the_first_error()
    test_skips_an_inaccessible_directory()
    test_skips_a_directory_when_glob_raises()
    print("Weixin process detection checks passed.")
