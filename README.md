# Bannerlord Viewport

Windows desktop toolkit for browsing and rendering Mount & Blade II: Bannerlord `.tpac` assets, assembling troop loadouts and crafted weapons, editing equipment positions, and exporting renders, FBX files, textures, and source XML.

## Current state

- Select an `AssetPackages` folder.
- Recursively scans for `.tpac` files.
- Uses the vendored TpacTool parser to read TPAC headers.
- Shows parsed `Metamesh` meshes and `Geometry` records on the left.
- Renders parsed geometry with PBR materials in a native DirectX viewport.
- Supports mouse orbit and wheel zoom.

Meshes now render with PBR materials, studio lighting, faction colours and animated troop loadouts. Meshes beginning with `clo_` are excluded from parsing.

## Credits and licensing

- [TpacTool](https://github.com/szszss/TpacTool) by szszss is directly vendored and adapted for TPAC parsing and texture export under the MIT License. Its copyright and license text are retained in `third_party/TpacTool/LICENSE` and shipped with the application.
- [Bannerlord Weapon Piece Aligner](https://github.com/haterade22/TAOM/tree/bannerlord-1.4.5/tools/BannerlordCraftingTool), originally built by KEYForce for TAOM, is the basis for Bannerlord Viewport's crafting editor. Its code was adapted and extended with TPAC rendering, troop-loadout integration, and source XML saving under TAOM's MIT License.
- [Item Position Editor](https://www.nexusmods.com/mountandblade2bannerlord/mods/13041) by sakinolemir inspired the live per-item position/rotation/holster editing workflow. No code, DLL, XML, localization, or other asset from that mod is included; Bannerlord Viewport's implementation was written independently for this desktop application.
- Helix Toolkit, SharpDX, AssimpNet/Assimp, and lz4net provide rendering, DirectX, FBX, and TPAC compression support under their respective open-source licenses.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for exact usage, upstream links, copyrights, and license locations. Bannerlord Viewport is an unofficial community tool and is not affiliated with or endorsed by TaleWorlds Entertainment.

## Troop browsing

- The troops tab can open one XML file or a folder. Folder loading includes XML files in subfolders and resolves equipment from their owning modules and dependencies.
- Search filters the troop list live by display name or ID, ignoring case. Filtering leaves the displayed preview and custom loadout unchanged.
- Custom-loadout item selections update the troop render automatically, including held and sheathed weapons. Recognised typed IDs and cleared slots also update the preview; partial search text leaves existing equipment visible. Animation playback continues across live loadout changes.
- Duplicate IDs keep the first entry in sorted file order. Unreadable folder XML files are skipped and reported in the summary; its tooltip lists the errors. The selected troop file/folder is remembered when **Save settings** is enabled.

## Render exports

- The viewport's camera button opens PNG and turntable export controls. Turntables orbit the selected asset, crafted weapon or troop loadout through 360 degrees using the current pose and faction colours.
- Turntable options include resolution, duration, frame rate, direction, start angle (0-360 degrees), elevation, full-rotation framing, grid and output folder. The start angle is absolute rather than inherited from the viewport, defaults to 180 degrees (front for native armour), and applies to single and batch turntables. PNG sequences support transparency. MP4 uses a studio background and requires an FFmpeg executable with `libx264`; select it in the turntable dialog, put `ffmpeg.exe` beside the app, or add it to `PATH`.
- Render files use `mesh_name.png`; turntables use `mesh_name.mp4` or a `mesh_name` PNG-sequence folder, without GUIDs, hashes or timestamps. Optional batch module/package folders also keep their plain names. Existing turntable outputs are skipped in batches or reported as a name conflict for single exports; batch PNGs follow the overwrite checkbox, with duplicate names in the same batch skipped rather than overwriting another mesh. Batch reports use `batch-report.json` / `batch-turntables.json`. Cancelled MP4 files are removed; cancelled PNG sequences retain completed frames and a `turntable.json` manifest. The original camera and animation playback are restored afterwards.
- The assets tab also supports batch PNG rendering of all parsed meshes or the current search results.
- **Batch Turntables** in the assets tab exports a separate 360-degree PNG sequence or MP4 for every mesh in the chosen all-mesh/search scope. Set the output folder, resolution, duration, frame rate, elevation, direction, background and grid. Each mesh is framed for the full orbit; optional module/package folders keep large exports organised. Progress and cancellation are available, failed meshes do not stop the batch, and `batch-turntables.json` records outputs and errors. The original preview and camera are restored afterwards. Batch turntable settings follow **Save settings**.
- Enable **Save settings** to remember PNG, batch and turntable options, including output folders. Valid edits save automatically. No named render presets are created. When saving is disabled, render settings are not restored on startup.

## Asset exports

- The download button beside the viewport's camera button exports the selected asset or troop loadout as FBX, referenced textures, or both. Choose an output folder, PNG/DDS/both, and highest detail or all available LODs for individual assets.
- Troop loadouts export each equipped item's mesh parts separately, including held or sheathed equipment. Choose a rigged bind pose or a static snapshot of the current animation frame. Loadout exports use the displayed highest-detail geometry.
- Human armour assets with recognised body metadata can also include the native human skeleton when it is available. Other rigs are not inferred. Original UVs, normals, available secondary UVs and vertex colours are retained; vertex alpha is cloth data, not an opacity map.
- DDS preserves the original compression, mipmaps and texture arrays. PNG exports the first surface at full resolution with alpha intact. Unsupported PNG formats can still be exported as DDS. Packed metallic/gloss maps and faction masks remain in their original channels; Bannerlord-specific shading is not recreated in FBX.
- Each export creates a uniquely named folder with an `export.json` material/texture manifest and any warnings. Failed or cancelled exports leave no partial folder. FBX uses centimetres with Y up and retains physical scale. Export options are remembered only when **Save settings** is enabled.

## Item positioning

The top-right tools share one compact inspector. Applicable sections start collapsed; opening a section closes the previous one. The header button tucks the whole inspector into a small tools icon and restores the current section when reopened. XML previews inside the offset and equipment-position editors are folded separately. Long sections use a single scroll area on smaller windows.

- Equipping Item0 or Item1 shows the equipment-position editor at the top right of the troops viewport. Select an item and its held or holstered/carried state, then adjust XYZ position in centimetres and XYZ rotation in degrees. Undo/redo is per item; reset affects only the selected attachment state.
- Adjustments follow the item ID across loadouts and are remembered between sessions only when **Save settings** is enabled. Preview state does not alter equipment-roster XML.
- **Copy XML** produces positioning-only fragments or a complete item definition. **Save XML** writes positioning edits directly to the item's source XML, after showing the affected paths for confirmation. Other definitions and unrelated attributes are preserved.
- Crafted weapons expose a **Crafting piece offsets** editor directly in the troops inspector. Select a piece and adjust `piece_offset`, `previous_piece_offset` and `next_piece_offset` in centimetres; the equipped weapon updates on the current troop pose, including held or sheathed states. Edits are per item, support undo/redo and selected-piece/all-piece reset, and follow the same **Save settings** checkbox.
- Saving inline piece edits inserts item-specific copies into the original crafting-piece XML and updates the source item's references. Originals and other items sharing those pieces remain unchanged. **Copy XML** includes the corresponding companion-definition snippets for manual use.
- Crafted weapons get their held frame from crafting data, so generic held position/rotation is disabled. Crafted holster tuning inserts an item-specific template into the source template XML. Holster rotation definitions are added to the loaded Native `item_holsters.xml`, without changing existing shared holsters. The confirmation lists all affected files, including files inside the game install. These writes are not live runtime mod patches.

## Saving source XML

- Troops **Save XML** updates the selected troop's first non-civilian equipment roster, which is also the roster used by the preview. Other variants, civilian gear, mounts, Item2+ slots, skills and other troop metadata are preserved. Pending offsets and crafting-piece edits used by the equipped items are saved along with the loadout. Search filtering does not lose the source troop selection.
- The standalone crafting editor has **Copy XML** and **Save XML**. Saving merges tuned BuildData into each piece's own source definition; these shared edits also affect other weapons using those pieces. When opened from a source weapon via **Open in crafting**, the selected pieces and their scale factors are saved to that weapon's original item XML. Without a source weapon, scale is preview-only and cannot be saved into a crafting-piece definition.
- Each changed file gets a timestamped `.viewport-*.bak` copy beside it. Source saves stage all changes, preserve comments, text indentation, UTF-8 BOM and line endings, and reject files changed on disk while the save was being prepared. XML attribute formatting may be normalised by the serializer. Missing or ambiguous source definitions fail without writing partial snippets over the file.
- Successful saves become the new editing baseline and clear saved per-item preview tuning/history to avoid applying offsets twice after a reload. The **Save settings** checkbox controls local preview settings only; source **Save XML** is an explicit file write regardless of that checkbox.

## Run

```powershell
dotnet run
```

## Export checks

```powershell
dotnet run --project .\tests\AssetExportChecks.csproj
dotnet run --project .\tests\AssetExportChecks.csproj -- --inspector
dotnet run --project .\tests\AssetExportChecks.csproj -- --troops
dotnet run --project .\tests\AssetExportChecks.csproj -- --source-xml
dotnet run --project .\tests\AssetExportChecks.csproj -- --turntables "<game>\Modules\LOTRLOME_Armory\AssetPackages" [ffmpeg.exe]
```

The item-position integration checks require a local game install with the TAOM fixtures:

```powershell
dotnet run --project .\tests\AssetExportChecks.csproj -- --item-positions "<game>\Modules\LOTRLOME_Armory\AssetPackages" "<game>\Modules\TAOM\ModuleData\troops\troops_gondor.xml"
```
