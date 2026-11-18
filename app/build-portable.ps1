# Builds the portable ZIP of the desktop application:
#
#   XrplFile\
#     XRPL File.exe   the launcher (app\XrplFile.Launcher), the one thing a person runs
#     README.txt
#     app\            the application with the .NET runtime and the Windows App SDK
#
# The ZIP and its .sha256 land in -OutputDirectory, artifacts\portable by default. The release
# workflow (.github\workflows\desktop-release.yml) runs this script, so a local run produces the
# same archive.
[CmdletBinding()]
param([string] $OutputDirectory)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\portable' }

# Native AOT finds the linker through Visual Studio's vcvarsall.bat. On Visual Studio 18 that script
# calls vswhere.exe by name, and without the installer folder on PATH the linker path comes back
# mangled ("'vswhere.exe' is not recognized...") and the launcher fails to link.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $installer) { $env:PATH = "$installer;$env:PATH" }

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.XrplFileAppVersion |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No XrplFileAppVersion in Directory.Build.props.' }

$staging = Join-Path $OutputDirectory 'XrplFile'
$appFolder = Join-Path $staging 'app'
$launcherOut = Join-Path $OutputDirectory 'launcher'
$zipName = "XrplFile-$version-win-x64-portable.zip"
$zipPath = Join-Path $OutputDirectory $zipName
foreach ($path in @($staging, $launcherOut, $zipPath, "$zipPath.sha256")) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}
New-Item -ItemType Directory -Path $appFolder -Force | Out-Null

# dotnet's output goes to the console, so the script's own output stays the ZIP path alone. The
# arguments travel as an array of strings: unquoted tokens passed through a function's $args are
# parsed as parameters first, and PowerShell 7 splits -p:Name=Value at the colon, which hands dotnet
# "Name=Value" as a second project (MSB1008). Windows PowerShell 5.1 does not, so a local run passes.
function Invoke-Dotnet([string[]] $Arguments) {
    & dotnet @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

Invoke-Dotnet @('publish', (Join-Path $root 'app\XrplFile.App\XrplFile.App.csproj'), '-c', 'Release',
    '-f', 'net10.0-windows10.0.19041.0', '-p:PublishProfile=Portable-win-x64', '-o', $appFolder, '--nologo')
Invoke-Dotnet @('publish', (Join-Path $root 'app\XrplFile.Launcher\XrplFile.Launcher.csproj'), '-c', 'Release',
    '-r', 'win-x64', '-o', $launcherOut, '--nologo')

# Without these the application starts to a blank or unstyled window, and nothing else would notice.
foreach ($required in @('wwwroot\index.html', 'wwwroot\app.css', 'XrplFile.App.exe')) {
    $path = Join-Path $appFolder $required
    if (-not (Test-Path $path) -or (Get-Item $path).Length -eq 0) {
        throw "The published application is missing $required"
    }
}

# What a user of the ZIP never needs: debug symbols, and the Windows App SDK's AI and machine
# learning runtime (ONNX Runtime, DirectML, Windows AI, WinML, the NPU probe), which the application
# never calls. Portable-win-x64.pubxml turns off WinML's start-up initialization, so nothing loads
# these at start either.
Get-ChildItem $appFolder -Recurse -File -Filter '*.pdb' | Remove-Item -Force
$unused = @('onnxruntime*.dll', 'DirectML.dll', 'Microsoft.ML.OnnxRuntime*.dll', 'Microsoft.Windows.AI*',
    'Microsoft.Windows.Vision*', 'Microsoft.Windows.Internal.Vision*', 'Microsoft.Windows.SemanticSearch*',
    'Microsoft.Graphics.Imaging*')
foreach ($pattern in $unused) { Get-ChildItem $appFolder -File -Filter $pattern | Remove-Item -Force }
Remove-Item (Join-Path $appFolder 'NpuDetect') -Recurse -Force -ErrorAction SilentlyContinue

# WinUI's own strings come as one folder of .mui files per Windows language, close to ninety of them.
# The window chrome only needs the languages the interface speaks; for any other the resource
# loader falls back to en-us. SatelliteResourceLanguages in the publish profile does the same for
# .NET's satellite assemblies, which are not .mui files and so never match here.
$muiLanguages = @('en-us', 'en-GB', 'ru-RU', 'de-DE', 'es-ES', 'es-MX', 'pt-BR', 'pt-PT', 'zh-CN')
Get-ChildItem $appFolder -Directory |
    Where-Object { $muiLanguages -notcontains $_.Name -and (Get-ChildItem $_.FullName -File -Filter '*.mui') } |
    Remove-Item -Recurse -Force

Copy-Item (Join-Path $launcherOut 'XrplFile.Launcher.exe') (Join-Path $staging 'XRPL File.exe')
Copy-Item (Join-Path $PSScriptRoot 'portable-readme.txt') (Join-Path $staging 'README.txt')

Compress-Archive -Path $staging -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zipPath.sha256" -Value "$hash *$zipName" -Encoding ascii -NoNewline

$files = (Get-ChildItem $staging -Recurse -File).Count
$megabytes = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "$zipName - $megabytes MB, $files files, SHA-256 $hash"
Write-Output $zipPath
