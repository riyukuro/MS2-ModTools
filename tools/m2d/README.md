# tools/m2d: read and repack .m2d/.m2h archives

Handles the MS2F and NS2F archive formats, through the NuGet package `Maple2.File.Parser.Tadeucci`. Targets .NET 8; on a newer
runtime set `DOTNET_ROLL_FORWARD=Major`. The Python tools build it on first use; by hand:

```sh
dotnet build tools/m2d -c Release -o tools/m2d/bin
```

```sh
dotnet tools/m2d/bin/m2d.dll list <a.m2d>                         # entry names
dotnet tools/m2d/bin/m2d.dll info <a.m2d>                         # archive details
dotnet tools/m2d/bin/m2d.dll cat <a.m2d> <entry>                  # one entry to stdout
dotnet tools/m2d/bin/m2d.dll extract <a.m2d> <outdir> [prefix...]
dotnet tools/m2d/bin/m2d.dll repack <orig.m2d> <srcdir> <out.m2d> [--add]
```

`repack` replaces entries whose names match files under `<srcdir>`, copies the rest raw, and writes the `.m2h` next to the
output. `--add` also adds new entries. `.m2h` headers for NS2F are written in the reader's field order: the library's `Encode`
puts a u64 file count where the reader expects a u32, which made repacked NS2F archives unreadable.
