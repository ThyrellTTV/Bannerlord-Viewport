# Bannerlord Viewport

Standalone Windows prototype for browsing Mount & Blade II: Bannerlord `.tpac` asset packages and previewing mesh candidates in a native 3D viewport.

## Current state

- Select an `AssetPackages` folder.
- Recursively scans for `.tpac` files.
- Uses the vendored TpacTool parser to read TPAC headers.
- Shows parsed `Metamesh` meshes and `Geometry` records on the left.
- Renders a deterministic placeholder model in the right-side `Viewport3D`.
- Supports mouse orbit and wheel zoom.

Actual mesh rendering is still pending. The app now knows which model records exist inside the package, but it still renders a placeholder until the TPAC vertex stream is mapped into the WPF viewport mesh format.

## Run

```powershell
dotnet run
```
