#!/usr/bin/env python3
"""Code metrics for KeepingYouAwake for Windows: coverage + complexity → Markdown.

Reads Cobertura coverage files (the merged .NET report from ReportGenerator and
pytest-cov's coverage.xml) and, when the `lizard` package is installed, measures
cyclomatic complexity and function size of the C# and Python sources.

    python scripts/code_metrics.py --dotnet coverage/Cobertura.xml --python coverage.xml \
        --update docs/code-metrics.md --summary "$GITHUB_STEP_SUMMARY" --check

--update rewrites the section between the `<!-- metrics:begin -->` and
`<!-- metrics:end -->` markers of a hand-written report; --summary appends the
generated section to a file (the GitHub job summary); --check exits non-zero
when a threshold in THRESHOLDS is not met.
"""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BEGIN, END = "<!-- metrics:begin -->", "<!-- metrics:end -->"

# Minimums enforced by --check (percent). Raise them as coverage grows.
THRESHOLDS = {
    ("dotnet", "KeepingYouAwake.Core", "line"): 97.0,
    ("dotnet", "KeepingYouAwake.Core", "branch"): 90.0,
    ("dotnet", "KeepingYouAwake", "line"): 80.0,
    ("dotnet", "KeepingYouAwake", "branch"): 55.0,
    ("python", "*", "line"): 97.0,
    ("python", "*", "branch"): 95.0,
}
# Complexity limits enforced by --check: no function may exceed these.
MAX_CCN = 10
MAX_FUNCTION_NLOC = 50
MAX_PARAMETERS = 5

SOURCE_DIRS = ["src", "tools/cli", "tools/mcp-server"]
EXCLUDE = ["*/tests/*", "*/bin/*", "*/obj/*", "*/e2e/*"]


@dataclass
class Coverage:
    name: str
    lines_valid: int = 0
    lines_covered: int = 0
    branches_valid: int = 0
    branches_covered: int = 0
    files: dict[str, tuple[int, int]] = field(default_factory=dict)  # file -> (covered, valid)

    @property
    def line_rate(self) -> float:
        return 100.0 * self.lines_covered / self.lines_valid if self.lines_valid else 100.0

    @property
    def branch_rate(self) -> float:
        return 100.0 * self.branches_covered / self.branches_valid if self.branches_valid else 100.0


def _branch_counts(line: ET.Element) -> tuple[int, int]:
    # condition-coverage="50% (1/2)"
    text = line.get("condition-coverage", "")
    if "(" not in text:
        return 0, 0
    covered, valid = text[text.index("(") + 1 : text.index(")")].split("/")
    return int(covered), int(valid)


def read_cobertura(path: Path, group_by_package: bool) -> list[Coverage]:
    """Per-package coverage from a Cobertura file. Lines are deduplicated per file (max hits)."""
    root = ET.parse(path).getroot()
    result: list[Coverage] = []
    for package in root.iter("package"):
        name = package.get("name") or "?"
        lines: dict[tuple[str, int], tuple[int, int, int]] = {}
        for cls in package.iter("class"):
            filename = cls.get("filename", "?").replace("\\", "/")
            for line in cls.iter("line"):
                key = (filename, int(line.get("number", "0")))
                hits = int(line.get("hits", "0"))
                covered, valid = _branch_counts(line) if line.get("branch", "").lower() == "true" else (0, 0)
                old = lines.get(key, (0, 0, 0))
                lines[key] = (max(old[0], hits), max(old[1], covered), max(old[2], valid))
        cov = Coverage(name)
        for (filename, _), (hits, b_cov, b_valid) in lines.items():
            cov.lines_valid += 1
            cov.lines_covered += hits > 0
            cov.branches_valid += b_valid
            cov.branches_covered += b_cov
            covered, valid = cov.files.get(filename, (0, 0))
            cov.files[filename] = (covered + (hits > 0), valid + 1)
        result.append(cov)
    if not group_by_package:
        merged = Coverage("python")
        for cov in result:
            merged.lines_valid += cov.lines_valid
            merged.lines_covered += cov.lines_covered
            merged.branches_valid += cov.branches_valid
            merged.branches_covered += cov.branches_covered
            merged.files.update(cov.files)
        result = [merged]
    return result


@dataclass
class Function:
    name: str
    file: str
    line: int
    ccn: int
    nloc: int
    params: int


def measure_complexity() -> list[Function] | None:
    try:
        import lizard  # type: ignore[import-not-found]
    except ImportError:
        return None
    functions: list[Function] = []
    paths = [str(ROOT / d) for d in SOURCE_DIRS]
    for info in lizard.analyze(paths, exclude_pattern=EXCLUDE):
        rel = Path(info.filename).resolve().relative_to(ROOT).as_posix()
        if "/tests/" in rel or "/e2e/" in rel:
            continue
        for fn in info.function_list:
            # File-scoped namespaces and big static tables show up as pseudo functions.
            if fn.name.endswith("*global*") or fn.name.endswith("::new"):
                continue
            functions.append(Function(fn.name, rel, fn.start_line, fn.cyclomatic_complexity, fn.nloc, len(fn.parameters)))
    return functions


def pct(value: float) -> str:
    return f"{value:.1f}%"


def render(dotnet: list[Coverage], python: list[Coverage], functions: list[Function] | None) -> str:
    out: list[str] = []
    out.append("### Coverage\n")
    out.append("| Component | Line | Branch | Covered / coverable lines |")
    out.append("|---|---:|---:|---:|")
    for cov in dotnet:
        out.append(f"| `{cov.name}` (.NET) | {pct(cov.line_rate)} | {pct(cov.branch_rate)} | {cov.lines_covered} / {cov.lines_valid} |")
    for cov in python:
        out.append(f"| Python tools (`kya`, MCP server) | {pct(cov.line_rate)} | {pct(cov.branch_rate)} | {cov.lines_covered} / {cov.lines_valid} |")
    if not dotnet and not python:
        out.append("| (no coverage files given) | | | |")

    files = [(f, c, v) for cov in dotnet + python for f, (c, v) in cov.files.items() if v > 0]
    lowest = sorted(files, key=lambda x: (x[1] / x[2], -x[2]))[:8]
    if lowest:
        out.append("\nLowest-covered files:\n")
        out.append("| File | Line coverage |")
        out.append("|---|---:|")
        for f, c, v in lowest:
            short = f.split("/src/")[-1] if "/src/" in f else f
            out.append(f"| `{short}` | {pct(100.0 * c / v)} ({c}/{v}) |")

    out.append("\n### Complexity\n")
    if functions is None:
        out.append("_`lizard` is not installed; complexity was not measured._")
        return "\n".join(out) + "\n"
    total_nloc = sum(f.nloc for f in functions)
    avg_ccn = sum(f.ccn for f in functions) / len(functions) if functions else 0
    over_ccn = [f for f in functions if f.ccn > 10]
    over_nloc = [f for f in functions if f.nloc > 50]
    out.append("| Metric | Value |")
    out.append("|---|---:|")
    out.append(f"| Functions | {len(functions)} |")
    out.append(f"| Function NLOC (total) | {total_nloc} |")
    out.append(f"| Average cyclomatic complexity (CCN) | {avg_ccn:.2f} |")
    out.append(f"| Max CCN | {max((f.ccn for f in functions), default=0)} |")
    out.append(f"| Functions with CCN > 10 | {len(over_ccn)} |")
    out.append(f"| Functions longer than 50 NLOC | {len(over_nloc)} |")
    out.append(f"| Functions with more than 5 parameters | {sum(f.params > 5 for f in functions)} |")
    out.append("\nMost complex functions:\n")
    out.append("| Function | File | CCN | NLOC |")
    out.append("|---|---|---:|---:|")
    for f in sorted(functions, key=lambda f: (-f.ccn, -f.nloc))[:10]:
        out.append(f"| `{f.name}` | `{f.file}:{f.line}` | {f.ccn} | {f.nloc} |")
    return "\n".join(out) + "\n"


def check(dotnet: list[Coverage], python: list[Coverage], functions: list[Function] | None) -> list[str]:
    failures: list[str] = []
    by_name = {("dotnet", c.name): c for c in dotnet} | {("python", "*"): c for c in python}
    measured_kinds = {"dotnet"} if dotnet else set()
    measured_kinds |= {"python"} if python else set()
    for (kind, name, metric), minimum in THRESHOLDS.items():
        if kind not in measured_kinds:
            continue  # that report wasn't given in this run
        cov = by_name.get((kind, name))
        if cov is None:
            # A report was given but a component is missing: its tests didn't produce coverage.
            failures.append(f"{kind} {name}: no coverage data in the report")
            continue
        value = cov.line_rate if metric == "line" else cov.branch_rate
        if value < minimum:
            failures.append(f"{kind} {name} {metric} coverage {value:.1f}% < {minimum:.1f}%")
    for f in functions or []:
        if f.ccn > MAX_CCN:
            failures.append(f"{f.name} ({f.file}:{f.line}) has CCN {f.ccn} > {MAX_CCN}")
        if f.nloc > MAX_FUNCTION_NLOC:
            failures.append(f"{f.name} ({f.file}:{f.line}) has {f.nloc} NLOC > {MAX_FUNCTION_NLOC}")
        if f.params > MAX_PARAMETERS:
            failures.append(f"{f.name} ({f.file}:{f.line}) has {f.params} parameters > {MAX_PARAMETERS}")
    return failures


def update_report(path: Path, section: str) -> None:
    text = path.read_text(encoding="utf-8")
    if BEGIN not in text or END not in text:
        raise SystemExit(f"{path} has no {BEGIN} / {END} markers")
    head, rest = text.split(BEGIN, 1)
    _, tail = rest.split(END, 1)
    path.write_text(f"{head}{BEGIN}\n{section}{END}{tail}", encoding="utf-8")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dotnet", type=Path, action="append", default=[], help="Cobertura file from .NET tests")
    parser.add_argument("--python", type=Path, action="append", default=[], help="coverage.xml from pytest-cov")
    parser.add_argument("--update", type=Path, help="report to rewrite between the metrics markers")
    parser.add_argument("--summary", type=Path, help="file to append the generated section to")
    parser.add_argument("--check", action="store_true", help="fail when thresholds are not met")
    args = parser.parse_args(argv)

    dotnet = [c for p in args.dotnet for c in read_cobertura(p, group_by_package=True)]
    python = [c for p in args.python for c in read_cobertura(p, group_by_package=False)]
    functions = measure_complexity()
    section = render(dotnet, python, functions)

    if args.update:
        update_report(args.update, section)
    if args.summary:
        with args.summary.open("a", encoding="utf-8") as summary:
            summary.write("## Code metrics\n\n" + section)
    if not args.update and not args.summary:
        sys.stdout.write(section)

    if args.check:
        failures = check(dotnet, python, functions)
        for failure in failures:
            print(f"::error::{failure}", file=sys.stderr)
        return 1 if failures else 0
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
