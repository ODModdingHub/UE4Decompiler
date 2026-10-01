# Unreal Engine 4 Support Guide

UE4Decompiler provides native parsing and binary reconstruction support across Unreal Engine 4 versions (4.10 through 4.27).

---

## Engine Version Matrix

| Version | Container Formats | Binary `.uasset` Writing | Media Export (glTF/PNG) | C++ Stubs |
| :---: | :---: | :---: | :---: | :---: |
| **UE 4.10 - 4.20** | Traditional `.pak` | Placeholder Header + JSON IR | Yes | Yes |
| **UE 4.21** | Traditional `.pak` | **Full Editor-Loadable Uncooked** | Yes | Yes |
| **UE 4.22 - 4.25** | Traditional `.pak` | Editor-Loadable 4.21 structure | Yes | Yes |
| **UE 4.26 - 4.27** | `.pak` + IoStore (`.utoc/.ucas`) | Editor-Loadable 4.21 structure | Yes | Yes |

---

## Unreal 4.21 Binary Uncooked Package Writer

For assets targeting Unreal 4.21 (and compatible 4.x versions), UE4Decompiler includes a hand-crafted binary package writer (`UncookedPackageWriter`) that produces valid uncooked `.uasset` packages:
- **`FPackageFileSummary` Generation**: Writes package tags (`0x9E2A83C1`), legacy file versions (`-8`), engine changelists, and package flags.
- **`FCrc` Serialized Name Hashes**: Computes non-case-preserving and case-preserving CRC32 hashes matching engine algorithms, ensuring the in-editor linker correctly buckets name pool entries.
- **Import & Export Table Construction**: Serializes `FObjectImport` and `FObjectExport` tables with valid outer chains and type paths.
- **`FRawMesh` Geometry Inlining**: Translates cooked mesh geometry into uncooked `FRawMesh` bulk payloads, allowing static meshes to open and render in the Unreal Editor.
- **Texture Transcoding**: Decodes cooked texture mips to PNG and re-encodes uncooked DXT/RGBA editor texture packages.
- **Native Game Class Stubs**: Generates compilable C++ modules for referenced native game classes, allowing game-subclassed Blueprints to open in-editor without crashing.
