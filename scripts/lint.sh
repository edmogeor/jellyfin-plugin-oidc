#!/usr/bin/env sh
set -eu

dotnet tool restore >/dev/null
dotnet build Jellyfin.Plugin.Oidc.slnx
dotnet jb inspectcode --no-build --swea --format=Text --output=- --LogLevel=OFF --verbosity=OFF Jellyfin.Plugin.Oidc.slnx
npm --prefix tests/e2e run lint
