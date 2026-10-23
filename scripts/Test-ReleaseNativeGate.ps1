#Requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Folder)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Folder) { throw 'Use a new scratch folder.' }
[void][IO.Directory]::CreateDirectory($Folder)
$sourceRoot = Split-Path -Parent $PSScriptRoot
Copy-Item -LiteralPath (Join-Path $sourceRoot 'release.ps1') -Destination $Folder
if (Test-Path -LiteralPath (Join-Path $sourceRoot 'scripts\ReleaseCandidate.ps1')) {
    [void][IO.Directory]::CreateDirectory((Join-Path $Folder 'scripts'))
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'scripts\ReleaseCandidate.ps1') -Destination (Join-Path $Folder 'scripts\ReleaseCandidate.ps1')
}
[void][IO.Directory]::CreateDirectory((Join-Path $Folder 'third_party\sqlcipher'))
Copy-Item -LiteralPath (Join-Path $sourceRoot 'third_party\sqlcipher\e_sqlcipher.dll') -Destination (Join-Path $Folder 'third_party\sqlcipher\e_sqlcipher.dll')
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $Folder 'KillerShot.csproj'), '<Project><PropertyGroup><Version>1.0.0</Version><AssemblyVersion>1.0.0.0</AssemblyVersion><FileVersion>1.0.0.0</FileVersion><ReleaseDate>2026-10-23</ReleaseDate></PropertyGroup></Project>', $utf8)
[IO.File]::WriteAllText((Join-Path $Folder 'CHANGELOG.md'), '## [1.0.0] - 2026-10-23', $utf8)
[void][IO.Directory]::CreateDirectory((Join-Path $Folder 'Strings'))
Copy-Item -LiteralPath (Join-Path $sourceRoot 'Strings\en-US.xaml') -Destination (Join-Path $Folder 'Strings\en-US.xaml')
$testState = [pscustomobject]@{ PublishCalls = 0 }
function git {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'ls-remote' -and $args[1] -eq '--heads') { return ('a' * 40) + "`trefs/heads/main" }
    if ($args[0] -eq 'symbolic-ref') { return 'refs/remotes/origin/main' }
    if ($args[0] -eq 'rev-parse') {
        if ($args -contains '--abbrev-ref') { return 'main' }
        return 'a' * 40
    }
}
function dotnet {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'publish') {
        $testState.PublishCalls++
        throw 'BUILD_REACHED_BEFORE_NATIVE_GATE'
    }
    return 'No vulnerable packages.'
}
function Get-Process { return [pscustomobject]@{ Name='SimplySignDesktop' } }
$failure = ''
try { & (Join-Path $Folder 'release.ps1') -DryRun }
catch { $failure = $_.Exception.Message }
$passed = $testState.PublishCalls -eq 0 -and $failure -match 'Native.*signature|signature.*Native'
$result = [pscustomobject]@{ Passed=$passed; PublishCalls=$testState.PublishCalls; Failure=$failure; Boundary='Real release preflight; Git/build/signing/publication are inert or unreachable.' }
$result | ConvertTo-Json | Out-File -LiteralPath (Join-Path $Folder 'native-gate-result.json') -Encoding utf8
$result | Format-List
if (!$passed) { throw 'Unsigned native input reached build preparation or failed outside the native signature prerequisite.' }
