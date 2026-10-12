#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
./scripts/setup.sh
set -a
source .env
set +a
export Parameters__workspacePassword="$UPTIME_PASSWORD"
exec dotnet run --project src/UptimeKing.AppHost
