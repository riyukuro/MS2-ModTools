# tools/toolbench: Gamebryo Toolbench under Wine

`tools/run-toolbench.sh` runs the prebuilt Toolbench + World Builder from **your own** Gamebryo LightSpeed 3.2 SDK
(`MS2_GAMEBRYO_APPDIR`), headless under 64-bit Wine, with the 3D viewport working. Nothing is built from source except a 30-line
C# shim. The script's header lists its options.

`--setup` (once, about 1 h, mostly dotnet48) makes working copies under `<MS2_OUT>`, a Wine prefix with its winetricks packages,
registers fonts and builds the shim. Your SDK folder is not written to. Needs: wine, winetricks, Xvfb, xdotool, ImageMagick,
fontconfig, and `pefile` for python3.

Four fixes beyond the prerequisites, all in the script:

1. **CLR4 retarget + shim.** No .NET 3.5 is usable in Wine, so the `.exe.config` files are retargeted to CLR4 (`.orig` kept). On
   CLR4 a resource lookup recurses until stack overflow; `ToolbenchLauncher.cs` sets the invariant culture and starts
   `ToolbenchWindowed.exe` in-process.
2. **Fonts.** WPF only sees fonts registered in the Wine registry, and it stops at once without an Arial. If setup warns about
   `arial.ttf`, run `winetricks corefonts`.
3. **Large address aware.** The shim is the process image, so it needs the flag, or a real map (15k entities) runs out of memory
   at 2 GB.
4. **Palette.** On CLR4 leaving any list of the Palette View (open a library, search, pick a folder) ended Toolbench with a
   NullReferenceException; the shim passes over that one exception (the reason is in `ToolbenchLauncher.cs`). The error
   `Unable to find "WPFToolkit.aero..."` in the Output View is harmless: CLR4 asks for a theme assembly the SDK never had.
   A setup made before this fix needs the shim built again (run the setup again).

Things that are easy to get wrong:

- Open a `.gsl` game solution. Toolbench refuses a `.block` or `.emtproj` on the command line.
- Toolbench writes into the content root on save, so work on a copy, with real `Exported` directories rather than links to your
  extracted data.
- Windows is not tested. Toolbench is built for .NET 3.5, so the Wine fixes above shouldn't apply there.

| tool | what |
|---|---|
| `mkgsl.py ROOT [NAME.gsl]` | writes a solution listing every project and block under ROOT; ROOT needs `asset-web-config/` from `APPDIR/Media` |
| `ms2media.py` | gathers the textures and NIFs a map needs from your client's `Resource/Model/*.m2d` (`MS2_CLIENT_DATA`) |
| `nifcheck.py` | checks MS2 NIFs against the stock Gamebryo 3.2 stream format |
| `ToolbenchLauncher.cs` | the CLR4 shim |
