using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace UE4Decompiler.Tests;

public class MappingTests
{
    private readonly ITestOutputHelper _output;

    public MappingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectMappingTypes()
    {
        var asm = typeof(CUE4Parse.FileProvider.AbstractFileProvider).Assembly;
        var t = asm.GetType("CUE4Parse.MappingsProvider.FileUsmapTypeMappingsProvider");
        Assert.NotNull(t);
        _output.WriteLine("Type: " + t.FullName);
        foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            _output.WriteLine("  " + m);
        }

        var tm = asm.GetType("CUE4Parse.MappingsProvider.TypeMappings");
        Assert.NotNull(tm);
        _output.WriteLine("\nTypeMappings: " + tm.FullName);
        foreach (var m in tm.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            _output.WriteLine("  " + m);
        }

        var providerType = asm.GetType("CUE4Parse.FileProvider.AbstractFileProvider");
        var prop = providerType?.GetProperty("MappingsContainer");
        _output.WriteLine("\nMappingsContainer type: " + prop?.PropertyType.FullName);
    }
}
