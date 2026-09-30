param(
    [string]$IdentityName = "Speedline.Widget",
    [string]$Publisher = "CN=Speedline",
    [string]$PublisherDisplayName = "Speedline",
    [string]$DisplayName = "Speedline",
    [string]$PrivacyUrl = "",
    [string]$Version = "1.2.0.0",
    [string]$OutDir = (Join-Path $PSScriptRoot "..\dist")
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "WifiSpeedWidget\WifiSpeedWidget.csproj"
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$layout = Join-Path $OutDir "layout"
$buildToolsVersion = "10.0.28000.2705"

if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { throw "Version must look like 1.2.3.0 (the Store needs the last part to be 0)." }

function Find-MakeAppx {
    $packages = ((dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
    $found = Get-ChildItem (Join-Path $packages "microsoft.windows.sdk.buildtools\*\bin\*\x64\makeappx.exe") -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) {
        $found = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
    }
    if ($found) { $found.FullName }
}

$makeAppx = Find-MakeAppx
if (-not $makeAppx) {
    Write-Host "Getting makeappx from the Microsoft.Windows.SDK.BuildTools NuGet package..."
    $tools = Join-Path $env:TEMP "speedline-buildtools"
    Remove-Item $tools -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $tools | Out-Null
    Set-Content (Join-Path $tools "tools.csproj") @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="$buildToolsVersion" /></ItemGroup>
</Project>
"@
    dotnet restore (Join-Path $tools "tools.csproj") | Out-Null
    $makeAppx = Find-MakeAppx
    if (-not $makeAppx) { throw "Could not find makeappx.exe." }
}

Remove-Item $layout -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $layout | Out-Null

$assemblyVersion = $Version -replace '\.0$', ''
dotnet publish $project -c Release -r win-x64 --self-contained true `
    "-p:AppDisplayName=$DisplayName" "-p:Version=$assemblyVersion" "-p:PrivacyPolicyUrl=$PrivacyUrl" `
    -p:DebugType=none -p:DebugSymbols=false -o $layout
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$assets = Join-Path $PSScriptRoot "Assets"
if (-not (Test-Path $assets)) { throw "Package assets are missing. Run Packaging\make-assets.ps1 first." }
$layoutAssets = Join-Path $layout "Assets"
New-Item -ItemType Directory -Force $layoutAssets | Out-Null
Copy-Item (Join-Path $assets "*") $layoutAssets -Recurse -Force

Add-Type -AssemblyName System.Security
function Escape([string]$value) { [System.Security.SecurityElement]::Escape($value) }

$manifest = Get-Content (Join-Path $PSScriptRoot "AppxManifest.template.xml") -Raw
$manifest = $manifest.Replace("{{IdentityName}}", (Escape $IdentityName)).
    Replace("{{Publisher}}", (Escape $Publisher)).
    Replace("{{PublisherDisplayName}}", (Escape $PublisherDisplayName)).
    Replace("{{DisplayName}}", (Escape $DisplayName)).
    Replace("{{Version}}", $Version)
[System.IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

$makePri = Join-Path (Split-Path $makeAppx) "makepri.exe"
if (-not (Test-Path $makePri)) { throw "Could not find makepri.exe next to makeappx.exe." }
$priWork = Join-Path $OutDir "pri-work"
Remove-Item $priWork -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $priWork | Out-Null
Copy-Item $assets (Join-Path $priWork "Assets") -Recurse -Force
Copy-Item (Join-Path $layout "AppxManifest.xml") $priWork
$priConfig = Join-Path $priWork "priconfig.xml"
& $makePri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed." }
[xml]$priXml = Get-Content $priConfig -Raw
$packagingNode = $priXml.resources.packaging
if ($packagingNode) { [void]$priXml.resources.RemoveChild($packagingNode) }
$priXml.Save($priConfig)
& $makePri new /pr $priWork /cf $priConfig /mn (Join-Path $priWork "AppxManifest.xml") /of (Join-Path $layout "resources.pri") /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri new failed." }
Remove-Item $priWork -Recurse -Force

$safeName = ($DisplayName -replace '[^A-Za-z0-9]', '')
$package = Join-Path $OutDir "${safeName}_${Version}_x64.msix"
Remove-Item $package -Force -ErrorAction SilentlyContinue
& $makeAppx pack /d $layout /p $package /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed." }

$size = [math]::Round((Get-Item $package).Length / 1MB, 1)
Write-Host ""
Write-Host "Built $package ($size MB)"
Write-Host "Identity: $IdentityName | Publisher: $Publisher | Version: $Version"
