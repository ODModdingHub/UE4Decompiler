#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

VERSION="${1:-}"
OUTPUT_FILE="${2:-}"

if [[ -z "$VERSION" ]]; then
    VERSION=$(grep -E '<Version>[0-9]+\.[0-9]+\.[0-9]+' "$ROOT_DIR/Directory.Build.props" | sed -E 's/.*<Version>([0-9]+\.[0-9]+\.[0-9]+)<\/Version>.*/\1/' || echo "2.0.0")
fi

# Strip leading 'v' if provided
VERSION="${VERSION#v}"
TAG="v$VERSION"
DATE="$(date +'%Y-%m-%d')"

# Find the previous git tag, or if none exists, use the root commit
PREV_TAG="$(git describe --tags --abbrev=0 2>/dev/null || echo "")"

if [[ -n "$PREV_TAG" ]]; then
    COMMIT_RANGE="$PREV_TAG..HEAD"
    RANGE_DESC="Changes since $PREV_TAG"
else
    COMMIT_RANGE="HEAD"
    RANGE_DESC="Initial Release / Full History"
fi

TEMP_LOG="$(mktemp)"
git log "$COMMIT_RANGE" --pretty=format:"%h%x09%s%x09%an" > "$TEMP_LOG"

FEAT_LIST=""
FIX_LIST=""
PERF_LIST=""
DOC_LIST=""
REFACTOR_LIST=""
OTHER_LIST=""

while IFS=$'\t' read -r hash subject author; do
    [[ -z "$hash" ]] && continue
    line="- [\`$hash\`] $subject ($author)"

    case "$subject" in
        feat*|Feat*)
            FEAT_LIST+="$line"$'\n'
            ;;
        fix*|Fix*)
            FIX_LIST+="$line"$'\n'
            ;;
        perf*|Perf*)
            PERF_LIST+="$line"$'\n'
            ;;
        docs*|Docs*)
            DOC_LIST+="$line"$'\n'
            ;;
        refactor*|Refactor*|style*|Style*)
            REFACTOR_LIST+="$line"$'\n'
            ;;
        *)
            OTHER_LIST+="$line"$'\n'
            ;;
    esac
done < "$TEMP_LOG"
rm -f "$TEMP_LOG"

CHANGELOG_MD=""
CHANGELOG_MD+="## [$TAG] - $DATE"$'\n\n'
CHANGELOG_MD+="> $RANGE_DESC"$'\n\n'

if [[ -n "$FEAT_LIST" ]]; then
    CHANGELOG_MD+="### 🚀 Features & Enhancements"$'\n'
    CHANGELOG_MD+="$FEAT_LIST"$'\n'
fi

if [[ -n "$FIX_LIST" ]]; then
    CHANGELOG_MD+="### 🐛 Bug Fixes & Stability"$'\n'
    CHANGELOG_MD+="$FIX_LIST"$'\n'
fi

if [[ -n "$PERF_LIST" ]]; then
    CHANGELOG_MD+="### ⚡ Performance & Codecs"$'\n'
    CHANGELOG_MD+="$PERF_LIST"$'\n'
fi

if [[ -n "$REFACTOR_LIST" ]]; then
    CHANGELOG_MD+="### 🛠️ Architecture & Refactoring"$'\n'
    CHANGELOG_MD+="$REFACTOR_LIST"$'\n'
fi

if [[ -n "$DOC_LIST" ]]; then
    CHANGELOG_MD+="### 📚 Documentation"$'\n'
    CHANGELOG_MD+="$DOC_LIST"$'\n'
fi

if [[ -n "$OTHER_LIST" ]]; then
    CHANGELOG_MD+="### 📦 Other Changes"$'\n'
    CHANGELOG_MD+="$OTHER_LIST"$'\n'
fi

if [[ -n "$OUTPUT_FILE" ]]; then
    mkdir -p "$(dirname "$OUTPUT_FILE")"
    echo "$CHANGELOG_MD" > "$OUTPUT_FILE"
    echo "==> Changelog written to $OUTPUT_FILE"
else
    echo "$CHANGELOG_MD"
fi
