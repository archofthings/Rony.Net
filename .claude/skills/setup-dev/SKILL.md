---
name: setup-dev
description: Check the .NET environment for Rony.Net (SDK, runtime, roll-forward), restore and build the solution, and print the build and test commands and the NuGet source path. Use in a fresh checkout or worktree, when a test run aborts with "framework not found", or after a package reference changed.
---

# Set up the development environment

Run the setup script from the repository root:

```bash
.claude/skills/setup-dev/scripts/setup_dev.sh
```

- It checks that the .NET SDK is version 8 or newer.
- It checks whether the .NET 8 runtime is installed. The test and sample projects target `net8.0`; with only a
  newer runtime they start only when `DOTNET_ROLL_FORWARD=Major` is set. `.claude/settings.json` sets it for
  Claude sessions; the script says so when it is missing in the current shell.
- It restores and builds the whole solution and prints the commands to use afterwards.

Afterwards:

```bash
dotnet build                                   # must stay at 0 warnings
dotnet test --no-build tests/Rony.UnitTests    # one project
dotnet test --no-build tests/Rony.FunctionalTests --filter "FullyQualifiedName~MockTcpServerTests"
dotnet test                                    # full suite, once when the task is finished
```

Read library code from `~/.nuget/packages/<package id in lower case>/<version>/`; never search the whole filesystem.

If the script fails because `dotnet` is missing or too old, tell the user; don't try to install an SDK yourself.
