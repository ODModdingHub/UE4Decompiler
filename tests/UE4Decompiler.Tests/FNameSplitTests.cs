using UE4Decompiler.Output.Writer;
using Xunit;

namespace UE4Decompiler.Tests;

public class FNameSplitTests
{
    [Theory]
    [InlineData("Wall_10", "Wall", 11)]
    [InlineData("Wall_0", "Wall", 1)]
    [InlineData("MI_Material_99", "MI_Material", 100)]
    [InlineData("Mesh_1", "Mesh", 2)]
    public void Split_ValidTrailingNumbers_SplitsCorrectly(string input, string expectedBase, int expectedNumber)
    {
        var (baseName, number) = FNameSplit.Split(input);
        Assert.Equal(expectedBase, baseName);
        Assert.Equal(expectedNumber, number);
    }

    [Theory]
    [InlineData("Wall_00", "Wall_00", 0)] // leading zero not allowed unless single 0
    [InlineData("Wall_01", "Wall_01", 0)]
    [InlineData("Wall_007", "Wall_007", 0)]
    [InlineData("Wall", "Wall", 0)]
    [InlineData("_10", "_10", 0)]         // base cannot be empty
    [InlineData("", "", 0)]
    [InlineData(null, null, 0)]
    [InlineData("Wall_abc", "Wall_abc", 0)]
    public void Split_InvalidOrNonNumberSuffix_PreservesName(string? input, string? expectedBase, int expectedNumber)
    {
        var (baseName, number) = FNameSplit.Split(input!);
        Assert.Equal(expectedBase, baseName);
        Assert.Equal(expectedNumber, number);
    }
}
