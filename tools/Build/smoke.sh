#!/usr/bin/env bash
# Pack the Build package, then use its PackPlugin target on a throwaway plugin.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

dotnet pack "$here/Hosihikari.Plugin.Build.csproj" -c Release -o "$work/packages" --nologo

if command -v cygpath >/dev/null 2>&1; then
  work_path=$(cygpath -m "$work")
else
  work_path=$work
fi
cat > "$work/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$work_path/packages" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="Hosihikari.Plugin.Build" />
    </packageSource>
    <packageSource key="nuget">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

plugin="$work/TestPlugin"
mkdir -p "$plugin"

cat > "$plugin/TestPlugin.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>TestPlugin</AssemblyName>
    <RootNamespace>TestPlugin</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Hosihikari.Plugin.Abstractions" Version="[3.0.0]" ExcludeAssets="runtime" />
    <PackageReference Include="Hosihikari.Plugin.Build" Version="1.2.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
EOF

cat > "$plugin/Plugin.cs" <<'EOF'
using Hosihikari;

[assembly: EntryPoint<TestPlugin.Plugin>]

namespace TestPlugin;

public sealed class Plugin : IPlugin
{
    public ValueTask OnLoad(IPluginContext context, CancellationToken token) => ValueTask.CompletedTask;
    public ValueTask<PluginHookResult> OnEnable(CancellationToken token) =>
        ValueTask.FromResult(PluginHookResult.Allow());
    public ValueTask<PluginHookResult> OnDisable(CancellationToken token) =>
        ValueTask.FromResult(PluginHookResult.Allow());
    public ValueTask OnUnload(CancellationToken token) => ValueTask.CompletedTask;
}
EOF

cat > "$plugin/hosihikari.json" <<'EOF'
{
  "schemaVersion": 1,
  "name": "TestPlugin",
  "entry": "TestPlugin.dll",
  "version": "1.0.0",
  "loader": "assembly",
  "kind": "plugin",
  "apiVersion": 3,
  "dependencies": [ { "name": "hosihikari" } ]
}
EOF

dotnet restore "$plugin/TestPlugin.csproj" --nologo
dotnet build "$plugin/TestPlugin.csproj" -t:PackPlugin -c Release --nologo --no-restore \
  -p:HosihikariPluginOutputRoot="$plugin/dist"

test -f "$plugin/dist/TestPlugin/TestPlugin.dll"
test -f "$plugin/dist/TestPlugin/hosihikari.json"
# 信封由打包器生成，不是作者清单的复制品；apiVersion 缺省值必须已经是 3。
grep -q '"type": "hosihikari"' "$plugin/dist/TestPlugin/manifest.json"
grep -q '"apiVersion": 3' "$plugin/dist/TestPlugin/hosihikari.json"
if find "$plugin/dist/TestPlugin" -name 'Hosihikari.Plugin.Abstractions.dll' | grep -q .; then
  echo 'shared contract leaked into the plugin package' >&2
  exit 1
fi

# 多目标项目的外层没有具体 TargetPath；必须给 -f，且错误要可读。
multi="$work/MultiPlugin"
cp -r "$plugin" "$multi"
rm -rf "$multi/bin" "$multi/obj" "$multi/dist"
sed -i 's#<TargetFramework>net10.0</TargetFramework>#<TargetFrameworks>net10.0</TargetFrameworks>#' \
  "$multi/TestPlugin.csproj"
dotnet restore "$multi/TestPlugin.csproj" --nologo
if output=$(dotnet build "$multi/TestPlugin.csproj" -t:PackPlugin -c Release --nologo --no-restore 2>&1); then
  echo 'multi-target PackPlugin should fail without -f' >&2
  exit 1
fi
if ! printf '%s' "$output" | grep -q -- '-f <tfm>'; then
  echo 'multi-target PackPlugin did not explain the required -f' >&2
  printf '%s\n' "$output" >&2
  exit 1
fi

dotnet build "$multi/TestPlugin.csproj" -t:PackPlugin -c Release --nologo --no-restore \
  -f net10.0 -p:HosihikariPluginOutputRoot="$multi/dist"
test -f "$multi/dist/TestPlugin/TestPlugin.dll"

echo 'PackPlugin smoke passed'
