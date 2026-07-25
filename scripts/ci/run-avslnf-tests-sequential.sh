#!/usr/bin/env bash
# Runs each test project in Trackdub.Avalonia.slnf sequentially so CI logs show
# which assembly failed or hung. Keeps Inference.Tests last (heaviest ONNX load).
set -euo pipefail

slnf="${1:-Trackdub.Avalonia.slnf}"
framework_flag="${2:-}"

mapfile -t projects < <(
  dotnet sln "$slnf" list |
    tail -n +2 |
    tr -d '\r' |
    grep -E 'Tests\.csproj$' |
    grep -vi 'Inference\.Tests' |
    sort
)

mapfile -t heavy < <(
  dotnet sln "$slnf" list |
    tail -n +2 |
    tr -d '\r' |
    grep -Ei 'Inference\.Tests\.csproj$'
)

if ((${#heavy[@]} > 0)); then
  projects+=("${heavy[@]}")
fi

for project in "${projects[@]}"; do
  normalized="${project//\\//}"
  echo "::group::dotnet test ${normalized}"
  # shellcheck disable=SC2086
  dotnet test "$normalized" -c Release $framework_flag --no-build -m:1 --verbosity minimal
  echo "::endgroup::"
done
