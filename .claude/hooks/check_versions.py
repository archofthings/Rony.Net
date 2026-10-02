#!/usr/bin/env python3
"""PostToolUse hook: keep the package version in one place.

All packages take their <Version> from `Directory.Build.props`. After a `src/*/*.csproj` file was edited, checks
that no project under `src/` sets its own <Version>. If one does, prints it to stderr and exits 2, so Claude
removes it. The release workflow checks the same thing, but only after the tag was pushed.
"""
import glob
import json
import os
import re
import sys

try:
    payload = json.load(sys.stdin)
except ValueError:
    sys.exit(0)

file_path = (payload.get("tool_input") or {}).get("file_path") or ""
project_dir = os.path.realpath(os.environ.get("CLAUDE_PROJECT_DIR") or payload.get("cwd") or os.getcwd())
src = os.path.join(project_dir, "src") + os.sep

if not file_path.endswith(".csproj") or not os.path.realpath(file_path).startswith(src):
    sys.exit(0)

overrides = {}
for csproj in sorted(glob.glob(os.path.join(src, "*", "*.csproj"))):
    with open(csproj, encoding="utf-8") as handle:
        match = re.search(r"<Version>([^<]+)</Version>", handle.read())
    if match:
        overrides[os.path.relpath(csproj, project_dir)] = match.group(1)

if overrides:
    print("These projects set their own <Version>; remove it (the version lives in Directory.Build.props):",
          file=sys.stderr)
    for path, version in overrides.items():
        print(f"  {version}  {path}", file=sys.stderr)
    sys.exit(2)
sys.exit(0)
