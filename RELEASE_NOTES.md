# Bannerlord Viewport v0.1.0 Beta 2

This first team-testing release expands Bannerlord Viewport into a practical TPAC, troop, crafting, positioning, and export tool.

Beta 2 clarifies source provenance and includes the complete third-party notices and license files in the release package. Application behavior is unchanged from Beta 1.

## Highlights

- Browse TPAC assets with textured PBR materials, faction colour blending, studio lighting, search, and batch rendering.
- Load individual troop XML files or folders, preview complete custom loadouts, play Bannerlord animations, and edit held or holstered equipment positions directly on the troop.
- Edit crafting-piece selections and offsets, copy generated XML, or safely save changes back to source XML with timestamped backups.
- Export selected assets and troop loadouts as FBX with referenced PNG/DDS textures.
- Export transparent or studio PNG renders, configurable 360-degree turntables, and batch turntables. MP4 export supports FFmpeg when supplied separately.
- Save render and editor settings when **Save settings** is enabled.

## Testing Focus

Please pay particular attention to:

- TPAC packages or materials that fail to parse or render correctly.
- Troop loadouts with unusual weapon combinations, holsters, shields, bows, polearms, or crafted weapons.
- Source XML save results and generated backups.
- FBX and texture exports in your preferred 3D application.
- Batch render and turntable naming, framing, cancellation, and performance on large asset sets.

## Requirements

- Windows x64.
- A local Mount & Blade II: Bannerlord installation or extracted module data to browse.
- FFmpeg is optional and only required for MP4 turntable export. PNG sequences work without it.

This is a prerelease intended for internal testing. Back up mod source files before testing direct XML saves.
