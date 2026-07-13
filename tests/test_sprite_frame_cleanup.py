import re
from pathlib import Path


SOURCE = Path(__file__).parents[1] / "windows-pet-wpf" / "MainWindow.xaml.cs"


def test_boundary_frames_remove_large_spillover_artifacts() -> None:
    source = SOURCE.read_text(encoding="utf-8")

    assert re.search(
        r"frameIndex\s+is\s+>=\s+8\s+and\s+<=\s+10\s*\?\s*600\s*:\s*120",
        source,
    )
    assert re.search(
        r"frameIndex\s+is\s+>=\s+8\s+and\s+<=\s+10\s*\?\s*8\s*:\s*12",
        source,
    )


if __name__ == "__main__":
    test_boundary_frames_remove_large_spillover_artifacts()
    print("Sprite-frame cleanup checks passed.")
