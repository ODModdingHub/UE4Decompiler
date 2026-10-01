using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;

namespace UE4Decompiler.Core.Services;

/// <summary>
/// Discovers and tracks asset dependency relationships (Phase 52).
/// </summary>
public sealed class DependencyGraphService : IDependencyGraphService
{
    public DependencyGraph BuildGraph(IReadOnlyList<ParsedAsset> assets)
    {
        var graph = new DependencyGraph();

        // 1. Initialize nodes
        foreach (var asset in assets)
        {
            var node = new DependencyNode
            {
                PackagePath = asset.File.Path,
                ClassName = asset.PrimaryType
            };
            graph.Nodes[asset.File.Path] = node;
        }

        // 2. Wire connections from imports
        foreach (var asset in assets)
        {
            if (!graph.Nodes.TryGetValue(asset.File.Path, out var node)) continue;

            foreach (var import in asset.Imports)
            {
                // Soft / hard import paths e.g. "/Game/Characters/BP_Player"
                var cleanImport = import.Split('.')[0];
                node.References.Add(cleanImport);

                // If target node exists in graph, update reverse ref
                if (graph.Nodes.TryGetValue(cleanImport, out var targetNode))
                {
                    targetNode.ReferencedBy.Add(asset.File.Path);
                }
            }
        }

        return graph;
    }
}
