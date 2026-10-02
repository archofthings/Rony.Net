#!/usr/bin/env bash
# Checks the .NET environment for Rony.Net, restores and builds the solution, and prints the commands to use.
# Usage: .claude/skills/setup-dev/scripts/setup_dev.sh
set -euo pipefail

cd "$(dirname "$0")/../../../.."

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is not installed. Install the .NET 8 SDK (or newer): https://dotnet.microsoft.com/download" >&2
  exit 1
fi

sdk_major=$(dotnet --version | cut -d. -f1)
echo "SDK:      $(dotnet --version)"
if [ "$sdk_major" -lt 8 ]; then
  echo "The solution needs the .NET 8 SDK or newer." >&2
  exit 1
fi

# Test and sample projects target net8.0. Without the .NET 8 runtime they only start with roll-forward.
if dotnet --list-runtimes | grep -q '^Microsoft.NETCore.App 8\.'; then
  echo "Runtime:  .NET 8 is installed"
else
  newest=$(dotnet --list-runtimes | grep '^Microsoft.NETCore.App ' | tail -1 | cut -d' ' -f2)
  echo "Runtime:  .NET 8 is not installed (newest: ${newest:-none}); tests need DOTNET_ROLL_FORWARD=Major"
  if [ "${DOTNET_ROLL_FORWARD:-}" != "Major" ]; then
    echo "          It is not set in this shell. Claude sessions get it from .claude/settings.json;"
    echo "          in your own terminal run: export DOTNET_ROLL_FORWARD=Major"
    export DOTNET_ROLL_FORWARD=Major
  fi
fi

echo
dotnet restore --nologo -v q
dotnet build --no-restore --nologo -v q -clp:NoSummary
echo "Build:    ok"

cat <<EOF

Commands (from the repo root):
  dotnet build                                                      # must stay at 0 warnings
  dotnet test                                                       # full suite
  dotnet test --no-build tests/Rony.UnitTests                       # one project
  dotnet test --no-build tests/Rony.FunctionalTests --filter "FullyQualifiedName~MockTcpServerTests"
  dotnet test --no-build samples/Rony.Samples                       # the README and wiki examples
  dotnet pack --configuration Release --output artifacts            # the four packages (artifacts/ is not tracked)

NuGet package sources: ${NUGET_PACKAGES:-$HOME/.nuget/packages}/<package id in lower case>/<version>/
EOF
