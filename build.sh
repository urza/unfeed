#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
if ! command -v dotnet >/dev/null; then export PATH="$HOME/.dotnet:$PATH"; fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore Feed.slnx --locked-mode -v quiet
dotnet publish src/Feed.Cli -c Release --no-restore -o out -nologo -v quiet
dotnet publish src/Feed.Web -c Release --no-restore -o out -nologo -v quiet
