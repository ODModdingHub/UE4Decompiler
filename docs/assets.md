# Asset Types & Recovery Capabilities

UE4Decompiler supports deep extraction and reconstruction across all major Unreal Engine asset classes.

---

## 1. Static Meshes (`UStaticMesh`)
- **glTF 2.0 Export (`.glb`)**: Full vertex positions, normals, UV channels, vertex colors, and materials.
- **FRawMesh Serialization**: For UE 4.21 projects, builds uncooked `FRawMesh` binary payloads and inlines geometry into loadable `.uasset` files.
- **LOD Support**: Preserves all Level of Detail (LOD) models extracted from containers.

---

## 2. Skeletal Meshes (`USkeletalMesh`)
- **Rigging & Bones**: Exports bone hierarchies and rest poses to glTF 2.0 (`.glb`).
- **Bone Weights**: Preserves per-vertex bone influence weights.
- **Morph Targets**: Extracts facial animation and shape blend keys into glTF morph targets.

---

## 3. Textures (`UTexture2D`, `UTextureCube`)
- **Transcoding**: Decodes cooked texture mips (DXT1/5, BC7, ASTC, ETC2) to lossless PNG format.
- **Binary Uncooked Packages**: Generates editor-loadable texture packages with source art payloads.
- **Dimension Control**: Configurable maximum resolution (`--tex-max`) for accelerated extraction.

---

## 4. Materials & Material Instances (`UMaterial`, `UMaterialInstanceConstant`)
- **Expression Graphs**: Preserves scalar, vector, and texture parameter overrides in structured JSON models.
- **Shader Code**: Extracts compiled HLSL pixel and vertex shader stages where present in the container.
- **Template Reskinning**: Reskins base material templates with recovered parameter values for editor rendering.

---

## 5. Levels & Maps (`UWorld`, `ULevel`)
- **Actor Hierarchies**: Extracts placed actors, component hierarchies, and spatial coordinates.
- **Transform Serialization**: Saves precise world transform matrices (Translation, Rotation, Scale) to JSON models.
- **Actor Synthesis**: Re-synthesizes placed actors onto clean level templates.

---

## 6. Audio (`USoundWave`)
- **Audio Extraction**: Extracts PCM, OGG Vorbis, and Bink audio streams into playable `.wav` and `.ogg` files.
- **Properties**: Preserves volume curves, attenuation settings, and sound cues in JSON models.

---

## 7. Tables & Structures (`UDataTable`, `UStringTable`, `UUserDefinedStruct`)
- **Data Tables**: Exports table rows into standard JSON and CSV files.
- **String Tables**: Exports localization keys, namespaces, and strings.
- **Structs**: Generates corresponding C++ struct headers with reflected `UPROPERTY` declarations.
