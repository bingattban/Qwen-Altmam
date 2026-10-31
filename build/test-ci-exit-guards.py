#!/usr/bin/env python3
"""Section 05.01/05.04: prevent masked JavaScript regression failures in BOTH Windows gates."""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WORKFLOWS = (
    ROOT / ".github/workflows/windows-ci.yml",
    ROOT / ".github/workflows/windows-release.yml",
)
TEST_LINE = re.compile(r"^\s*node\s+build/(test-[\w-]+\.js)\s*$")
SELF_LINE = re.compile(r"^\s*python\s+build/test-ci-exit-guards\.py\s*$")
EXIT_CHECK = re.compile(r"^\s*if\s*\(\$LASTEXITCODE\s+-ne\s+0\)\s*\{\s*throw\b.+\}\s*$")


def validate(lines: list[str], expected: set[str], label: str) -> list[str]:
    issues: list[str] = []
    seen: list[str] = []
    guards = 0
    for index, line in enumerate(lines):
        test = TEST_LINE.fullmatch(line)
        self_check = SELF_LINE.fullmatch(line)
        if not test and not self_check:
            continue
        if test:
            seen.append(test.group(1))
        else:
            guards += 1
        # A later successful command must never hide the failure of this command.
        if index + 1 >= len(lines) or not EXIT_CHECK.fullmatch(lines[index + 1]):
            issues.append(f"{label}:{index + 1}: missing immediate PowerShell exit-code guard")
    missing = sorted(expected - set(seen))
    extra = sorted(set(seen) - expected)
    repeated = sorted({name for name in seen if seen.count(name) > 1})
    if missing:
        issues.append(f"{label}: regression scripts omitted: {', '.join(missing)}")
    if extra:
        issues.append(f"{label}: unexpected regression scripts: {', '.join(extra)}")
    if repeated:
        issues.append(f"{label}: duplicate regression scripts: {', '.join(repeated)}")
    if guards != 1:
        issues.append(f"{label}: require exactly one guarded invocation of this CI validator")
    return issues


def self_test() -> None:
    sample = [
        "          node build/test-a.js",
        "          node build/test-b.js",
        "          if ($LASTEXITCODE -ne 0) { throw 'b failed' }",
        "          python build/test-ci-exit-guards.py",
        "          if ($LASTEXITCODE -ne 0) { throw 'guard failed' }",
    ]
    assert any(":1:" in s for s in validate(sample, {"test-a.js", "test-b.js"}, "fixture")), (
        "the validator must detect a masked earlier test failure"
    )
    safe = sample[:1] + [
        "          if ($LASTEXITCODE -ne 0) { throw 'a failed' }",
    ] + sample[1:]
    assert not validate(safe, {"test-a.js", "test-b.js"}, "fixture"), (
        "the validator must accept individually guarded tests"
    )
    assert any("omitted" in s for s in validate(safe, {"test-a.js", "test-b.js", "test-c.js"}, "fixture"))


def main() -> None:
    self_test()
    expected = {path.name for path in (ROOT / "build").glob("test-*.js")}
    if len(expected) < 16:
        raise SystemExit("SECTION05 CI FAIL: regression script inventory unexpectedly small")
    issues: list[str] = []
    for workflow in WORKFLOWS:
        issues.extend(validate(workflow.read_text(encoding="utf-8").splitlines(),
                               expected, workflow.name))
    if issues:
        raise SystemExit("SECTION05 CI FAIL:\n" + "\n".join(issues))
    print(f"SECTION05 CI PASS: {len(expected)} JavaScript regressions guarded individually in both Windows workflows")


if __name__ == "__main__":
    main()
