#!/usr/bin/env bash
# Clear the macOS Gatekeeper quarantine attribute from a downloaded StyloExtract binary.
# Usage:
#   ./clear-quarantine.sh                       # clears ./stylo-extract and ./stylo-extract-playwright if present
#   ./clear-quarantine.sh path/to/binary        # clears the given file
#
# Required because GitHub downloads on macOS are tagged with com.apple.quarantine. Until
# stripped, Gatekeeper refuses to run unsigned binaries from outside the App Store.

set -euo pipefail

clear_one() {
    local target="$1"
    if [ ! -e "$target" ]; then
        echo "Skip: ${target} (not found)" >&2
        return
    fi
    if xattr -l "$target" 2>/dev/null | grep -q "com.apple.quarantine"; then
        xattr -d com.apple.quarantine "$target"
        chmod +x "$target"
        echo "Cleared: $target"
    else
        chmod +x "$target"
        echo "Already clean: $target"
    fi
}

if [ $# -eq 0 ]; then
    clear_one "./stylo-extract"
    clear_one "./stylo-extract-playwright"
else
    for arg in "$@"; do
        clear_one "$arg"
    done
fi
