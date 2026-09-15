#!/usr/bin/env bash
# Build the plugin, package the release zip and write its checksum into manifest.json.
#
#   ./build.sh              # version taken from jf-featureenhance/Directory.Build.props
#   ./build.sh 1.0.0.0      # explicit version
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$ROOT/jf-featureenhance/Jellyfin.Plugin.FeatureEnhance/Jellyfin.Plugin.FeatureEnhance.csproj"
OUT_DIR="$ROOT/jf-featureenhance/Jellyfin.Plugin.FeatureEnhance/bin/Release/net10.0"

VERSION="${1:-}"
if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/jf-featureenhance/Directory.Build.props" | head -1)"
fi
[ -n "$VERSION" ] || { echo "could not determine version" >&2; exit 1; }

# dotnet is not always on PATH (e.g. installed into ~/.dotnet by the install script)
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi
command -v dotnet >/dev/null 2>&1 || { echo "dotnet SDK not found (need .NET 10)" >&2; exit 1; }

echo "==> building Feature Enhance $VERSION"
dotnet build "$PROJ" -c Release

mkdir -p "$ROOT/dist"
ZIP="$ROOT/dist/Jellyfin.Plugin.FeatureEnhance_${VERSION}.zip"
rm -f "$ZIP"

python3 - "$OUT_DIR/Jellyfin.Plugin.FeatureEnhance.dll" "$ZIP" <<'PY'
import sys, zipfile
dll, zip_path = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED) as z:
    z.write(dll, 'Jellyfin.Plugin.FeatureEnhance.dll')
PY

MD5="$(md5sum "$ZIP" | cut -d' ' -f1)"
echo "==> $ZIP"
echo "    md5: $MD5"
python3 "$ROOT/scripts/update_manifest.py" --version "$VERSION" --checksum "$MD5"
echo "==> done. Tag and publish with:"
echo "    git tag v$VERSION && git push origin v$VERSION"
