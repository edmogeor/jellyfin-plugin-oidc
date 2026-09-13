#!/usr/bin/env sh
set -eu

docker compose down -v >/dev/null
./up.sh
printf 'Running OIDC browser tests...\n'
npm test
