#!/usr/bin/env bash
# Assemble the managed core lip package.
#
# Usage: bash ./pack.sh <version>
set -euo pipefail

version=${1:?usage: pack.sh <version>}
here=$(cd "$(dirname "$0")" && pwd)
out="$here/artifacts"
publish="$out/publish"
package="$out/hosihikari"

rm -rf "$out"
mkdir -p "$publish" "$package"

dotnet publish "$here/src/Hosihikari.PluginManager.csproj" -c Release -o "$publish" --nologo

cp "$publish"/*.dll "$publish"/*.json "$package/"
if compgen -G "$publish/*.pdb" > /dev/null; then
    cp "$publish"/*.pdb "$package/"
fi

cat > "$out/manifest.json" <<EOF
{
  "formatVersion": 4,
  "formatUuid": "0c6b977a-64ad-441f-9a29-c7f4b196882f",
  "name": "PluginManager",
  "version": "$version",
  "type": "files",
  "placements": [
    { "src": "hosihikari/", "dest": "plugins/hosihikari/" }
  ]
}
EOF

(cd "$out" && zip -qr "hosihikari-core-$version.zip" hosihikari manifest.json)
echo "$out/hosihikari-core-$version.zip"
