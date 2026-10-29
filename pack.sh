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

# 包根的 sei.json 是分发元数据，安装时被跳过，不落进服务端根目录。
cat > "$out/package/sei.json" <<EOF
{
  "schemaVersion": 1,
  "name": "hosihikari",
  "version": "$version",
  "dependencies": [],
  "layout": "root"
}
EOF

(cd "$out/package" && zip -qr "$out/hosihikari-core-$version.zip" plugins sei.json)
echo "$out/hosihikari-core-$version.zip"
