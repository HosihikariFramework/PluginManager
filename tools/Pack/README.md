# Hosihikari.Plugin.Pack

Command-line packer for Hosihikari plugin packages. It validates the manifest, restores
`#:package` dependencies at pack time, and copies `#:include` files, so the server never downloads
packages while loading a plugin.

```bash
dotnet tool install --global Hosihikari.Plugin.Pack
hosihikari-pack pack-plugin <source directory> <manifest.json> <output root>
```

Other modes: `pack-loader` packs a loader build output, and `pack-plugin-from-assembly` packs a
precompiled plugin assembly.
