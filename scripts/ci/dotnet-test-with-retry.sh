#!/usr/bin/env bash
# Retry dotnet test when xUnit.net v3 out-of-process discovery flakes on Windows CI
# (GetAssemblyInfo returns only the arch preamble JSON).
set -euo pipefail

if (($# == 0)); then
  echo "usage: dotnet-test-with-retry.sh <dotnet test args...>" >&2
  exit 2
fi

max_attempts="${DOTNET_TEST_MAX_ATTEMPTS:-2}"
attempt=1
log="$(mktemp)"
trap 'rm -f "$log"' EXIT

while ((attempt <= max_attempts)); do
  set +e
  dotnet "$@" 2>&1 | tee "$log"
  exit_code="${PIPESTATUS[0]}"
  set -e

  if ((exit_code == 0)); then
    exit 0
  fi

  if ((attempt >= max_attempts)); then
    exit "$exit_code"
  fi

  if ! grep -qE 'Test process did not return valid JSON|Catastrophic failure' "$log"; then
    exit "$exit_code"
  fi

  echo "dotnet test failed with xUnit discovery handshake error (attempt ${attempt}/${max_attempts}); retrying in 15s..." >&2
  sleep 15
  attempt=$((attempt + 1))
done
