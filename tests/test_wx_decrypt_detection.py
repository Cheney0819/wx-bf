import ast
from pathlib import Path


SOURCE = Path(__file__).parents[1] / "windows" / "wx_decrypt.py"


def load_detector(namespace: dict) -> object:
    tree = ast.parse(SOURCE.read_text(encoding="utf-8"))
    detector = next(
        node
        for node in tree.body
        if isinstance(node, ast.FunctionDef) and node.name == "detect_v4_instance"
    )
    module = ast.Module(body=[detector], type_ignores=[])
    exec(compile(ast.fix_missing_locations(module), str(SOURCE), "exec"), namespace)
    return namespace["detect_v4_instance"]


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

    assert detector()["pid"] == 1234


if __name__ == "__main__":
    test_detects_main_weixin_process_with_startup_flags()
    print("Weixin process detection checks passed.")
