param([Parameter(Mandatory=$true)][string]$WorkDirectory)
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'Use a new, empty work directory.' }
$null = New-Item -ItemType Directory -Path $work
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Controls/DockHost.cs'))
$source = [Security.SecurityElement]::Escape($source)
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><Compile Include="$source" Link="DockHost.cs" /></ItemGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $work 'Probe.csproj'), $project)
[IO.File]::Copy((Join-Path $PSScriptRoot 'Probe.cs.txt'), (Join-Path $work 'Probe.cs'))
& dotnet run --project (Join-Path $work 'Probe.csproj') -- (Join-Path $work 'renders')
exit $LASTEXITCODE
