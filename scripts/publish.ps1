param(
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDir = "$PSScriptRoot/../dist/$RuntimeIdentifier"
)

$ErrorActionPreference = "Stop"

Write-Host "==> Publishing UE4Decompiler.Cli for $RuntimeIdentifier ($Configuration)..."
dotnet publish "$PSScriptRoot/../src/UE4Decompiler.Cli/UE4Decompiler.Cli.csproj" `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$OutputDir/cli"

Write-Host "==> Publishing UE4Decompiler.Gui for $RuntimeIdentifier ($Configuration)..."
dotnet publish "$PSScriptRoot/../src/UE4Decompiler.Gui/UE4Decompiler.Gui.csproj" `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -o "$OutputDir/gui"

Write-Host "==> Published successfully to $OutputDir"
