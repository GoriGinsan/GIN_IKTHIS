param(
    [Parameter(Mandatory=$true)][string]$GamePath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin')
)
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $GamePath).Path
$sdk = dotnet --list-sdks | Select-Object -Last 1
if ($sdk -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A .NET SDK with the C# compiler is required.' }
$compiler = Join-Path $Matches[2] "$($Matches[1])/Roslyn/bincore/csc.dll"
$plugins = Get-ChildItem -LiteralPath (Join-Path $game 'BepInEx/plugins') -Recurse -File
$refs = @('BepInEx.dll', '0Harmony.dll') | ForEach-Object { Join-Path $game "BepInEx/core/$_" }
$refs += @('mscorlib.dll','System.dll','System.Core.dll','System.Xml.dll',
    'UnityEngine.dll','UnityEngine.UI.dll','Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll') |
    ForEach-Object { Join-Path $game "CharaStudio_Data/Managed/$_" }
foreach ($name in 'KKAPI.dll','ExtensibleSaveFormat.dll','Timeline.dll') {
    $matches = @($plugins | Where-Object Name -eq $name)
    if ($matches.Count -ne 1) { throw "Expected one reference named $name under BepInEx/plugins." }
    $refs += $matches[0].FullName
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$dll = Join-Path (Resolve-Path -LiteralPath $OutputDirectory).Path 'GIN IKTHIS.dll'
$resource = Join-Path $PSScriptRoot 'assets/IKTHIS.png'
$argsList = @('/noconfig','/nologo','/nostdlib+','/codepage:65001','/target:library','/optimize+','/deterministic+','/langversion:3',
    "/out:$dll", "/resource:$resource,GinIkThis.IKTHIS.png")
$argsList += $refs | ForEach-Object { "/reference:$_" }
$argsList += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName
dotnet $compiler $argsList
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Get-FileHash -LiteralPath $dll -Algorithm SHA256
