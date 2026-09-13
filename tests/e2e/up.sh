#!/usr/bin/env sh
set -eu

fail() {
  printf '\nSetup failed. Recent service logs:\n' >&2
  docker compose ps >&2
  docker compose logs --tail=40 jellyfin-server keycloak-server caddy >&2
  exit 1
}

printf 'Preparing local TLS certificate...\n'
./prepare-tls.sh
printf 'Publishing the plugin...\n'
docker compose run --rm plugin-build || fail
printf 'Starting Jellyfin and Keycloak...\n'
docker compose up -d --wait --wait-timeout 120 keycloak-server || fail
docker compose up -d --wait --wait-timeout 120 --no-deps --force-recreate jellyfin-server || fail
printf 'Configuring OIDC test settings...\n'
docker compose run --rm configure || fail
printf 'Restarting Jellyfin to load OIDC settings...\n'
docker compose restart jellyfin-server || fail
docker compose up -d --wait --wait-timeout 120 --no-deps jellyfin-server || fail
printf 'Starting local TLS proxy at https://localhost:8443...\n'
docker compose up -d --no-deps caddy || fail
printf 'Waiting for the proxied Jellyfin server...\n'
curl --fail --insecure --retry 30 --retry-all-errors --retry-delay 1 https://localhost:8443/health >/dev/null || fail
