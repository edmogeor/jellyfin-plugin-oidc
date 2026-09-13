#!/usr/bin/env sh
set -eu

mkdir -p .tls
openssl req -x509 -newkey rsa:2048 -nodes -days 2 \
  -keyout .tls/tls.key \
  -out .tls/tls.crt \
  -subj '/CN=jellyfin' \
  -addext 'subjectAltName=DNS:localhost,DNS:oidc.localhost,IP:127.0.0.1'
cp .tls/tls.crt .tls/ca.crt
# Keycloak runs as a non-root container user on GitHub-hosted runners.
chmod 644 .tls/tls.key
