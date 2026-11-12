# Builds a release: runs the tests, publishes a self-contained Dispatch.exe, then compiles the installer
# if Inno Setup 6 is installed.   Usage:  powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-Version 0.7.0]
param([string]$Version = "0.7.0")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

dotnet test "$root\Dispatch.slnx" -c Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed; not building a release." }

$publish = Join-Path $root "publish\win-x64"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish "$root\src\Dispatch.App\Dispatch.App.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
Write-Host "Published to $publish"

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup 6 isn't installed, so only the portable build was made. Install it (https://jrsoftware.org/isinfo.php or 'winget install JRSoftware.InnoSetup') and run this script again."
    exit 0
}

& $iscc "/DAppVersion=$Version" "$root\installer\Dispatch.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
Write-Host "Installer: $(Join-Path $root "artifacts\Dispatch-$Version-setup.exe")"
