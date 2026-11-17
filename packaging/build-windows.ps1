# Builds the portable Windows package: release\CES-<v>-win-x64.zip with ces-gui.exe (GUI, self-contained)
# and ces.exe (command line, single file). ffmpeg/ffprobe (and MediaInfo) are needed for Compare mode.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version
$pub = Join-Path $root "publish\win-x64"
Remove-Item $pub -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root "src\Ces") -c Release -r win-x64 --self-contained -o $pub | Out-Null
if ($LASTEXITCODE) { throw "GUI publish failed" }
dotnet publish (Join-Path $root "src\Ces.Cli") -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $pub "_cli") | Out-Null
if ($LASTEXITCODE) { throw "CLI publish failed" }
Copy-Item (Join-Path $pub "_cli\ces.exe") $pub
Remove-Item (Join-Path $pub "_cli") -Recurse -Force
Get-ChildItem $pub -Filter *.pdb | Remove-Item
Copy-Item (Join-Path $root "README.md") $pub
Copy-Item (Join-Path $root "LICENSE") (Join-Path $pub "LICENSE.txt")
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.txt") $pub
Copy-Item (Join-Path $root "external\BDInfo\LICENSE") (Join-Path $pub "LICENSE-BDInfo.txt")
$out = Join-Path $root "release"
New-Item -ItemType Directory -Force $out | Out-Null
$zip = Join-Path $out "CES-$version-win-x64.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $pub "*") -DestinationPath $zip -CompressionLevel Optimal
"{0}  {1:N1} MB" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB)
