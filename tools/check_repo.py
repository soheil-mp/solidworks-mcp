"""Forbidden-content guard for the public repository (run in CI and before every push).

Fails when tracked files include Dassault Systemes binaries or dumps, native SOLIDWORKS files, build logs, or text
that leaks local machine details (absolute user paths, e-mail addresses other than the noreply/example ones).

    python tools/check_repo.py          # checks `git ls-files`
"""
import re
import subprocess
import sys

FORBIDDEN_NAMES = [
    r"SolidWorks\.Interop\..*\.dll$", r"\.tlb$", r"\.chm$", r"_api\.txt$", r"_dump\.txt$", r"swconst_enums\.txt$",
    r"swcommands\.txt$", r"\.binlog$", r"\.(sldprt|sldasm|slddrw|slddrt|sldlfp)$", r"\.nupkg$", r"\.mcpb$",
]
TEXT_EXT = (".cs", ".md", ".py", ".json", ".props", ".targets", ".csproj", ".slnx", ".ps1", ".txt", ".yml", ".yaml", ".xml")
LEAKS = [
    (re.compile(r"[A-Za-z]:\\\\?Users\\\\?(?!<|%|\$|USERNAME|you|name)[A-Za-z0-9_.-]+", re.I), "absolute user path"),
    (re.compile(r"[A-Za-z0-9._%+-]+@(?!users\.noreply\.github\.com|example\.com|anthropic\.com)[A-Za-z0-9.-]+\.[a-z]{2,}"), "e-mail address"),
]
ALLOW_LEAK_FILES = {"tools/check_repo.py"}


def main() -> int:
    files = subprocess.run(["git", "ls-files"], capture_output=True, text=True, check=True).stdout.splitlines()
    problems = []
    for f in files:
        for pat in FORBIDDEN_NAMES:
            if re.search(pat, f, re.I):
                problems.append(f"{f}: forbidden file type ({pat})")
        if f.endswith(TEXT_EXT) and f not in ALLOW_LEAK_FILES:
            try:
                text = open(f, encoding="utf-8", errors="replace").read()
            except OSError:
                continue
            for rx, what in LEAKS:
                for m in rx.finditer(text):
                    line = text.count("\n", 0, m.start()) + 1
                    problems.append(f"{f}:{line}: {what}: {m.group(0)[:60]}")
    for p in problems:
        print("check_repo:", p)
    print("check_repo:", "ok" if not problems else f"{len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
