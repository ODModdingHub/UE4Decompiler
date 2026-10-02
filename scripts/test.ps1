$ErrorActionPreference = "Stop"

Write-Host "==> Running UE4Decompiler Automated Test Suite..." -ForegroundColor Cyan
dotnet test tests/UE4Decompiler.Tests/UE4Decompiler.Tests.csproj --verbosity normal

Write-Host "==> All tests executed successfully!" -ForegroundColor Green
