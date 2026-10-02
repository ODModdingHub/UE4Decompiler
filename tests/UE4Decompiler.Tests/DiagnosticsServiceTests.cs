using UE4Decompiler.Core.Models;
using UE4Decompiler.Core.Services;
using Xunit;

namespace UE4Decompiler.Tests;

public class DiagnosticsServiceTests
{
    [Fact]
    public async Task RunDiagnosticsAsync_BasicEnvironment_ReportsPass()
    {
        var diag = new DiagnosticsService();
        var report = await diag.RunDiagnosticsAsync(null, null);

        Assert.NotNull(report);
        Assert.NotEmpty(report.Items);

        var runtimeCheck = report.Items.FirstOrDefault(i => i.CheckName == ".NET Environment");
        Assert.NotNull(runtimeCheck);
        Assert.Equal(DiagnosticLevel.Pass, runtimeCheck.Level);

        var skiaCheck = report.Items.FirstOrDefault(i => i.CheckName == "SkiaSharp Native Graphics");
        Assert.NotNull(skiaCheck);
        Assert.Equal(DiagnosticLevel.Pass, skiaCheck.Level);
    }
}
