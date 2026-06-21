#!/usr/bin/env bash
# StyloExtract installer
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/scottgal/styloextract/main/scripts/install.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/scottgal/styloextract/main/scripts/install.sh | bash -s -- --with-playwright
#   STYLOEXTRACT_VERSION=v1.2.0 ./scripts/install.sh
#
# Downloads the latest stylo-extract binary for your platform and installs it to
# /usr/local/bin/stylo-extract. Defaults to the lean AOT build; pass --with-playwright
# to install the larger R2R build that includes JS-rendered fetching.

set -euo pipefail

VERSION="${STYLOEXTRACT_VERSION:-latest}"
INSTALL_DIR="${STYLOEXTRACT_INSTALL_DIR:-/usr/local/bin}"
REPO="scottgal/styloextract"
EDITION="aot"
BIN_PREFIX="stylo-extract"

for arg in "$@"; do
    case "$arg" in
        --with-playwright)
            EDITION="playwright"
            BIN_PREFIX="stylo-extract-playwright"
            ;;
        --aot)
            EDITION="aot"
            BIN_PREFIX="stylo-extract"
            ;;
        *)
            echo "Unknown argument: $arg" >&2
            echo "Usage: install.sh [--with-playwright | --aot]" >&2
            exit 2
            ;;
    esac
done

OS=$(uname -s | tr '[:upper:]' '[:lower:]')
ARCH=$(uname -m)

case "$OS" in
    linux)  PLATFORM="linux"; EXT="tar.gz" ;;
    darwin) PLATFORM="osx"; EXT="tar.gz" ;;
    mingw*|msys*|cygwin*) PLATFORM="win"; EXT="zip" ;;
    *) echo "Unsupported OS: $OS" >&2; exit 1 ;;
esac

case "$ARCH" in
    x86_64|amd64) ARCH_SUFFIX="x64" ;;
    aarch64|arm64) ARCH_SUFFIX="arm64" ;;
    *) echo "Unsupported architecture: $ARCH" >&2; exit 1 ;;
esac

if [ "$PLATFORM" = "win" ] && [ "$ARCH_SUFFIX" = "arm64" ]; then
    echo "Windows arm64 builds are not produced; only win-x64 is available." >&2
    exit 1
fi

RID="${PLATFORM}-${ARCH_SUFFIX}"
ASSET_NAME="${BIN_PREFIX}-${RID}.${EXT}"

echo "============================================"
echo "  StyloExtract installer"
echo "============================================"
echo ""
echo "Edition: ${EDITION}"
echo "Platform: ${RID}"

if [ "$VERSION" = "latest" ]; then
    echo "Resolving latest release..."
    VERSION=$(curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest" | grep '"tag_name"' | sed 's/.*"tag_name": "\(.*\)".*/\1/' || echo "")
    if [ -z "$VERSION" ]; then
        echo "Could not determine latest version. Set STYLOEXTRACT_VERSION=v1.2.0 (or similar) and retry." >&2
        exit 1
    fi
fi

DOWNLOAD_URL="https://github.com/${REPO}/releases/download/${VERSION}/${ASSET_NAME}"
echo "Version: ${VERSION}"
echo "Asset: ${ASSET_NAME}"
echo ""

TEMP_DIR=$(mktemp -d)
trap "rm -rf '$TEMP_DIR'" EXIT

echo "Downloading..."
curl -fsSL -o "${TEMP_DIR}/${ASSET_NAME}" "$DOWNLOAD_URL"

echo "Extracting..."
case "$EXT" in
    tar.gz) tar xzf "${TEMP_DIR}/${ASSET_NAME}" -C "${TEMP_DIR}" ;;
    zip)    unzip -q "${TEMP_DIR}/${ASSET_NAME}" -d "${TEMP_DIR}" ;;
esac

if [ "$PLATFORM" = "win" ]; then
    BIN_NAME="${BIN_PREFIX}.exe"
else
    BIN_NAME="${BIN_PREFIX}"
fi

if [ ! -f "${TEMP_DIR}/${BIN_NAME}" ]; then
    echo "Expected binary ${BIN_NAME} not found in archive." >&2
    ls -la "${TEMP_DIR}" >&2
    exit 1
fi

echo "Installing to ${INSTALL_DIR}/${BIN_NAME}..."
if [ -w "$INSTALL_DIR" ]; then
    cp "${TEMP_DIR}/${BIN_NAME}" "${INSTALL_DIR}/${BIN_NAME}"
    chmod +x "${INSTALL_DIR}/${BIN_NAME}"
else
    sudo cp "${TEMP_DIR}/${BIN_NAME}" "${INSTALL_DIR}/${BIN_NAME}"
    sudo chmod +x "${INSTALL_DIR}/${BIN_NAME}"
fi

if [ "$PLATFORM" = "osx" ]; then
    echo "Clearing macOS quarantine attribute..."
    if [ -w "$INSTALL_DIR" ]; then
        xattr -d com.apple.quarantine "${INSTALL_DIR}/${BIN_NAME}" 2>/dev/null || true
    else
        sudo xattr -d com.apple.quarantine "${INSTALL_DIR}/${BIN_NAME}" 2>/dev/null || true
    fi
fi

echo ""
echo "StyloExtract ${VERSION} (${EDITION}) installed."
echo ""
echo "Verify:    ${BIN_NAME} --help"
echo "Repo:      https://github.com/${REPO}"
echo "Docs:      https://scottgal.github.io/styloextract/"
