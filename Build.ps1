param([string]$Destination = (Join-Path $PSScriptRoot 'bin'))

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Windows x64 and .NET Framework 4.8 or newer are required.' }
$netRelease = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release
if ($netRelease -lt 528040) { throw '.NET Framework 4.8 or newer is required.' }
$refs = foreach ($name in @('UIAutomationClient','UIAutomationTypes','WindowsBase')) {
    $assembly = Get-ChildItem -LiteralPath "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\$name" -Recurse -Filter "$name.dll" |
        Select-Object -First 1
    if (-not $assembly) { throw "Missing .NET Framework assembly $name" }
    "/reference:$($assembly.FullName)"
}
$files = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -File -Filter '*.cs' | ForEach-Object FullName
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$output = Join-Path $Destination 'KeyLib.exe'
$references = @('/reference:System.Web.Extensions.dll', '/reference:System.Security.dll') + $refs
$icon = Join-Path $PSScriptRoot 'assets\KeyLib.ico'
& $compiler /nologo /target:winexe /platform:x64 /codepage:65001 /optimize+ "/out:$output" "/win32icon:$icon" "/resource:$icon,KeyLib.Brand.ico" @references @files
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)." }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third-party\libsodium.dll') -Destination $Destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third-party\libsodium-LICENSE.txt') -Destination $Destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md'), (Join-Path $PSScriptRoot 'LICENSE') -Destination $Destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'extension') -Destination $Destination -Recurse -Force
Write-Output $output
