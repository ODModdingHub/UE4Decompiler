using UE4Decompiler.Reconstructors;
using Xunit;

namespace UE4Decompiler.Tests;

public class KismetGraphSelfTestTests
{
    [Fact]
    public void KismetGraphSelfTest_PassesAllChecks()
    {
        var result = KismetGraphSelfTest.Run();
        Assert.True(result, "KismetGraphSelfTest must pass all pure bytecode, opcode mapping, and PinSerializer roundtrip assertions.");
    }
}
