#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ -f .env ]]; then
  echo '.env already exists; keeping your credentials.'
  exit 0
fi
umask 077
{
  printf 'POSTGRES_PASSWORD=%s\n' "$(openssl rand -hex 24)"
  printf 'UPTIME_PASSWORD=%s\n' "$(openssl rand -hex 16)"
} > .env
echo 'Created .env with unique local credentials. Use UPTIME_PASSWORD to sign in.'
echo 'Start the app with: docker compose up --build -d'
