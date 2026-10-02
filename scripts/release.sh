#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

VERSION=""
ALL_PLATFORMS=false
NO_BUILD=false
DRY_RUN=false

for arg in "$@"; do
    case "$arg" in
        --all-platforms)
            ALL_PLATFORMS=true
            ;;
        --no-build)
            NO_BUILD=true
            ;;
        --dry-run)
            DRY_RUN=true
            ;;
        v[0-9]*|[0-9]*)
            VERSION="${arg#v}"
            ;;
    esac
done

if [[ -z "$VERSION" ]]; then
    VERSION=$(grep -E '<Version>[0-9]+\.[0-9]+\.[0-9]+' "$ROOT_DIR/Directory.Build.props" | sed -E 's/.*<Version>([0-9]+\.[0-9]+\.[0-9]+)<\/Version>.*/\1/' || echo "2.0.0")
fi

TAG="v$VERSION"
DIST_DIR="$ROOT_DIR/dist"
RELEASES_DIR="$DIST_DIR/releases"
RELEASE_NOTES="$DIST_DIR/RELEASE_NOTES.md"
CHANGELOG_FILE="$ROOT_DIR/CHANGELOG.md"

echo "=========================================================================="
echo " UE4Decompiler Automated Release Pipeline"
echo " Target Version: $TAG"
if [[ "$DRY_RUN" == true ]]; then
    echo " Mode: DRY-RUN (simulating actions, no files or tags modified)"
fi
echo "=========================================================================="

mkdir -p "$DIST_DIR" "$RELEASES_DIR"

# 1. Generate Changelog & Release Notes
echo "==> [1/5] Generating release changelog..."
"$SCRIPT_DIR/generate-changelog.sh" "$VERSION" "$RELEASE_NOTES"

if [[ "$DRY_RUN" == false ]]; then
    if [[ -f "$CHANGELOG_FILE" ]]; then
        # Prepend to existing CHANGELOG.md if not already there
        if ! grep -q "## \[$TAG\]" "$CHANGELOG_FILE"; then
            TEMP_CHANGELOG="$(mktemp)"
            cat "$RELEASE_NOTES" > "$TEMP_CHANGELOG"
            echo "" >> "$TEMP_CHANGELOG"
            cat "$CHANGELOG_FILE" >> "$TEMP_CHANGELOG"
            mv "$TEMP_CHANGELOG" "$CHANGELOG_FILE"
            echo "==> Prepended release notes for $TAG to CHANGELOG.md"
        fi
    else
        # Create CHANGELOG.md
        echo "# UE4Decompiler Changelog" > "$CHANGELOG_FILE"
        echo "" >> "$CHANGELOG_FILE"
        cat "$RELEASE_NOTES" >> "$CHANGELOG_FILE"
        echo "==> Initialized CHANGELOG.md with $TAG"
    fi
else
    echo "==> [Dry-run] Would update CHANGELOG.md with release notes"
fi

# 2. Update Directory.Build.props if version changed
CURRENT_PROPS_VER=$(grep -E '<Version>[0-9]+\.[0-9]+\.[0-9]+' "$ROOT_DIR/Directory.Build.props" | sed -E 's/.*<Version>([0-9]+\.[0-9]+\.[0-9]+)<\/Version>.*/\1/' || echo "")
if [[ "$CURRENT_PROPS_VER" != "$VERSION" && -n "$CURRENT_PROPS_VER" ]]; then
    if [[ "$DRY_RUN" == false ]]; then
        echo "==> [2/5] Updating Directory.Build.props version to $VERSION..."
        sed -i.bak -E "s/<Version>[0-9]+\.[0-9]+\.[0-9]+<\/Version>/<Version>$VERSION<\/Version>/" "$ROOT_DIR/Directory.Build.props"
        sed -i.bak -E "s/<AssemblyVersion>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+<\/AssemblyVersion>/<AssemblyVersion>$VERSION.0<\/AssemblyVersion>/" "$ROOT_DIR/Directory.Build.props"
        sed -i.bak -E "s/<FileVersion>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+<\/FileVersion>/<FileVersion>$VERSION.0<\/FileVersion>/" "$ROOT_DIR/Directory.Build.props"
        rm -f "$ROOT_DIR/Directory.Build.props.bak"
    else
        echo "==> [2/5] [Dry-run] Would update Directory.Build.props version to $VERSION"
    fi
else
    echo "==> [2/5] Directory.Build.props matches target version ($VERSION)"
fi

# 3. Build & Package Target Binaries
if [[ "$NO_BUILD" == false && "$DRY_RUN" == false ]]; then
    echo "==> [3/5] Publishing and packaging binaries..."

    RIDS=()
    if [[ "$ALL_PLATFORMS" == true ]]; then
        RIDS=("osx-arm64" "osx-x64" "linux-x64" "win-x64")
    else
        # Determine host RID
        ARCH=$(uname -m)
        OS=$(uname -s)
        if [[ "$OS" == "Darwin" ]]; then
            if [[ "$ARCH" == "arm64" ]]; then RIDS=("osx-arm64"); else RIDS=("osx-x64"); fi
        elif [[ "$OS" == "Linux" ]]; then
            RIDS=("linux-x64")
        else
            RIDS=("win-x64")
        fi
    fi

    for rid in "${RIDS[@]}"; do
        echo "--> Building for $rid..."
        "$SCRIPT_DIR/publish.sh" "$rid" "Release" "$DIST_DIR/$rid"

        # Create archives
        CLI_ARCHIVE="$RELEASES_DIR/UE4Decompiler-$TAG-$rid-cli"
        GUI_ARCHIVE="$RELEASES_DIR/UE4Decompiler-$TAG-$rid-gui"

        if [[ "$rid" == win* ]]; then
            if command -v 7z &>/dev/null; then
                (cd "$DIST_DIR/$rid/cli" && 7z a -r "$CLI_ARCHIVE.zip" . >/dev/null)
                (cd "$DIST_DIR/$rid/gui" && 7z a -r "$GUI_ARCHIVE.zip" . >/dev/null)
            elif command -v zip &>/dev/null; then
                (cd "$DIST_DIR/$rid/cli" && zip -r -q "$CLI_ARCHIVE.zip" .)
                (cd "$DIST_DIR/$rid/gui" && zip -r -q "$GUI_ARCHIVE.zip" .)
            fi
        else
            tar -czf "$CLI_ARCHIVE.tar.gz" -C "$DIST_DIR/$rid/cli" .
            tar -czf "$GUI_ARCHIVE.tar.gz" -C "$DIST_DIR/$rid/gui" .
        fi
    done

    # Generate SHA-256 Checksums
    echo "--> Calculating checksums..."
    CHECKSUM_FILE="$RELEASES_DIR/checksums.sha256"
    rm -f "$CHECKSUM_FILE"
    (
        cd "$RELEASES_DIR"
        for f in *; do
            [[ -f "$f" && "$f" != "checksums.sha256" ]] || continue
            if command -v shasum &>/dev/null; then
                shasum -a 256 "$f" >> checksums.sha256
            elif command -v sha256sum &>/dev/null; then
                sha256sum "$f" >> checksums.sha256
            fi
        done
    )
    echo "==> Checksums generated at $CHECKSUM_FILE"
else
    echo "==> [3/5] Skipping build (NO_BUILD=$NO_BUILD, DRY_RUN=$DRY_RUN)"
fi

# 4. Git Tagging
echo "==> [4/5] Checking Git Tag: $TAG..."
if git rev-parse "$TAG" >/dev/null 2>&1; then
    echo "--> Tag $TAG already exists locally."
else
    if [[ "$DRY_RUN" == false ]]; then
        git tag -a "$TAG" -m "Release $TAG"
        echo "--> Created git tag: $TAG"
    else
        echo "--> [Dry-run] Would create git tag: $TAG"
    fi
fi

# 5. Create GitHub Release
echo "==> [5/5] Creating GitHub Release..."
if command -v gh &>/dev/null && gh auth status &>/dev/null; then
    if [[ "$DRY_RUN" == false ]]; then
        echo "--> Publishing release to GitHub via gh CLI..."
        ASSETS=()
        for a in "$RELEASES_DIR"/*; do
            [[ -f "$a" ]] && ASSETS+=("$a")
        done

        if [[ ${#ASSETS[@]} -gt 0 ]]; then
            gh release create "$TAG" "${ASSETS[@]}" \
                --title "UE4Decompiler $TAG" \
                --notes-file "$RELEASE_NOTES"
        else
            gh release create "$TAG" \
                --title "UE4Decompiler $TAG" \
                --notes-file "$RELEASE_NOTES"
        fi
        echo "==> Release $TAG published successfully to GitHub!"
    else
        echo "--> [Dry-run] Would run: gh release create $TAG dist/releases/* --title 'UE4Decompiler $TAG' --notes-file $RELEASE_NOTES"
    fi
else
    echo ""
    echo "=========================================================================="
    echo " Release Assets and Tag prepared successfully!"
    echo " Tag: $TAG"
    echo " Release Notes: $RELEASE_NOTES"
    echo " Release Artifacts:"
    ls -lh "$RELEASES_DIR" || true
    echo ""
    echo " To publish to GitHub:"
    echo "   1. Push your tag:  git push origin $TAG"
    echo "   2. GitHub Actions will automatically publish the release and attach assets."
    echo "      (Or manually upload artifacts from dist/releases/ to GitHub Releases)"
    echo "=========================================================================="
fi
