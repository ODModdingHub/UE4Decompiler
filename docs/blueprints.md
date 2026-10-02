# Blueprint Decompilation Guide

In cooked Unreal Engine packages, visual Blueprint node graphs (`UEdGraph`, `UEdGraphNode`) are stripped by the Unreal cook process. Only the compiled virtual machine bytecode (`Script` bytecode on `UFunction` exports) and class properties remain.

UE4Decompiler provides three distinct levels of Blueprint recovery:

---

## 1. Decompiled Pseudo-Code & Source Representation

UE4Decompiler features a dedicated Kismet bytecode disassembler and decompiler (`BlueprintDecompiler`) that parses the execution tokens (calls, jumps, variable assignments, context switches) into clean, C++-like pseudo-code:

```cpp
// Asset Path: /Game/Characters/BP_Player.uasset
Blueprint BP_Player : ACharacter
{
    // Variables
    var Health : FloatProperty = 100.0;
    var WeaponSlot : ObjectProperty = default;

    // Functions
    function ReceiveBeginPlay()
    {
        CallFunction(PrintString, "Player Spawned");
        Super::ReceiveBeginPlay();
    }
}
```

---

## 2. Visual Graph Generation (DOT & Mermaid)

You can visualize the decompiled control-flow graphs directly:

### Graphviz DOT Format
```bash
ue4decompiler graph ./Game/Paks /Game/Characters/BP_Player --format dot -o BP_Player.dot
dot -Tpng BP_Player.dot -o BP_Player.png
```

### Mermaid Flowchart Format
```bash
ue4decompiler graph ./Game/Paks /Game/Characters/BP_Player --format mermaid
```

---

## 3. In-Editor Blueprint Synthesis & Stub Generation

When recovering an uncooked project:
- **Clean Blueprint Stubs**: By default, UE4Decompiler generates clean, loadable Blueprint assets that preserve parent classes, components, and default variable values so the project opens without crashes.
- **Native C++ Stub Modules**: Referenced native game classes are synthesized as compilable C++ classes (`.h` and `.cpp`), providing the required native inheritance chain in the Unreal Editor.
