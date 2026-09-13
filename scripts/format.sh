#!/usr/bin/env sh
set -eu

dotnet tool restore >/dev/null
dotnet csharpier format
npm --prefix tests/e2e run format
