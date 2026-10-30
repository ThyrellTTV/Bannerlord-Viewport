# Bannerlord Viewport v0.1.0 Beta 3

This prerelease adds turntable exports for crafted weapons and fixes export failures on scaled displays and weapons with nonvisual crafting pieces.

## New Since Beta 2

- Export the current custom weapon build directly from the Crafting tab as an MP4 video or PNG sequence.
- Load a crafted-item XML file or folder and batch export assembled weapons using their piece references, scales, and offsets.
- Keep separate crafted-weapon batch settings when **Save settings** is enabled. Existing outputs are skipped, and `batch-crafted-turntables.json` records each item's result.
- Find the crafting export buttons immediately below the source paths, above Weapon Template and Pieces.

## Fixes And Credits

- Correct PNG and turntable capture sizing on scaled Windows displays, including the viewport resize restoration that could eventually fail with `E_INVALIDARG`. Thanks to yotthani for the contributed fix and report.
- Treat intentionally empty crafting meshes, such as the native "None" polearm pommel, as nonvisual pieces instead of failing the entire weapon export. Their offsets remain part of assembly calculations.
- Include the crafting piece ID in errors for genuinely missing weapon meshes.
- Credit KEYForce's Bannerlord Weapon Piece Aligner as the basis for the crafting editor and include TAOM's MIT license in the release package.

## Testing Focus

Please pay particular attention to:

- Crafted weapon assembly, including custom scales, offsets, and empty/default pieces.
- Single and batch turntable exports, skipped outputs, failure reports, cancellation, and performance on large item sets.
- PNG and turntable exports at 125%, 150%, and 175% Windows display scaling. The contributor verified the DPI fix at 150%; other scaling factors still need real-display testing.

## Requirements

- Windows x64.
- Extract the complete ZIP and run `Bannerlord Viewport.exe`. The .NET runtime is included.
- A local Mount & Blade II: Bannerlord installation or extracted module data to browse.
- FFmpeg is optional and only required for MP4 turntable export. PNG sequences work without it.

This is a prerelease intended for internal testing. Back up mod source files before testing direct XML saves.
