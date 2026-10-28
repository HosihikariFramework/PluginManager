#!/usr/bin/env bash
# Assemble the managed core release archive. Extracting it at the server root
# installs the core into plugins/hosihikari/.
#
# Usage: bash ./pack.sh <version>
set -euo pipefail

version=${1:?usage: pack.sh <version>}
here=$(cd "$(dirname "$0")" && pwd)
out="$here/artifacts"
publish="$out/publish"
package="$out/package/plugins/hosihikari"

rm -rf "$out"
mkdir -p "$publish" "$package"

dotnet publish "$here/src/Hosihikari.PluginManager.csproj" -c Release -o "$publish" --nologo

cp "$publish"/*.dll "$publish"/*.json "$package/"
if compgen -G "$publish/*.pdb" > /dev/null; then
    cp "$publish"/*.pdb "$package/"
fi

(cd "$out/package" && zip -qr "$out/hosihikari-core-$version.zip" plugins)
echo "$out/hosihikari-core-$version.zip"
