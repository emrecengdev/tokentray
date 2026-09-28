# Builds release packages into .\dist:
#   TokenTray-<ver>-win-x64.zip / -win-arm64.zip   self-contained, no .NET needed (~70 MB zip)
#   TokenTray-<ver>-win-x64-small.zip               needs the .NET 10 Desktop Runtime (~6 MB)
# plus SHA256SUMS.txt.
param([string]$Version = "1.1.0")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet test tests/TokenTray.Tests -c Release
if ($LASTEXITCODE) { throw "tests failed" }

$dist = Join-Path $PSScriptRoot "dist"
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $dist | Out-Null

function Pack($rid, $selfContained, $suffix) {
    $out = Join-Path $dist "build-$rid$suffix"
    dotnet publish src/TokenTray -c Release -r $rid -o $out -p:Version=$Version `
        -p:SelfContained=$selfContained -p:PublishSingleFile=true | Out-Host
    if ($LASTEXITCODE) { throw "publish $rid failed" }
    $zip = Join-Path $dist "TokenTray-$Version-$rid$suffix.zip"
    Compress-Archive -Path (Join-Path $out "TokenTray.exe") -DestinationPath $zip
    Remove-Item $out -Recurse -Force
}

Pack "win-x64" $true ""
Pack "win-arm64" $true ""
Pack "win-x64" $false "-small"

Get-ChildItem $dist -Filter *.zip | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Set-Content (Join-Path $dist "SHA256SUMS.txt")
Get-ChildItem $dist | Format-Table Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }

