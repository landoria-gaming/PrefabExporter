# Requirements — PrefabExporter

## Goal

Build a desktop application for exploring assets from a local Valheim installation and understanding the references between them. The first use case is to find a prefab such as `ShieldWood` and inspect its linked objects, model, materials, textures, icons, sounds and effects.

## Platform and technology

- Implement the application in C# on a supported .NET version.
- Support Windows.
- Use WPF with C# and XAML for the desktop interface.
- Implement the core logic, game data access and indexing as cross-platform .NET libraries, independent of WPF and Windows-specific APIs where practical.

## Game data

- Store the path to the local Valheim installation in a configuration file, whether installed through Steam or Xbox.
- Index game assets without requiring Unity Editor. Detect and record the Unity version from the game files.
- Read the installation path directly from the configuration file. The user edits the file to set or change it; do not provide a path input or file picker in the interface. Validate the configured path.
- Read game assets without modifying the installation.
- Display the detected game version and associate indexes with the installation and version.
- Inspect the available game data, Unity resources, managed assemblies and relevant external files, not only SoftRef bundles. Validate access to files needed for each operation and report missing, unsupported or unreadable files. Do not assume Steam and Xbox installations have identical layouts.

## Indexing and references

- Provide an Index Assets button and a progress bar. Show the bar only during indexing, fill the remaining width, and use indeterminate progress during the reference audit.
- Prevent indexing when a completed index already exists for the detected game version.
- Show the configured Valheim path and detected game version in a status bar.
- Write binary files `assets-{version}.bin` and `references-{version}.bin`, and a text file `prefabs-{version}.jsonl`. Include the detected game version in each filename.
- Write errors and warnings to a single `log.txt` beside the executable; do not persist indexing status. Display indexing warnings and errors in the text area.
- Audit indexed references after indexing and write unresolved targets, grouped by cause with example sources, to `log.txt`.
- During indexing, build `prefabs-{version}.jsonl`, a compact alphabetical prefab list used by search.
- During indexing, build `assets-{version}.offsets.bin` and `references-{version}.offsets.bin` for direct access to assets and references.
- Do not use SQLite for the initial release. Read and write large files incrementally to limit memory usage.

- Index asset names, types, bundle locations and stable identifiers when available.
- Aim to enumerate every serialized reference precisely, including references nested in arrays, dictionaries, material properties, engine resources and external files. Never silently discard a detected reference.
- Record the source object, field, target collection and target identifier for each reference.
- Resolve targets to names and types when available, including shaders, built-in resources, links across bundles and external files. Retain identifiers and external paths when a target cannot be resolved.
- Retain and display every detected unresolved reference with its source field, target identifiers and external path when available. Explicitly report unreadable objects and coverage gaps; never present incomplete results as exhaustive.
- Preserve objects without references as searchable assets.
- Require a completed full-game indexing pass before allowing asset browsing or searching. Allow exploration with a visible warning when assets are unreadable or references unresolved; record the details. An interrupted or outdated index must not unlock exploration. Show progress, allow cancellation, and support recovery or rebuilding.
- Store indexes under `indexes/<game-version>` next to the executable. Resolve relative index paths against the executable directory and keep installations with the same version separate.
- Handle large datasets without loading the entire index into memory.

## User interface

- Use a top panel with indexing controls and a list of completed indexes on the first line, and prefab search controls on the second line. Select the most recently completed index by default and search the selected index. Keep Export All Files disabled until a future release. Below the controls, use one text area for messages, results and references.
- Use a simple, readable layout and avoid decorative elements or advanced dashboards in the first release.

## Exploration

- Show matching prefab names and paths alphabetically in the text area as soon as two characters are entered, without a search button. To inspect a prefab, select a suggestion or enter its exact name and click Show References; display its details, hierarchy and references in the same text area.

- Search assets by name, path, type, identifier and bundle.
- Show an asset's details and all indexed outgoing references, including shaders, scripts, materials, textures, audio, effects and references to external files or bundles.
- Navigate referenced assets and inspect incoming references where indexed.
- Browse prefab hierarchy and components, including children.
- Follow indirect dependencies without a fixed depth limit, including shader and external references. Detect cycles, stream indexed data, and show a bounded result in the text area without saving a report. Omit unrelated sprites from shared atlases. Allow cancellation and identify each source field and target.
- Filter results by asset type and reference status; handle cycles and deep graphs safely.
- Show prefab meshes, materials, shaders, material texture slots such as albedo and normal maps, and related icons.

## Future scope

- Configure the AssetRipper executable or its directory through `AssetRipperPath` in `appsettings.json`.
- Extract selected prefab icons and textures as PNG and individual meshes as GLB into `output/<prefab>/files`. Keep the assembled FBX in `output/<prefab>`. Report unsupported or missing resources.
- Assemble an FBX for active static meshes with their transforms, color textures and normal maps, using Blender in the background. Report unsupported content.
- Enlarge FBX color textures with nearest-neighbor sampling to reduce smoothing in importers that use linear filtering.

## Acceptance criteria

- The user can configure a Valheim installation path in the configuration file, create an index, search for `ShieldWood`, and display its details in the text area.
- Its prefab hierarchy and four icon references can be inspected.
- Its model, material and the material's referenced textures can be traced through explicit links.
- The application distinguishes unresolved targets from assets with no references.
- An interrupted indexing operation does not appear as a completed index.
- The application remains usable while indexing and can navigate a large index without loading it all into memory.
- The WPF application runs on Windows; core libraries can run on Windows, Linux and macOS when their dependencies support those platforms.

## Open decisions

- The application name is PrefabExporter.
- Decide which warnings should be prominent when an indexing pass completes with unreadable assets or unresolved references.
- Define animation and skinned model support when planning the future export feature.
