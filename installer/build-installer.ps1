param(
    [switch]$CopyToDesktop = $true
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."

# The .wxs is the single source of truth for the version: an MSI only upgrades an older install
# when its own package version is higher, so the file name must never disagree with what is
# inside the package.
$mainWxs = "$root\installer\WhisperPlus.wxs"
$version = ([xml](Get-Content -Raw $mainWxs)).Wix.Package.Version
if (-not $version) { throw "Could not read the package version from $mainWxs" }

Write-Host "==> Publishing WhisperPlus $version (Release, win-x64, self-contained)..." -ForegroundColor Cyan
dotnet publish "$root\src\WhisperPlus\WhisperPlus.csproj" -c Release -r win-x64 --self-contained true

$publishDir = "$root\src\WhisperPlus\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
$harvestScript = "$root\installer\harvest.py"
$publishWxs = "$root\installer\PublishFiles.wxs"
$outMsi = "$root\installer\WhisperPlus-$version.msi"

Write-Host "==> Harvesting publish files with harvest.py..." -ForegroundColor Cyan
python $harvestScript $publishDir $publishWxs

Write-Host "==> Building MSI package with WiX Toolset..." -ForegroundColor Cyan
dotnet wix build -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext $mainWxs $publishWxs -o $outMsi

Write-Host "==> Built: $outMsi" -ForegroundColor Green

if ($CopyToDesktop) {
    $desktopPath = [Environment]::GetFolderPath("Desktop")
    $desktopMsi = Join-Path $desktopPath "WhisperPlus-$version.msi"
    Copy-Item $outMsi -Destination $desktopMsi -Force
    Write-Host "==> Copied installer to Desktop: $desktopMsi" -ForegroundColor Green
}
