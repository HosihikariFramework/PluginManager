# Hosihikari.Plugin.Pack

Command-line packer for Hosihikari plugin packages. It validates the manifest, restores
`#:package` dependencies at pack time, and copies `#:include` files, so the server never downloads
packages while loading a plugin.

```bash
dotnet tool install --global Hosihikari.Plugin.Pack
hosihikari-pack pack-plugin <source directory> <hosihikari.json> <output root>
```

Other modes: `pack-loader` packs a loader build output, `pack-plugin-from-assembly` packs a
precompiled plugin assembly, and `migrate <package directory>` turns a 1.x `manifest.json` package
into a `hosihikari.json` one.

The packed package carries the author's `hosihikari.json` plus a generated `manifest.json`
envelope, which is the only thing LeviLamina reads.
