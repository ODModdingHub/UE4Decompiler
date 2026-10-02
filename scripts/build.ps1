$ErrorActionPreference = "Stop"

Write-Host "==> Building UE4Decompiler Solution (Release)..." -ForegroundColor Cyan
dotnet build UE4Decompiler.sln -c Release

Write-Host "==> Build completed successfully!" -ForegroundColor Green
