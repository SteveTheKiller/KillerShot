param([Parameter(Mandatory=$true)][string]$WorkDirectory, [Parameter(Mandatory=$true)][string]$AppDll)
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'Use a new, empty work directory.' }
$null = New-Item -ItemType Directory -Path $work
$dll = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($AppDll))
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $work 'Probe.csproj'), $project)
[IO.File]::Copy((Join-Path $PSScriptRoot 'Probe.cs.txt'), (Join-Path $work 'Probe.cs'))
& dotnet run --project (Join-Path $work 'Probe.csproj') -- $dll
exit $LASTEXITCODE
