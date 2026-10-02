param(
    [string]$Version = "",
    [switch]$AllPlatforms,
    [switch]$NoBuild,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$RootDir = (Resolve-Path "$ScriptDir/..").Path

# 1. Determine target version
if ([string]::IsNullOrWhiteSpace($Version)) {
    $propsPath = Join-Path $RootDir "Directory.Build.props"
    if (Test-Path $propsPath) {
        $propsXml = [xml](Get-Content $propsPath -Raw)
        $Version = $propsXml.Project.PropertyGroup.Version
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = "2.0.0"
    }
}

if ($Version.StartsWith("v")) {
    $Version = $Version.Substring(1)
}

$Tag = "v$Version"
$DistDir = Join-Path $RootDir "dist"
$ReleasesDir = Join-Path $DistDir "releases"
$ReleaseNotesFile = Join-Path $DistDir "RELEASE_NOTES.md"
$ChangelogFile = Join-Path $RootDir "CHANGELOG.md"

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host " UE4Decompiler Automated Release Pipeline (PowerShell)" -ForegroundColor Cyan
Write-Host " Target Version: $Tag" -ForegroundColor Cyan
if ($DryRun) {
    Write-Host " Mode: DRY-RUN (simulating actions, no files or tags modified)" -ForegroundColor Yellow
}
Write-Host "==========================================================================" -ForegroundColor Cyan

if (-not (Test-Path $DistDir)) { New-Item -ItemType Directory -Path $DistDir | Out-Null }
if (-not (Test-Path $ReleasesDir)) { New-Item -ItemType Directory -Path $ReleasesDir | Out-Null }

# 2. Extract commit history and generate Markdown changelog
Write-Host "==> [1/5] Generating release changelog..." -ForegroundColor Green

$prevTag = ""
try {
    $prevTag = (git describe --tags --abbrev=0 2>$null)
} catch { }

$commitRange = if ($prevTag) { "$prevTag..HEAD" } else { "HEAD" }
$rangeDesc = if ($prevTag) { "Changes since $prevTag" } else { "Initial Release / Full History" }

$gitLogOutput = git log $commitRange --pretty=format:"%h%x09%s%x09%an"

$featList = [System.Collections.Generic.List[string]]::new()
$fixList = [System.Collections.Generic.List[string]]::new()
$perfList = [System.Collections.Generic.List[string]]::new()
$docList = [System.Collections.Generic.List[string]]::new()
$refactorList = [System.Collections.Generic.List[string]]::new()
$otherList = [System.Collections.Generic.List[string]]::new()

foreach ($line in $gitLogOutput) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split "`t"
    if ($parts.Length -lt 2) { continue }
    $hash = $parts[0]
    $subject = $parts[1]
    $author = if ($parts.Length -ge 3) { $parts[2] } else { "" }
    $entry = "- [\`$hash\`] $subject ($author)"

    if ($subject -match "^feat") {
        $featList.Add($entry)
    } elseif ($subject -match "^fix") {
        $fixList.Add($entry)
    } elseif ($subject -match "^perf") {
        $perfList.Add($entry)
    } elseif ($subject -match "^docs") {
        $docList.Add($entry)
    } elseif ($subject -match "^(refactor|style)") {
        $refactorList.Add($entry)
    } else {
        $otherList.Add($entry)
    }
}

$dateStr = (Get-Date).ToString("yyyy-MM-dd")
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("## [$Tag] - $dateStr")
[void]$sb.AppendLine()
[void]$sb.AppendLine("> $rangeDesc")
[void]$sb.AppendLine()

if ($featList.Count -gt 0) {
    [void]$sb.AppendLine("### 🚀 Features & Enhancements")
    foreach ($item in $featList) { [void]$sb.AppendLine($item) }
    [void]$sb.AppendLine()
}

if ($fixList.Count -gt 0) {
    [void]$sb.AppendLine("### 🐛 Bug Fixes & Stability")
    foreach ($item in $fixList) { [void]$sb.AppendLine($item) }
    [void]$sb.AppendLine()
}

if ($perfList.Count -gt 0) {
    [void]$sb.AppendLine("### ⚡ Performance & Codecs")
    foreach ($item in $perfList) { [void]$sb.AppendLine($item) }
    [void]$sb.AppendLine()
}

if ($refactorList.Count -gt 0) {
    [void]$sb.AppendLine("### 🛠️ Architecture & Refactoring")
    foreach ($item in $refactorList) { [void]$sb.AppendLine($item) }
    [void]$sb.AppendLine()
}

if ($docList.Count -gt 0) {
    [void]$sb.AppendLine("### 📚 Documentation")
    foreach ($item in $docList) { [void]$sb.AppendLine($item) }
    [void]$sb.AppendLine()
}

if ($otherList.Count -gt 0) {
    [void]$sb.AppendLine("### 📦 Other Changes")
    foreach ($item in $otherList) { [void]$sb.AppendLine($item) }
    [void]$sb.AppendLine()
}

$releaseNotesContent = $sb.ToString()
[System.IO.File]::WriteAllText($ReleaseNotesFile, $releaseNotesContent, [System.Text.Encoding]::UTF8)
Write-Host "==> Release notes generated at $ReleaseNotesFile" -ForegroundColor Green

if (-not $DryRun) {
    if (Test-Path $ChangelogFile) {
        $existing = [System.IO.File]::ReadAllText($ChangelogFile, [System.Text.Encoding]::UTF8)
        if (-not $existing.Contains("## [$Tag]")) {
            $updated = "$releaseNotesContent`n$existing"
            [System.IO.File]::WriteAllText($ChangelogFile, $updated, [System.Text.Encoding]::UTF8)
            Write-Host "==> Prepended release notes for $Tag to CHANGELOG.md" -ForegroundColor Green
        }
    } else {
        $initial = "# UE4Decompiler Changelog`n`n$releaseNotesContent"
        [System.IO.File]::WriteAllText($ChangelogFile, $initial, [System.Text.Encoding]::UTF8)
        Write-Host "==> Initialized CHANGELOG.md with $Tag" -ForegroundColor Green
    }
}

# 3. Update Directory.Build.props if version changed
$propsFile = Join-Path $RootDir "Directory.Build.props"
if (Test-Path $propsFile) {
    $propsContent = [System.IO.File]::ReadAllText($propsFile, [System.Text.Encoding]::UTF8)
    if ($propsContent -match "<Version>([^<]+)</Version>") {
        $curVer = $matches[1]
        if ($curVer -ne $Version) {
            if (-not $DryRun) {
                Write-Host "==> [2/5] Updating Directory.Build.props version to $Version..." -ForegroundColor Green
                $propsContent = $propsContent -replace "<Version>[^<]+</Version>", "<Version>$Version</Version>"
                $propsContent = $propsContent -replace "<AssemblyVersion>[^<]+</AssemblyVersion>", "<AssemblyVersion>$Version.0</AssemblyVersion>"
                $propsContent = $propsContent -replace "<FileVersion>[^<]+</FileVersion>", "<FileVersion>$Version.0</FileVersion>"
                [System.IO.File]::WriteAllText($propsFile, $propsContent, [System.Text.Encoding]::UTF8)
            } else {
                Write-Host "==> [2/5] [Dry-run] Would update Directory.Build.props version to $Version" -ForegroundColor Yellow
            }
        } else {
            Write-Host "==> [2/5] Directory.Build.props matches target version ($Version)" -ForegroundColor Green
        }
    }
}

# 4. Build and Package Binaries
if (-not $NoBuild -and -not $DryRun) {
    Write-Host "==> [3/5] Publishing and packaging binaries..." -ForegroundColor Green

    $rids = if ($AllPlatforms) { @("win-x64", "linux-x64", "osx-arm64", "osx-x64") } else { @("win-x64") }

    foreach ($rid in $rids) {
        Write-Host "--> Building for $rid..." -ForegroundColor Cyan
        & "$ScriptDir/publish.ps1" -RuntimeIdentifier $rid -Configuration "Release" -OutputDir "$DistDir/$rid"

        $cliArchive = Join-Path $ReleasesDir "UE4Decompiler-$Tag-$rid-cli.zip"
        $guiArchive = Join-Path $ReleasesDir "UE4Decompiler-$Tag-$rid-gui.zip"

        if (Test-Path $cliArchive) { Remove-Item $cliArchive -Force }
        if (Test-Path $guiArchive) { Remove-Item $guiArchive -Force }

        Compress-Archive -Path "$DistDir/$rid/cli/*" -DestinationPath $cliArchive -CompressionLevel Optimal
        Compress-Archive -Path "$DistDir/$rid/gui/*" -DestinationPath $guiArchive -CompressionLevel Optimal
    }

    # Generate SHA-256 Checksums
    Write-Host "--> Calculating SHA-256 checksums..." -ForegroundColor Cyan
    $checksumFile = Join-Path $ReleasesDir "checksums.sha256"
    $checksums = [System.Collections.Generic.List[string]]::new()
    Get-ChildItem -Path $ReleasesDir -Filter "*.zip" | ForEach-Object {
        $hash = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksums.Add("$hash  $($_.Name)")
    }
    [System.IO.File]::WriteAllLines($checksumFile, $checksums, [System.Text.Encoding]::UTF8)
    Write-Host "==> Checksums generated at $checksumFile" -ForegroundColor Green
} else {
    Write-Host "==> [3/5] Skipping build (NoBuild=$NoBuild, DryRun=$DryRun)" -ForegroundColor Yellow
}

# 5. Git Tagging
Write-Host "==> [4/5] Checking Git Tag: $Tag..." -ForegroundColor Green
$tagExists = $false
try {
    git rev-parse "$Tag" 2>$null | Out-Null
    $tagExists = ($LASTEXITCODE -eq 0)
} catch { }

if ($tagExists) {
    Write-Host "--> Tag $Tag already exists locally." -ForegroundColor Yellow
} else {
    if (-not $DryRun) {
        git tag -a "$Tag" -m "Release $Tag"
        Write-Host "--> Created git tag: $Tag" -ForegroundColor Green
    } else {
        Write-Host "--> [Dry-run] Would create git tag: $Tag" -ForegroundColor Yellow
    }
}

# 6. Create GitHub Release
Write-Host "==> [5/5] Creating GitHub Release..." -ForegroundColor Green
$ghAvailable = $false
try {
    $ghPath = Get-Command gh -ErrorAction SilentlyContinue
    if ($ghPath) {
        gh auth status 2>$null | Out-Null
        $ghAvailable = ($LASTEXITCODE -eq 0)
    }
} catch { }

if ($ghAvailable) {
    if (-not $DryRun) {
        Write-Host "--> Publishing release to GitHub via gh CLI..." -ForegroundColor Cyan
        $assets = Get-ChildItem -Path $ReleasesDir -File | ForEach-Object { $_.FullName }
        if ($assets.Count -gt 0) {
            gh release create "$Tag" $assets --title "UE4Decompiler $Tag" --notes-file "$ReleaseNotesFile"
        } else {
            gh release create "$Tag" --title "UE4Decompiler $Tag" --notes-file "$ReleaseNotesFile"
        }
        Write-Host "==> Release $Tag published successfully to GitHub!" -ForegroundColor Green
    } else {
        Write-Host "--> [Dry-run] Would run: gh release create $Tag <assets> --title 'UE4Decompiler $Tag' --notes-file $ReleaseNotesFile" -ForegroundColor Yellow
    }
} else {
    Write-Host ""
    Write-Host "==========================================================================" -ForegroundColor Cyan
    Write-Host " Release Assets and Tag prepared successfully!" -ForegroundColor Green
    Write-Host " Tag: $Tag" -ForegroundColor Green
    Write-Host " Release Notes: $ReleaseNotesFile" -ForegroundColor Green
    Write-Host " Release Artifacts:" -ForegroundColor Green
    Get-ChildItem -Path $ReleasesDir | Format-Table Name, Length, LastWriteTime
    Write-Host ""
    Write-Host " To publish to GitHub:" -ForegroundColor Yellow
    Write-Host "   1. Push your tag:  git push origin $Tag" -ForegroundColor Yellow
    Write-Host "   2. GitHub Actions will automatically publish the release and attach assets." -ForegroundColor Yellow
    Write-Host "      (Or manually upload artifacts from dist/releases/ to GitHub Releases)" -ForegroundColor Yellow
    Write-Host "==========================================================================" -ForegroundColor Cyan
}
