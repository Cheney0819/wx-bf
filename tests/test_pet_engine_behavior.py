from pathlib import Path


SOURCE = Path(__file__).parents[1] / "windows-pet-wpf" / "PetEngine.cs"


def method_body(source: str, start: str, end: str) -> str:
    return source.split(start, 1)[1].split(end, 1)[0]


def test_balanced_autonomous_behavior_timing() -> None:
    source = SOURCE.read_text(encoding="utf-8")
    tick = method_body(source, "    public PetVisual Tick()", "    public PetVisual Interact")
    auto_behavior = method_body(source, "    public PetVisual AutoBehavior()", "    public void RecoverEnergy")
    quiet_behavior = auto_behavior.split("switch (_random.Next(0, 5))", 1)[1]

    assert "SetIdle();" in tick
    assert "SetBlink();" not in tick
    assert "SetCozy();" not in tick
    assert "ScheduleNextAmbient();" not in tick
    assert "_state != PetState.Idle && DateTime.Now >= _stateEndsAt" in tick
    assert "SetWave();" not in quiet_behavior
    assert "SetPatrolling();" not in quiet_behavior
    assert "SetStretch();" not in quiet_behavior
    assert "DateTime.Now.AddSeconds(_random.Next(12, 21))" in source
    assert "DateTime.Now.AddSeconds(_random.Next(40, 91))" in source
    assert "TotalMilliseconds < 600" in source


if __name__ == "__main__":
    test_balanced_autonomous_behavior_timing()
    print("Balanced desktop-pet behavior checks passed.")
