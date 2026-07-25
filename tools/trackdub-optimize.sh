#!/usr/bin/env bash
# Optimize a Trackdub model with Microsoft Olive (optimize) on the current machine.
#
# Usage:
#   ./tools/trackdub-optimize.sh -- --model openai/whisper-tiny --model-root whisper-tiny-genai --no-benchmark

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

# ---------------------------------------------------------------------------
# Resolve managed venv path per OS
# ---------------------------------------------------------------------------
OS_TYPE="$(uname -s)"
case "$OS_TYPE" in
    Darwin)
        DATA_DIR="$HOME/Library/Application Support/trackdub"
        ;;
    Linux)
        DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/trackdub"
        ;;
    *)
        echo "Unsupported OS: $OS_TYPE" >&2
        exit 1
        ;;
esac

VENV_PATH="$DATA_DIR/tools/olive-env"
PYTHON_EXE="$VENV_PATH/bin/python"
PIP_EXE="$VENV_PATH/bin/pip"
OLIVE_EXE="$VENV_PATH/bin/olive"

# ---------------------------------------------------------------------------
# 1. Ensure Python 3.10+
# ---------------------------------------------------------------------------
find_python() {
    for candidate in python3 python; do
        if command -v "$candidate" &>/dev/null; then
            ver=$("$candidate" --version 2>&1 | grep -oP '(?<=Python )\d+\.\d+' || true)
            if [ -n "$ver" ]; then
                major="${ver%%.*}"; minor="${ver##*.}"
                if [ "$major" -gt 3 ] || { [ "$major" -eq 3 ] && [ "$minor" -ge 10 ]; }; then
                    echo "$candidate"
                    return 0
                fi
            fi
        fi
    done
    return 1
}

if ! SYSTEM_PYTHON="$(find_python)"; then
    if [ "$OS_TYPE" = "Darwin" ] && command -v brew &>/dev/null; then
        echo "Python not found — installing via Homebrew…"
        brew install python@3.11
        SYSTEM_PYTHON="$(find_python)" || {
            echo "Python 3.10+ still not found after brew install." >&2
            echo "Install from https://python.org and retry." >&2
            exit 1
        }
    elif [ "$OS_TYPE" = "Darwin" ]; then
        echo "Python 3.10+ not found. Install Homebrew (https://brew.sh) or Python from https://python.org." >&2
        exit 1
    else
        echo "Python 3.10+ not found. Install with:" >&2
        echo "  Ubuntu/Debian:  sudo apt install python3.11" >&2
        echo "  Fedora/RHEL:    sudo dnf install python3.11" >&2
        exit 1
    fi
fi

# ---------------------------------------------------------------------------
# 2. Create venv if absent
# ---------------------------------------------------------------------------
if [ ! -f "$PYTHON_EXE" ]; then
    echo "Creating venv at $VENV_PATH…"
    mkdir -p "$(dirname "$VENV_PATH")"
    "$SYSTEM_PYTHON" -m venv "$VENV_PATH"
fi

# ---------------------------------------------------------------------------
# 3. Upgrade pip, then install / upgrade olive-ai
# ---------------------------------------------------------------------------
echo "Upgrading pip…"
"$PYTHON_EXE" -m pip install --upgrade pip --quiet

echo "Installing olive-ai…"
"$PIP_EXE" install olive-ai --quiet --upgrade

# ---------------------------------------------------------------------------
# 4. Locate Trackdub.Tools
# ---------------------------------------------------------------------------
PUBLISHED_BIN="$REPO_ROOT/src/Trackdub.Tools/bin/Trackdub.Tools"
if [ -f "$PUBLISHED_BIN" ]; then
    TOOLS_PREFIX=("$PUBLISHED_BIN")
else
    TOOLS_PROJECT="$REPO_ROOT/src/Trackdub.Tools/Trackdub.Tools.csproj"
    if [ ! -f "$TOOLS_PROJECT" ]; then
        echo "Cannot locate Trackdub.Tools (tried $PUBLISHED_BIN and dotnet run)." >&2
        exit 1
    fi
    TOOLS_PREFIX=(dotnet run --project "$TOOLS_PROJECT" --)
fi

# ---------------------------------------------------------------------------
# 5. Invoke modellab — forward remaining args
# ---------------------------------------------------------------------------
echo ""
echo "Running: ${TOOLS_PREFIX[*]} modellab --python $PYTHON_EXE --olive $OLIVE_EXE $*"
echo ""

"${TOOLS_PREFIX[@]}" modellab --python "$PYTHON_EXE" --olive "$OLIVE_EXE" "$@"
