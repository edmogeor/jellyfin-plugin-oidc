#!/usr/bin/env sh
set -eu

mkdir -p .tls
if [ ! -f .tls/tls.key ] || ! openssl x509 -checkend 60 -noout -in .tls/tls.crt >/dev/null 2>&1; then
  openssl req -x509 -newkey rsa:2048 -nodes -days 2 \
    -keyout .tls/tls.key \
    -out .tls/tls.crt \
    -subj '/CN=jellyfin' \
    -addext 'subjectAltName=DNS:localhost,DNS:oidc.localhost,IP:127.0.0.1'
fi
cp .tls/tls.crt .tls/ca.crt
base64 -d > .tls/oidc-profile.png <<'EOF'
iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScL1dQAAAABJRU5ErkJggg==
EOF
# Keycloak runs as a non-root container user on GitHub-hosted runners.
chmod 644 .tls/tls.key
