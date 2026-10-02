# Unreal Engine 5 Support Guide

UE4Decompiler provides robust support for Unreal Engine 5 projects (UE 5.0 through 5.5), handling modern packaging formats, Zen containers, unversioned properties, and editor re-import pipelines.

---

## Supported UE5 Technologies

### 1. IoStore Containers (`.utoc` & `.ucas`)
UE5 games package assets into IoStore container files (`.utoc` table of contents and `.ucas` container archive chunks). UE4Decompiler mounts these containers seamlessly via CUE4Parse's IoStore reader, handling Zen format chunk compression (Oodle, Zstd, LZ4).

### 2. Unversioned Property Serialization (`.usmap`)
UE5 games frequently strip property name tags and types from cooked packages to reduce build size. UE4Decompiler supports loading `.usmap` mapping files to reconstruct the property schema and deserialize all asset variables accurately.

### 3. High-Fidelity JSON IR & Media Pipeline
Because Unreal Engine 5 introduces major binary architecture changes (Nanite data structures, Zen Store chunk layouts, virtual textures), writing raw uncooked UE5 `.uasset` files directly is neither lossless nor editor-stable.

Instead, UE4Decompiler employs the industry-standard recovery pipeline:
1. **JSON Intermediate Representation (IR)**: Deserializes the full object graph, parameters, components, and properties into structured JSON models (`.json` sidecars).
2. **Lossless Media Export**: Decodes static and skeletal meshes to glTF 2.0 (`.glb`) with full bone hierarchies and vertex attributes; decodes textures to PNG; extracts audio streams.
3. **Commandlet Re-Import**: The recovered JSON IR and media files are imported into clean Unreal Engine 5 editor projects using automated Python scripts or the Unreal Editor `JsonAssetImport` commandlet.

---

## UE5 Asset Classes Handled

- **Blueprints**: Full Kismet bytecode decompilation into Pseudo-Blueprint source, Graphviz DOT, and Mermaid flowcharts.
- **Nanite & Skeletal Meshes**: Extracted to glTF 2.0 (`.glb`) with materials and LODs.
- **Materials & Material Instances**: Full expression trees and scalar/vector/texture parameter graphs serialized to JSON IR and HLSL.
- **World Partition & Levels**: Placed actor transforms, spatial grids, and component references preserved in level JSON models.
- **Enhanced Input & Control Rig**: Asset definitions and mapping contexts preserved in JSON models.
