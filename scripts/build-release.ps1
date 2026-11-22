param(
    [string]$Version = "",
    [string]$Tag = "",
    [string[]]$Archs = @("win-x64", "win-arm64"),
    [string]$OutputDir = "dist\release",
    [switch]$SkipInstaller,
    [switch]$RequireInstaller
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$CsprojPath = Join-Path $RepoRoot "SideScreenHUD.csproj"
$IssPath = Join-Path $RepoRoot "installer\SideScreenHUD.iss"

# Read <Version> from csproj if not explicitly provided
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projXml = Get-Content -Path $CsprojPath -Encoding UTF8
    $Version = ($projXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "1.0.0" }
}
$Version = $Version.Trim().TrimStart("v", "V")

# AssemblyVersion / FileVersion must be numeric (e.g. 1.0.0)
$NumericVersion = ($Version -split "-")[0]
if ($NumericVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    $NumericVersion = "1.0.0"
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = "v$Version"
}

$OutFull = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $RepoRoot $OutputDir }
New-Item -ItemType Directory -Force -Path $OutFull | Out-Null

# Locate Inno Setup compiler (ISCC.exe) if installer generation is enabled
$IsccPath = $null
if (-not $SkipInstaller) {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) {
        $IsccPath = $cmd.Source
    } else {
        $candidates = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
        )
        foreach ($c in $candidates) {
            if (Test-Path $c) { $IsccPath = $c; break }
        }
    }

    if ($RequireInstaller -and -not $IsccPath) {
        throw "Inno Setup (ISCC.exe) not found, but -RequireInstaller was specified."
    }
}

Write-Host "=== SideScreen HUD Release Packaging ==="
Write-Host "Version : $Version (Numeric: $NumericVersion)"
Write-Host "Tag     : $Tag"
Write-Host "Archs   : $($Archs -join ', ')"
Write-Host "Output  : $OutFull"
Write-Host "ISCC    : $(if ($IsccPath) { $IsccPath } else { 'Not installed (installer skipped)' })"

foreach ($rid in $Archs) {
    $archShort = $rid -replace '^win-', ''
    $pubDir = Join-Path $RepoRoot "dist\publish\$rid"
    if (Test-Path $pubDir) {
        Remove-Item -Recurse -Force $pubDir
    }

    Write-Host "`n--- Publishing $rid (self-contained single-file) ---"
    & dotnet publish $CsprojPath `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:Version=$NumericVersion `
        -p:InformationalVersion=$Tag `
        -o $pubDir `
        --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid with exit code $LASTEXITCODE"
    }

    # Verify essential files exist
    $exePath = Join-Path $pubDir "SideScreenHUD.exe"
    $webIndex = Join-Path $pubDir "web\index.html"
    if (-not (Test-Path $exePath)) { throw "Missing published executable: $exePath" }
    if (-not (Test-Path $webIndex)) { throw "Missing web assets: $webIndex" }

    # Remove any accidental stray debug/doc/report files in publish root
    Get-ChildItem -Path $pubDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem -Path $pubDir -Filter "*.xml" -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item (Join-Path $pubDir "self-test-report.json") -ErrorAction SilentlyContinue

    # Include README.md in published directory for both portable ZIP & installer
    Copy-Item (Join-Path $RepoRoot "README.md") -Destination (Join-Path $pubDir "README.md") -Force

    # 1. Create Portable ZIP package
    $zipName = "SideScreenHUD-$Tag-$rid.zip"
    $zipPath = Join-Path $OutFull $zipName
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }

    Write-Host "Creating portable ZIP: $zipName"
    Compress-Archive -Path (Join-Path $pubDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

    # 2. Create Windows Setup Installer via Inno Setup (when ISCC is available)
    if ($IsccPath -and -not $SkipInstaller) {
        $setupBase = "SideScreenHUD-$Tag-$rid-Setup"
        Write-Host "Building Inno Setup installer: $setupBase.exe"
        & $IsccPath `
            "/DMyAppVersion=$NumericVersion" `
            "/DMyArch=$archShort" `
            "/DMyPublishDir=$pubDir" `
            "/DMyOutputDir=$OutFull" `
            "/DMyOutputBaseFilename=$setupBase" `
            $IssPath

        if ($LASTEXITCODE -ne 0) {
            throw "ISCC failed for $rid with exit code $LASTEXITCODE"
        }
    }
}

# Generate SHA256SUMS.txt
$artifacts = Get-ChildItem -Path $OutFull -File | Where-Object { $_.Name -ne "SHA256SUMS.txt" } | Sort-Object Name
$checksumLines = foreach ($f in $artifacts) {
    $hash = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($f.Name)"
}
$checksumPath = Join-Path $OutFull "SHA256SUMS.txt"
Set-Content -Path $checksumPath -Value $checksumLines -Encoding UTF8

Write-Host "`n=== Release Artifacts Ready ==="
Get-ChildItem -Path $OutFull | Select-Object Name, @{Name="SizeMB";Expression={[math]::Round($_.Length / 1MB, 2)}} | Format-Table -AutoSize
