#!/usr/bin/env bash
set -Eeuo pipefail

# AutoPalExplorer API 15 build script
# Usage:
#   ./build.sh              # Release build
#   ./build.sh Debug        # Debug build
#   ./build.sh Release clean # Clean then build

CONFIGURATION="${1:-Release}"
ACTION="${2:-build}"
PROJECT_PATH="${PROJECT_PATH:-AutoPalExplorer/AutoPalExplorer.csproj}"
SOLUTION_PATH="${SOLUTION_PATH:-AutoPalExplorer.sln}"

# 版本相关配置
PAL_JSON="${PAL_JSON:-../Pal/Pal.json}"       # 分发清单，构建成功后同步版本号
INTERNAL_NAME="${INTERNAL_NAME:-AutoPalExplorer}"
VERSION_SCRIPT="scripts/sync-version.py"
# VERSION=1.2.0.0 ./build.sh   -> 使用指定版本（不自增）
# SKIP_VERSION_SYNC=1 ./build.sh -> 不修改 csproj / Pal.json

cd "$(dirname "$0")"

PYTHON="$(command -v python || command -v python3 || true)"

log() {
  printf '\n[AutoPalExplorer build] %s\n' "$1"
}

fail() {
  printf '\n[AutoPalExplorer build] ERROR: %s\n' "$1" >&2
  exit 1
}

command -v dotnet >/dev/null 2>&1 || fail "dotnet was not found. Install .NET 10 SDK first."

[[ -f "$PROJECT_PATH" ]] || fail "Project file not found: $PROJECT_PATH. Run this script from the extracted project root."

if ! dotnet --list-sdks | grep -qE '^10\.'; then
  fail "No .NET 10 SDK found. Current SDKs:\n$(dotnet --list-sdks)"
fi

TFM="$(grep -oPm1 '(?<=<TargetFramework>)[^<]+' "$PROJECT_PATH" || true)"
TFM="${TFM:-net10.0-windows7.0}"
OUTPUT_DIR="$(dirname "$PROJECT_PATH")/bin/$CONFIGURATION/$TFM"

log "Using project: $PROJECT_PATH"
log "Configuration: $CONFIGURATION"
log "Target framework: $TFM"

if [[ "$ACTION" == "clean" || "$ACTION" == "--clean" ]]; then
  log "Cleaning previous build output"
  dotnet clean "$PROJECT_PATH" -c "$CONFIGURATION"
  rm -rf "$(dirname "$PROJECT_PATH")/bin/$CONFIGURATION" "$(dirname "$PROJECT_PATH")/obj/$CONFIGURATION"
fi

# 计算本次构建的版本号
BUILD_VERSION="${VERSION:-}"
if [[ -z "$BUILD_VERSION" ]]; then
  if [[ -n "$PYTHON" && -f "$VERSION_SCRIPT" ]]; then
    BUILD_VERSION="$("$PYTHON" "$VERSION_SCRIPT" --csproj "$PROJECT_PATH" --pal "$PAL_JSON" --internal-name "$INTERNAL_NAME" next)"
  else
    log "WARNING: python 或 $VERSION_SCRIPT 缺失，无法自增版本，使用 csproj 中的固定版本"
  fi
fi
[[ -n "$BUILD_VERSION" ]] && log "Version: $BUILD_VERSION"

log "Restoring NuGet packages"
dotnet restore "$PROJECT_PATH"

log "Building"
if [[ -n "$BUILD_VERSION" ]]; then
  dotnet build "$PROJECT_PATH" -c "$CONFIGURATION" --no-restore -p:Version="$BUILD_VERSION"
else
  dotnet build "$PROJECT_PATH" -c "$CONFIGURATION" --no-restore
fi

log "Build finished"

# 构建成功后，把版本号写回 csproj 与 Pal.json
if [[ -n "$BUILD_VERSION" && -z "${SKIP_VERSION_SYNC:-}" ]]; then
  if [[ -n "$PYTHON" && -f "$VERSION_SCRIPT" ]]; then
    "$PYTHON" "$VERSION_SCRIPT" --csproj "$PROJECT_PATH" --pal "$PAL_JSON" --internal-name "$INTERNAL_NAME" set "$BUILD_VERSION"
    log "已同步版本 $BUILD_VERSION 到 $PROJECT_PATH 与 $PAL_JSON"
  fi
fi

if [[ -d "$OUTPUT_DIR" ]]; then
  echo "Output folder: $OUTPUT_DIR"
  echo "Expected plugin files:"
  ls -1 "$OUTPUT_DIR" | grep -E 'AutoPalExplorer\.(dll|json)|ECommons\.dll|\.zip$' || true
else
  echo "Output folder was not found at expected path: $OUTPUT_DIR"
  echo "Searching build outputs:"
  find "$(dirname "$PROJECT_PATH")/bin" -maxdepth 5 -type f \( -name 'AutoPalExplorer.dll' -o -name '*.zip' \) 2>/dev/null || true
fi

ZIP_FILE="$(find "$(dirname "$PROJECT_PATH")/bin/$CONFIGURATION" -maxdepth 5 -type f -name '*.zip' 2>/dev/null | head -n 1 || true)"
if [[ -n "$ZIP_FILE" ]]; then
  echo "Plugin zip: $ZIP_FILE"
fi

log "Done"
