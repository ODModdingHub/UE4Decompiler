# Property Mappings (.usmap) Guide

Cooked Unreal Engine games (especially UE 4.26+ and UE 5.x) typically strip property names and type information during the cooking process. Instead of storing tagged property identifiers, objects only contain raw serialized byte values indexed by property index numbers.

---

## What is a `.usmap` File?

A `.usmap` (Unreal Schema Mapping) file contains a dictionary of engine and game classes, listing the exact property names, types, inner structures, and serialized order.

When a `.usmap` mapping file is loaded:
- CUE4Parse matches property indices against the schema.
- Object variables (health, inventory, actor references, material parameters) are fully restored with their human-readable property names and types.
- JSON IR models and Blueprint pseudo-code display complete property definitions rather than unknown binary offsets.

---

## Supplying a Mapping File

### In the CLI
Pass the `--mapping` flag pointing to your `.usmap` file:

```bash
ue4decompiler recover ./Game/Content/Paks \
  --output ./Recovered \
  --mapping ./Mappings.usmap
```

### In the GUI
Enter the path to your `.usmap` file in the "Unversioned Property Mapping" field on the Home screen.

---

## Obtaining a `.usmap` File

- **Dumper Tools**: For released games, community tools such as `Dumper-7`, `UE4SS`, or `UnrealDumper` can dump the live game reflection schema directly from memory and export a matching `.usmap` file.
- **Auto-Discovery**: If a `.usmap` file is present in the input directory or alongside the game executable, UE4Decompiler can automatically identify and load it.
