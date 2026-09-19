# Third-Party Notices

Bannerlord Viewport includes or depends on the following third-party projects. Their original licenses remain in effect for their respective code and binaries.

## TpacTool

- Project: [TpacTool](https://github.com/szszss/TpacTool)
- Author: szszss
- License: MIT
- Use in this project: the TPAC parsing library is vendored under `third_party/TpacTool/TpacTool.Lib`. `third_party/TpacTool/Export` contains adapted TpacTool texture-export code with local DDS correctness fixes.
- License text: `third_party/TpacTool/LICENSE`, distributed as `Licenses/TpacTool.txt`.

TpacTool is an unofficial asset explorer and parser for Mount & Blade II: Bannerlord. Bannerlord Viewport is not an official TpacTool release.

## Item Position Editor

- Project: [Item Position Editor](https://www.nexusmods.com/mountandblade2bannerlord/mods/13041)
- Author: sakinolemir
- Use in this project: behavioral and UX reference for live per-item position, rotation, holster, step, reset, undo, and redo controls.

No Item Position Editor source code, compiled DLL, XML prefab, localization, or other asset is copied or redistributed by Bannerlord Viewport. The desktop implementation and XML-writing code in this repository were written independently for its WPF/HelixToolkit architecture. Item Position Editor remains the work of its author and is subject to the permissions stated on its Nexus Mods page.

## Helix Toolkit

- Project: [Helix Toolkit](https://github.com/helix-toolkit/helix-toolkit)
- Version: 3.1.2
- License: MIT
- Use in this project: WPF/SharpDX 3D viewport and rendering engine.
- License text: `third_party/HelixToolkit/LICENSE`, distributed as `Licenses/HelixToolkit.txt`.

## SharpDX

- Project: [SharpDX](https://github.com/sharpdx/SharpDX)
- Version: 4.2.0
- License: MIT
- Use in this project: DirectX bindings used transitively by Helix Toolkit.
- License text: `third_party/SharpDX/LICENSE`, distributed as `Licenses/SharpDX.txt`.

## AssimpNet and Assimp

- Project: [AssimpNet](https://bitbucket.org/Starnick/assimpnet)
- Version: 5.0.0-beta1
- License: MIT for AssimpNet; 3-clause BSD for Assimp.
- Use in this project: FBX scene export.
- License text: `third_party/AssimpNet/LICENSE.txt`, distributed as `Licenses/AssimpNet.txt`.

## lz4net

- Project: [lz4net](https://github.com/MiloszKrajewski/lz4net)
- Version: 1.0.15.93
- License: 2-clause BSD
- Use in this project: TPAC compression support used by TpacTool.
- License text: `third_party/lz4net/LICENSE`, distributed as `Licenses/lz4net.txt`.

Mount & Blade II: Bannerlord is a product of TaleWorlds Entertainment. This project is an unofficial community tool and is not affiliated with or endorsed by TaleWorlds Entertainment.
