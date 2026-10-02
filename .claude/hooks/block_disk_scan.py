#!/usr/bin/env python3
"""PreToolUse hook for Bash: block whole-disk searches.

Agents once left `find /` scans running for over half an hour while looking for library sources.
Blocks `locate`/`mdfind`, and `find`/`bfs` whose search path is `/`, `~`, `$HOME` or any absolute path
outside the project, the NuGet package cache and the temp/scratchpad folders. Searches inside the project stay allowed.
Exit code 2 blocks the command and shows the reason to Claude.
"""
import json
import os
import re
import shlex
import sys

try:
    payload = json.load(sys.stdin)
except ValueError:
    sys.exit(0)

command = (payload.get("tool_input") or {}).get("command") or ""
project = os.path.realpath(os.environ.get("CLAUDE_PROJECT_DIR") or payload.get("cwd") or os.getcwd())
home = os.path.expanduser("~")
allowed_roots = [project, os.path.join(home, ".nuget", "packages"), "/tmp", "/private/tmp", "/var/folders", "/private/var/folders"]

REASON = (
    "Blocked: whole-disk searches are not allowed in this project. NuGet package sources are in "
    "~/.nuget/packages/<package id in lower case>/<version>/; search there or inside the repo instead."
)


def block() -> None:
    print(REASON, file=sys.stderr)
    sys.exit(2)


def outside_allowed(path: str) -> bool:
    if path in ("/", "~") or path.startswith(("~", "$HOME", "${HOME}")):
        expanded = os.path.expanduser(path.replace("${HOME}", home).replace("$HOME", home))
    else:
        expanded = path
    if not os.path.isabs(expanded):
        return False  # relative paths stay inside the current (project) directory
    real = os.path.realpath(expanded)
    return not any(real == root or real.startswith(root + os.sep) for root in allowed_roots)


# Split on shell separators so `cd x && find / ...` is caught too.
for segment in re.split(r"&&|\|\||[;|\n]", command):
    try:
        tokens = shlex.split(segment)
    except ValueError:
        tokens = segment.split()
    if not tokens:
        continue
    program = os.path.basename(tokens[0])
    if program in ("locate", "mdfind", "plocate"):
        block()
    if program in ("find", "bfs", "fd", "gfind"):
        # Search paths are the arguments before the first expression/option.
        for arg in tokens[1:]:
            if arg.startswith("-") or arg in ("(", "!"):
                break
            if outside_allowed(arg):
                block()

sys.exit(0)
