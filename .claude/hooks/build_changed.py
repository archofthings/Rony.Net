#!/usr/bin/env python3
"""PostToolUse hook: build the project of a C# file right after it was edited or written.

Reads the hook payload from stdin. For a `.cs` or `.csproj` file inside the repository, finds the nearest
`.csproj` and builds it. On compiler errors or warnings, prints them to stderr and exits 2, so Claude sees them
and fixes the file straight away (CI and the 0-warnings rule would fail on them later). Does nothing for other
files, files outside the project, or when `dotnet` is not installed.
"""
import json
import os
import re
import shutil
import subprocess
import sys

MAX_LINES = 30

try:
    payload = json.load(sys.stdin)
except ValueError:
    sys.exit(0)

file_path = (payload.get("tool_input") or {}).get("file_path") or ""
project_dir = os.environ.get("CLAUDE_PROJECT_DIR") or payload.get("cwd") or os.getcwd()
dotnet = shutil.which("dotnet")

if not file_path.endswith((".cs", ".csproj")) or not os.path.isfile(file_path) or not dotnet:
    sys.exit(0)

real_file = os.path.realpath(file_path)
real_project_dir = os.path.realpath(project_dir)
if not real_file.startswith(real_project_dir + os.sep):
    sys.exit(0)
if any(f"{os.sep}{part}{os.sep}" in real_file for part in ("bin", "obj")):
    sys.exit(0)


def owning_project(path: str) -> str:
    """Return the nearest .csproj at or above `path`, or an empty string."""
    if path.endswith(".csproj"):
        return path
    folder = os.path.dirname(path)
    while folder.startswith(real_project_dir):
        candidates = sorted(name for name in os.listdir(folder) if name.endswith(".csproj"))
        if candidates:
            return os.path.join(folder, candidates[0])
        folder = os.path.dirname(folder)
    return ""


csproj = owning_project(real_file)
if not csproj:
    sys.exit(0)

try:
    result = subprocess.run(
        [dotnet, "build", csproj, "--nologo", "-v", "q", "-clp:NoSummary"],
        cwd=real_project_dir,
        capture_output=True,
        text=True,
        timeout=110,
    )
except subprocess.TimeoutExpired:
    sys.exit(0)

# One line per diagnostic; the same diagnostic appears once per target framework.
diagnostics = []
for line in result.stdout.splitlines():
    if re.search(r": (error|warning) [A-Z]+\d+", line):
        line = re.sub(r" \[[^\]]*\.csproj(::TargetFramework=[^\]]*)?\]$", "", line.strip())
        line = line.replace(real_project_dir + os.sep, "")
        if line not in diagnostics:
            diagnostics.append(line)

if not diagnostics and result.returncode == 0:
    sys.exit(0)

relative = os.path.relpath(csproj, real_project_dir)
print(f"dotnet build of {relative} reported problems (the build must have 0 errors and 0 warnings):", file=sys.stderr)
if diagnostics:
    print("\n".join(diagnostics[:MAX_LINES]), file=sys.stderr)
    if len(diagnostics) > MAX_LINES:
        print(f"... and {len(diagnostics) - MAX_LINES} more", file=sys.stderr)
else:
    print("\n".join(result.stdout.strip().splitlines()[-MAX_LINES:]), file=sys.stderr)
sys.exit(2)
