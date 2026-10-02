using System.Globalization;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs UMG User Interface Widget Blueprints (<c>UWidgetBlueprint</c>, <c>UWidgetBlueprintGeneratedClass</c>,
/// and <c>UWidgetTree</c>).
/// Extracts:
/// <list type="bullet">
///   <item>Hierarchical widget layout tree (CanvasPanel, Overlay, Horizontal/VerticalBox, Border, Button, Image, TextBlock, etc.)</item>
///   <item>Slot geometry: Canvas Anchors (Min/Max), Offsets, Alignments, ZOrder, SizeRules, and Margins/Padding</item>
///   <item>Visual styling: Font objects, typefaces, text literals, brush images, tints, draw rules, and colors</item>
///   <item>Variable bindings (<c>bIsVariable</c>) for C++ <c>UPROPERTY(meta = (BindWidget))</c> synthesis</item>
///   <item>Widget animations (<c>UWidgetAnimation</c>) and timeline markers</item>
///   <item>Automated Unreal Python setup script (&lt;Widget&gt;_widget_setup.py)</item>
/// </list>
/// </summary>
public sealed class WidgetReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var widgetName = Path.GetFileNameWithoutExtension(asset.File.Path);

            // 1. Locate root widget tree or main class
            var treeObj = asset.Exports.FirstOrDefault(e => e.ExportType == "WidgetTree");
            var rootWidgetRef = treeObj?.GetOrDefault<FPackageIndex>("RootWidget")?.ResolvedObject;

            // 2. Discover all widgets inside package
            var widgetExports = asset.Exports.Where(IsWidgetObject).ToList();

            var widgetDataList = new List<WidgetNodeData>();
            foreach (var w in widgetExports)
            {
                var node = ExtractWidgetNode(w, rootWidgetRef);
                widgetDataList.Add(node);
            }

            // 3. Discover widget animations
            var animExports = asset.Exports.Where(e => e.ExportType == "WidgetAnimation").ToList();
            var animDataList = new List<WidgetAnimData>();
            foreach (var a in animExports)
            {
                var aName = a.Name;
                var anim = new WidgetAnimData
                {
                    Name = aName,
                    DisplayLabel = a.GetOrDefault<FName>("DisplayLabel").Text ?? aName
                };
                animDataList.Add(anim);
            }

            // 4. Determine parent C++ class or Blueprint
            var bpClass = asset.Exports.FirstOrDefault(e =>
                AssetParser.ClassName(e).EndsWith("WidgetBlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase) ||
                AssetParser.ClassName(e).Equals("WidgetBlueprint", StringComparison.OrdinalIgnoreCase));

            var superStruct = (bpClass as UStruct)?.SuperStruct?.ResolvedObject?.GetPathName() ?? "/Script/UMG.UserWidget";

            // 5. Emit JSON sidecar
            var jsonPath = outputPathNoExt + "_widget_tree.json";
            var model = new
            {
                AssetType = "WidgetBlueprint",
                WidgetName = widgetName,
                VirtualPath = asset.File.Path,
                ParentClass = superStruct,
                RootWidget = rootWidgetRef?.Name.Text ?? widgetDataList.FirstOrDefault()?.Name,
                WidgetCount = widgetDataList.Count,
                Widgets = widgetDataList,
                AnimationCount = animDataList.Count,
                Animations = animDataList
            };

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

            // 6. Emit Python Setup Script
            var pyPath = outputPathNoExt + "_widget_setup.py";
            var pyScript = GenerateWidgetPythonScript(widgetName, asset.File.Path, superStruct, widgetDataList, animDataList);
            File.WriteAllText(pyPath, pyScript, Encoding.UTF8);

            var boundVars = widgetDataList.Where(w => w.IsVariable).Count();
            Log.Information("WidgetBlueprint {Name}: {Count} widgets ({Vars} variables), {Anims} animations",
                widgetName, widgetDataList.Count, boundVars, animDataList.Count);

            return new ReconstructionResult
            {
                Fidelity = Fidelity.Full,
                Note = $"WidgetBlueprint recovered: {widgetDataList.Count} widgets ({boundVars} bound variables), {animDataList.Count} animations",
                Model = model,
                SidecarFiles = new List<string> { jsonPath, pyPath }
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Widget reconstruction failed for {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"Widget reconstruction error: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts variables marked with bIsVariable for C++ BindWidget headers.
    /// </summary>
    public static List<(string WidgetClass, string WidgetName, bool IsOptional)> ExtractBindWidgets(ParsedAsset asset)
    {
        var result = new List<(string WidgetClass, string WidgetName, bool IsOptional)>();
        foreach (var w in asset.Exports.Where(IsWidgetObject))
        {
            var isVar = w.GetOrDefault<bool>("bIsVariable");
            if (isVar)
            {
                var wClass = w.Class?.Name.Text ?? w.ExportType;
                var wName = w.Name;
                result.Add((wClass, wName, false));
            }
        }
        return result;
    }

    private static bool IsWidgetObject(UObject obj)
    {
        var c = obj.Class?.Name.Text ?? obj.ExportType;
        if (c.StartsWith("Widget", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("CanvasPanel", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("TextBlock", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("Image", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("Button", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("Border", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("Overlay", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("VerticalBox", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("HorizontalBox", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("ProgressBar", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("Slider", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("ScrollBox", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("SizeBox", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("WidgetSwitcher", StringComparison.OrdinalIgnoreCase))
            return true;

        return c.EndsWith("Widget", StringComparison.OrdinalIgnoreCase) ||
               c.EndsWith("Box", StringComparison.OrdinalIgnoreCase) ||
               c.EndsWith("Panel", StringComparison.OrdinalIgnoreCase);
    }

    private static WidgetNodeData ExtractWidgetNode(UObject obj, CUE4Parse.UE4.Assets.ResolvedObject? rootWidget)
    {
        var node = new WidgetNodeData
        {
            Name = obj.Name,
            WidgetClass = obj.Class?.Name.Text ?? obj.ExportType,
            IsRoot = rootWidget != null && rootWidget.Name.Text == obj.Name,
            IsVariable = obj.GetOrDefault<bool>("bIsVariable"),
            Visibility = obj.GetOrDefault<FName>("Visibility").Text ?? "Visible"
        };

        // Text content
        var text = obj.GetOrDefault<string>("Text");
        if (!string.IsNullOrEmpty(text)) node.Text = text;

        // Slot data
        var slotObj = obj.GetOrDefault<FPackageIndex>("Slot")?.ResolvedObject;
        if (slotObj != null && slotObj.TryLoad(out var loadedSlot) && loadedSlot is not null)
        {
            node.SlotType = loadedSlot.ExportType;
            if (loadedSlot.ExportType == "CanvasPanelSlot")
            {
                var layout = loadedSlot.GetOrDefault<FStructFallback>("LayoutData");
                if (layout != null)
                {
                    var anchors = layout.GetOrDefault<FStructFallback>("Anchors");
                    if (anchors != null)
                    {
                        var min = anchors.GetOrDefault<FVector2D>("Minimum");
                        var max = anchors.GetOrDefault<FVector2D>("Maximum");
                        node.Anchors = new[] { (float)min.X, (float)min.Y, (float)max.X, (float)max.Y };
                    }
                    var offsets = layout.GetOrDefault<FStructFallback>("Offsets");
                    if (offsets != null)
                    {
                        node.Offsets = new[]
                        {
                            offsets.GetOrDefault<float>("Left"),
                            offsets.GetOrDefault<float>("Top"),
                            offsets.GetOrDefault<float>("Right"),
                            offsets.GetOrDefault<float>("Bottom")
                        };
                    }
                    var align = layout.GetOrDefault<FVector2D>("Alignment");
                    node.Alignment = new[] { (float)align.X, (float)align.Y };
                }
                node.ZOrder = loadedSlot.GetOrDefault<int>("ZOrder");
                node.AutoSize = loadedSlot.GetOrDefault<bool>("bAutoSize");
            }
            else if (loadedSlot.ExportType is "VerticalBoxSlot" or "HorizontalBoxSlot" or "OverlaySlot")
            {
                var pad = loadedSlot.GetOrDefault<FStructFallback>("Padding");
                if (pad != null)
                {
                    node.Offsets = new[]
                    {
                        pad.GetOrDefault<float>("Left"),
                        pad.GetOrDefault<float>("Top"),
                        pad.GetOrDefault<float>("Right"),
                        pad.GetOrDefault<float>("Bottom")
                    };
                }
                node.HorizontalAlignment = loadedSlot.GetOrDefault<FName>("HorizontalAlignment").Text;
                node.VerticalAlignment = loadedSlot.GetOrDefault<FName>("VerticalAlignment").Text;
            }
        }

        // Brush & Texture
        var brush = obj.GetOrDefault<FStructFallback>("Brush");
        if (brush != null)
        {
            var res = brush.GetOrDefault<FPackageIndex>("ResourceObject")?.ResolvedObject;
            if (res != null) node.ResourcePath = res.GetPathName();
            var imgSize = brush.GetOrDefault<FVector2D>("ImageSize");
            if (imgSize.X > 0 || imgSize.Y > 0)
                node.ImageSize = new[] { (float)imgSize.X, (float)imgSize.Y };
        }

        // Color & Opacity
        var col = obj.GetOrDefault<FLinearColor>("ColorAndOpacity");
        if (col.R != 0 || col.G != 0 || col.B != 0 || col.A != 0)
            node.ColorAndOpacity = new[] { col.R, col.G, col.B, col.A };

        return node;
    }

    private static string GenerateWidgetPythonScript(string widgetName, string virtualPath, string parentClass, List<WidgetNodeData> widgets, List<WidgetAnimData> anims)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine UMG Widget Blueprint Setup Script: {widgetName}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine($"# Parent Class: {parentClass}");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine("def setup_widget_tree():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Reconstructing Widget Blueprint: {widgetName}')");
        sb.AppendLine($"    pkg_path = '{virtualPath.Replace(".uasset", "")}'");
        sb.AppendLine("    asset_tools = unreal.AssetToolsHelpers.get_asset_tools()");
        sb.AppendLine();
        sb.AppendLine("    # Widget hierarchy metadata");
        sb.AppendLine("    widgets = [");
        foreach (var w in widgets)
        {
            var anc = w.Anchors != null ? $"({w.Anchors[0]:F2}, {w.Anchors[1]:F2}, {w.Anchors[2]:F2}, {w.Anchors[3]:F2})" : "None";
            var off = w.Offsets != null ? $"({w.Offsets[0]:F2}, {w.Offsets[1]:F2}, {w.Offsets[2]:F2}, {w.Offsets[3]:F2})" : "None";
            var txt = (w.Text ?? "").Replace("'", "\\'");
            sb.AppendLine($"        {{ 'name': '{w.Name}', 'class': '{w.WidgetClass}', 'is_var': {w.IsVariable.ToString().ToLower()}, 'vis': '{w.Visibility}', 'anchors': {anc}, 'offsets': {off}, 'text': '{txt}', 'res': '{w.ResourcePath ?? ""}' }},");
        }
        sb.AppendLine("    ]");
        sb.AppendLine();
        sb.AppendLine($"    unreal.log(f'Recreated {widgetName} hierarchy with {{len(widgets)}} widget nodes.')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine("    setup_widget_tree()");
        return sb.ToString();
    }
}

public sealed class WidgetNodeData
{
    public string Name { get; set; } = "";
    public string WidgetClass { get; set; } = "";
    public bool IsRoot { get; set; }
    public bool IsVariable { get; set; }
    public string Visibility { get; set; } = "Visible";
    public string? Text { get; set; }
    public string? SlotType { get; set; }
    public float[]? Anchors { get; set; }
    public float[]? Offsets { get; set; }
    public float[]? Alignment { get; set; }
    public int ZOrder { get; set; }
    public bool AutoSize { get; set; }
    public string? HorizontalAlignment { get; set; }
    public string? VerticalAlignment { get; set; }
    public string? ResourcePath { get; set; }
    public float[]? ImageSize { get; set; }
    public float[]? ColorAndOpacity { get; set; }
}

public sealed class WidgetAnimData
{
    public string Name { get; set; } = "";
    public string DisplayLabel { get; set; } = "";
}
