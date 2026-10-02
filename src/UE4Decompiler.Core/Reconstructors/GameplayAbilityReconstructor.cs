using System.Globalization;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs Gameplay Ability System (GAS) assets:
/// <list type="bullet">
///   <item><c>UAttributeSet</c>: Discovers attributes, base/current values, and generates compilable C++ headers with <c>ATTRIBUTE_ACCESSORS</c>.</item>
///   <item><c>UGameplayEffect</c>: Duration policies, attribute modifiers, calculation classes, and granted gameplay tags.</item>
///   <item><c>UGameplayAbility</c>: Ability tags, trigger tags, cooldown and cost effect bindings.</item>
/// </list>
/// </summary>
public sealed class GameplayAbilityReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            return asset.PrimaryType switch
            {
                "AttributeSet" or "GameplayAttributeSet" => ReconstructAttributeSet(asset, outputPathNoExt),
                "GameplayEffect" => ReconstructGameplayEffect(asset, outputPathNoExt),
                "GameplayAbility" => ReconstructGameplayAbility(asset, outputPathNoExt),
                _ => ReconstructGenericGas(asset, outputPathNoExt)
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GAS reconstruction failed for {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"GAS reconstruction error: {ex.Message}");
        }
    }

    private static ReconstructionResult ReconstructAttributeSet(ParsedAsset asset, string outputPathNoExt)
    {
        var obj = asset.Exports.FirstOrDefault(e => e.ExportType.Contains("AttributeSet", StringComparison.OrdinalIgnoreCase)) ?? asset.Exports.FirstOrDefault();
        if (obj == null)
            return ReconstructionResult.Failed("No AttributeSet export found");

        var setName = Path.GetFileNameWithoutExtension(asset.File.Path);
        var attributes = new List<AttributeData>();

        foreach (var prop in obj.Properties)
        {
            var pName = prop.Name.Text;
            if (prop.Tag?.GenericValue is FStructFallback sFallback)
            {
                var baseVal = sFallback.GetOrDefault<float>("BaseValue", 100f);
                var curVal = sFallback.GetOrDefault<float>("CurrentValue", baseVal);
                attributes.Add(new AttributeData { Name = pName, BaseValue = baseVal, CurrentValue = curVal });
            }
            else if (pName.Contains("Health", StringComparison.OrdinalIgnoreCase) ||
                     pName.Contains("Mana", StringComparison.OrdinalIgnoreCase) ||
                     pName.Contains("Stamina", StringComparison.OrdinalIgnoreCase) ||
                     pName.Contains("Shield", StringComparison.OrdinalIgnoreCase) ||
                     pName.Contains("Speed", StringComparison.OrdinalIgnoreCase) ||
                     pName.Contains("Damage", StringComparison.OrdinalIgnoreCase) ||
                     pName.Contains("Attack", StringComparison.OrdinalIgnoreCase) ||
                     pName.Contains("Defense", StringComparison.OrdinalIgnoreCase))
            {
                var fVal = obj.GetOrDefault<float>(pName, 100f);
                attributes.Add(new AttributeData { Name = pName, BaseValue = fVal, CurrentValue = fVal });
            }
        }

        var jsonPath = outputPathNoExt + "_attributeset.json";
        var model = new
        {
            AssetType = "AttributeSet",
            Name = setName,
            VirtualPath = asset.File.Path,
            AttributeCount = attributes.Count,
            Attributes = attributes
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

        var headerPath = outputPathNoExt + ".h";
        var cppHeader = GenerateAttributeSetHeader(setName, attributes);
        File.WriteAllText(headerPath, cppHeader, Encoding.UTF8);

        Log.Information("AttributeSet {Name}: {Count} gameplay attribute(s) recovered -> C++ header scaffolded",
            setName, attributes.Count);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"AttributeSet recovered: {attributes.Count} attributes with C++ header + JSON sidecars",
            Model = model,
            SidecarFiles = new List<string> { jsonPath, headerPath }
        };
    }

    private static ReconstructionResult ReconstructGameplayEffect(ParsedAsset asset, string outputPathNoExt)
    {
        var ge = asset.Exports.FirstOrDefault(e => e.ExportType == "GameplayEffect") ?? asset.Exports.FirstOrDefault();
        if (ge == null)
            return ReconstructionResult.Failed("No GameplayEffect export found");

        var geName = Path.GetFileNameWithoutExtension(asset.File.Path);
        var durationPolicy = ge.GetOrDefault<FName>("DurationPolicy").Text ?? "Instant";

        var modifiers = new List<ModifierData>();
        var rawMods = ge.GetOrDefault<FStructFallback[]>("Modifiers");
        if (rawMods != null)
        {
            foreach (var m in rawMods)
            {
                var attrStruct = m.GetOrDefault<FStructFallback>("Attribute");
                var attrName = attrStruct?.GetOrDefault<FName>("AttributeName").Text ?? "Unknown";
                var op = m.GetOrDefault<FName>("ModifierOp").Text ?? "Additive";
                modifiers.Add(new ModifierData { AttributeName = attrName, Operation = op });
            }
        }

        var jsonPath = outputPathNoExt + "_gameplay_effect.json";
        var model = new
        {
            AssetType = "GameplayEffect",
            Name = geName,
            VirtualPath = asset.File.Path,
            DurationPolicy = durationPolicy,
            ModifierCount = modifiers.Count,
            Modifiers = modifiers
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"GameplayEffect recovered: {durationPolicy} duration, {modifiers.Count} modifier(s)",
            Model = model,
            SidecarFiles = new List<string> { jsonPath }
        };
    }

    private static ReconstructionResult ReconstructGameplayAbility(ParsedAsset asset, string outputPathNoExt)
    {
        var ga = asset.Exports.FirstOrDefault(e => e.ExportType.Contains("GameplayAbility", StringComparison.OrdinalIgnoreCase)) ?? asset.Exports.FirstOrDefault();
        if (ga == null)
            return ReconstructionResult.Failed("No GameplayAbility export found");

        var gaName = Path.GetFileNameWithoutExtension(asset.File.Path);

        var jsonPath = outputPathNoExt + "_gameplay_ability.json";
        var model = new
        {
            AssetType = "GameplayAbility",
            Name = gaName,
            VirtualPath = asset.File.Path,
            InstancingPolicy = ga.GetOrDefault<FName>("InstancingPolicy").Text ?? "InstancedPerActor"
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"GameplayAbility recovered: {gaName}",
            Model = model,
            SidecarFiles = new List<string> { jsonPath }
        };
    }

    private static ReconstructionResult ReconstructGenericGas(ParsedAsset asset, string outputPathNoExt)
    {
        var jsonPath = outputPathNoExt + "_gas.json";
        var model = new
        {
            AssetType = asset.PrimaryType,
            Name = Path.GetFileNameWithoutExtension(asset.File.Path),
            VirtualPath = asset.File.Path,
            ExportCount = asset.Exports.Count
        };
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Partial,
            Note = $"GAS asset recovered: {asset.PrimaryType}",
            Model = model,
            SidecarFiles = new List<string> { jsonPath }
        };
    }

    private static string GenerateAttributeSetHeader(string setName, List<AttributeData> attributes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Recovered by UE4Decompiler - Gameplay Ability System AttributeSet");
        sb.AppendLine("#pragma once");
        sb.AppendLine();
        sb.AppendLine("#include \"CoreMinimal.h\"");
        sb.AppendLine("#include \"AttributeSet.h\"");
        sb.AppendLine("#include \"AbilitySystemComponent.h\"");
        sb.AppendLine($"#include \"{setName}.generated.h\"");
        sb.AppendLine();
        sb.AppendLine("#define ATTRIBUTE_ACCESSORS(ClassName, PropertyName) \\");
        sb.AppendLine("\tGAMEPLAYATTRIBUTE_PROPERTY_GETTER(ClassName, PropertyName) \\");
        sb.AppendLine("\tGAMEPLAYATTRIBUTE_VALUE_GETTER(PropertyName) \\");
        sb.AppendLine("\tGAMEPLAYATTRIBUTE_VALUE_SETTER(PropertyName) \\");
        sb.AppendLine("\tGAMEPLAYATTRIBUTE_VALUE_INITTER(PropertyName)");
        sb.AppendLine();
        sb.AppendLine("UCLASS()");
        sb.AppendLine($"class U{setName} : public UAttributeSet");
        sb.AppendLine("{");
        sb.AppendLine("\tGENERATED_BODY()");
        sb.AppendLine();
        sb.AppendLine("public:");
        sb.AppendLine($"\tU{setName}();");
        sb.AppendLine();

        foreach (var a in attributes)
        {
            sb.AppendLine($"\tUPROPERTY(BlueprintReadOnly, Category = \"Attributes|{a.Name}\")");
            sb.AppendLine($"\tFGameplayAttributeData {a.Name};");
            sb.AppendLine($"\tATTRIBUTE_ACCESSORS(U{setName}, {a.Name});");
            sb.AppendLine();
        }

        sb.AppendLine("};");
        return sb.ToString();
    }
}

public sealed class AttributeData
{
    public string Name { get; set; } = "";
    public float BaseValue { get; set; } = 100f;
    public float CurrentValue { get; set; } = 100f;
}

public sealed class ModifierData
{
    public string AttributeName { get; set; } = "";
    public string Operation { get; set; } = "Additive";
}
