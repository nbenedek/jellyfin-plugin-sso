#!/usr/bin/env bash
# Read-only negative-access checks. Never prints credentials or changes settings.
set -euo pipefail
if [[ $# -ne 2 ]]; then
  echo "Usage: $0 https://jellyfin.example.org provider-name" >&2
  exit 2
fi
BASE="${1%/}"
PROVIDER="$2"
case "$BASE" in
  https://*|http://localhost:*|http://127.0.0.1:*) ;;
  *) echo "Refusing a non-HTTPS remote endpoint" >&2; exit 2 ;;
esac
if [[ ! "$PROVIDER" =~ ^[a-zA-Z0-9._-]+$ ]]; then
  echo "Provider must consist of letters, digits, dots, hyphens or underscores" >&2
  exit 2
fi

CODE=0
http_code() {
  curl --silent --show-error --output /dev/null --max-time 15 --write-out '%{http_code}' "$@"
}
check() {
  local label="$1" expected="$2" got
  shift 2
  got="$(http_code "$@")"
  if [[ " $expected " == *" $got "* ]]; then
    printf 'PASS %-36s HTTP %s\n' "$label" "$got"
  else
    printf 'FAIL %-36s HTTP %s (expected %s)\n' "$label" "$got" "$expected"
    CODE=1
  fi
}

check "Enabled provider list" "200" "$BASE/sso/OID/GetNames"
check "Unknown provider" "400" "$BASE/sso/OID/start/invalid-provider-does-not-exist"
check "Missing callback state" "400" "$BASE/sso/OID/redirect/$PROVIDER"
check "Invented exchange token" "401" -X POST -H 'Content-Type: application/json' --data '{"data":"not-a-real-token"}' "$BASE/sso/OID/Auth/$PROVIDER"
check "Malformed JSON" "400" -X POST -H 'Content-Type: application/json' --data '{' "$BASE/sso/OID/Auth/$PROVIDER"
check "Self-service linking disabled" "403" "$BASE/sso/OID/start/$PROVIDER?isLinking=true"
check "Admin provider list requires login" "401 403" "$BASE/sso/OID/Get"
check "Admin provider deletion rejects GET" "405" "$BASE/sso/OID/Del/$PROVIDER"
exit "$CODE"
