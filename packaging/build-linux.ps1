# Builds the Debian and Ubuntu packages: publishes the Linux GUI + CLI, then runs packaging/package-deb.sh inside
# each WSL distro (each package carries that distro's dependency names). Output: release\ces_<v>-1~<distro>_amd64.deb
#   pwsh packaging/build-linux.ps1            build both
#   pwsh packaging/build-linux.ps1 -Test      also install, run and purge each package in its distro (as root)
param([switch]$Test, [string[]]$Distros = @("Ubuntu", "Debian"))
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version
$pub = Join-Path $root "publish\linux-x64"
Remove-Item $pub -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root "src\Ces") -c Release -r linux-x64 --self-contained -o (Join-Path $pub "gui") | Out-Null
if ($LASTEXITCODE) { throw "GUI publish failed" }
dotnet publish (Join-Path $root "src\Ces.Cli") -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $pub "cli") | Out-Null
if ($LASTEXITCODE) { throw "CLI publish failed" }
$wslRoot = "/mnt/" + $root.Substring(0, 1).ToLower() + $root.Substring(2).Replace("\", "/")
foreach ($d in $Distros) {
    wsl.exe -d $d -- sh "$wslRoot/packaging/package-deb.sh" $version $wslRoot
    if ($LASTEXITCODE) { throw "packaging failed in $d" }
    if ($Test) {
        $deb = Get-ChildItem (Join-Path $root "release") -Filter "ces_$version-1~$($d.ToLower())*_amd64.deb" | Select-Object -First 1
        wsl.exe -d $d -u root -- sh "$wslRoot/packaging/test-deb.sh" "$wslRoot/release/$($deb.Name)"
    }
}
Get-ChildItem (Join-Path $root "release") -Filter "ces_$version-*.deb" | ForEach-Object { "{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
