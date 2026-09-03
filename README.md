# HarfBuzzFix

A small Vintage Story client-side mod that fixes an intermittent crash caused by
[libGUI](https://mods.vintagestory.at/libgui) on Linux/glibc systems.

## The bug

libGUI bundles its own copy of `HarfBuzzSharp` and loads it via `NativeLibrary.SetDllImportResolver`
without any symbol isolation. On systems where something else in the process (in Vintage Story's
case, its own bundled SkiaSharp/Skia pipeline pulling in the system `libharfbuzz.so.0` transitively)
has already loaded a different build of HarfBuzz, the dynamic linker resolves libGUI's internal
`hb_*` calls to the wrong, ABI-incompatible symbols instead of its own bundled copy. That silently
corrupts the heap until a later `free()` aborts the whole process — usually surfacing as a crash on
world join or when opening any dialog that triggers font shaping (e.g. the character panel).

This has nothing to do with any particular desktop environment — it reproduces on any glibc Linux
system where a system HarfBuzz build gets loaded into the process ahead of libGUI's own copy.
See [ripls56/vslibgui#2](https://github.com/ripls56/vslibgui/issues/2) for the upstream report.

## The fix

Rather than racing to register a competing `DllImportResolver` (which collides with libGUI's own
registration and breaks its startup entirely — `NativeLibrary.SetDllImportResolver` only allows one
resolver per assembly), this mod uses Harmony to patch `Gui.NativeLibraryLoader.Register()` directly.
The patch skips libGUI's own (unisolated) registration and substitutes an equivalent one that loads
the bundled native library via `dlopen()` with `RTLD_NOW | RTLD_DEEPBIND`, so its internal symbol
lookups stay bound to itself regardless of what else the process has already loaded.

- Linux-only; no-ops on Windows/macOS.
- No-ops if libGUI isn't installed.
- Falls through to libGUI's original (working, just unisolated) loader if anything about the patch
  fails, so it can't leave you worse off than not having this mod at all.
- `RTLD_DEEPBIND` is a glibc extension; on musl-based systems the flag is a silent no-op and this mod
  provides no benefit there (also not a regression — the underlying interposition mechanism this
  fixes is largely glibc-specific to begin with).

## Install

Drop the built mod zip into your Vintage Story `Mods` folder like any other mod. Requires libGUI
(`gui`) to also be installed.

## Building

Requires the .NET SDK and references `VintagestoryAPI.dll` and `0Harmony.dll` from your Vintage
Story install (see `HarfBuzzFix.csproj` for paths — adjust to your install location).

```
dotnet build -c Release
```

## License

MIT — see [LICENSE](LICENSE).
