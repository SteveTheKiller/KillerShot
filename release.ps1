#Requires -Version 5.1
<#
.SYNOPSIS
    Prepare, accept, or publish an immutable KillerShot release candidate.
.DESCRIPTION
    1. Preflight: version, CHANGELOG date, ReleaseDate, translations, package vulnerabilities, and a
       clean default branch that matches origin.
    2. Publishes KillerShot.exe as one framework-dependent win-x64 file
       (Properties\PublishProfiles\PortableSingleFile.pubxml). The same file is the download, the
       portable copy and the installer: the footer's PORTABLE badge installs it per user, and
       "KillerShot.exe /silent" installs it machine-wide with admin rights or per user without.
       The silent install refuses any build without a valid Authenticode signature.
    3. Signs it, retrying the timestamp across three TSA endpoints, and runs
       "signtool verify /pa /v" as a post-sign gate.
    4. Builds the GPLv3 source archive from the exact tagged commit.
    5. Writes checksums, notes, and candidate.json, then stops for validation.
    6. AcceptCandidate binds the final validation record to those exact files.
    7. PublishCandidate verifies the retained acceptance hash and reuses the files without
       rebuilding or signing. Only that parameter set creates a tag or GitHub release.

    The self-tests need an interactive desktop and are not run here. Run the full suite on the
    release candidate first. WinGet, Chocolatey, the Store and the landing site are not handled
    here yet; add them from the Killendar or KillerShell scripts once the first release exists.

.PARAMETER CertThumbprint
    Preferred. SHA1 thumbprint of the code-signing certificate (40 hex chars, no spaces).
.PARAMETER CertName
    Fallback. CN (Subject) of the certificate in the Windows cert store.
.PARAMETER SkipSign
    Skip signing for local test builds. Only allowed with -DryRun.
.PARAMETER DryRun
    Compatibility option for candidate preparation. Allows SkipSign for development fixtures.
.PARAMETER CandidateDirectory
    New directory for a prepared candidate. An existing directory is never replaced.
.PARAMETER AcceptCandidate
    Path to candidate.json after all required acceptance gates have passed.
.PARAMETER ValidationRecord
    Final evidence record for the exact candidate. Acceptance stores a preserved copy.
.PARAMETER PublishCandidate
    Path to the accepted candidate.json. This mode performs no build or signing.
.PARAMETER Acceptance
    Path to its acceptance.json.
.PARAMETER AcceptanceSha256
    Acceptance hash retained independently when the candidate was accepted.

.EXAMPLE
    .\release.ps1 -DryRun -SkipSign
.EXAMPLE
    .\release.ps1 -CertThumbprint "AABBCC..."
#>
[CmdletBinding(DefaultParameterSetName='Prepare')]
param(
    [string]$CertThumbprint = "",
    [string]$CertName       = "Open Source Developer Stephen Riley",
    [Parameter(ParameterSetName='Prepare')][switch]$SkipSign,
    [Parameter(ParameterSetName='Prepare')][switch]$DryRun,
    [Parameter(ParameterSetName='Prepare')][string]$CandidateDirectory = '',
    [Parameter(Mandatory=$true,ParameterSetName='Accept')][string]$AcceptCandidate,
    [Parameter(Mandatory=$true,ParameterSetName='Accept')][string]$ValidationRecord,
    [Parameter(Mandatory=$true,ParameterSetName='Publish')][string]$PublishCandidate,
    [Parameter(Mandatory=$true,ParameterSetName='Publish')][string]$Acceptance,
    [Parameter(Mandatory=$true,ParameterSetName='Publish')][string]$AcceptanceSha256
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'scripts\ReleaseCandidate.ps1')

if ($SkipSign -and -not $DryRun) {
    throw "-SkipSign is only allowed with -DryRun. Unsigned builds cannot be published."
}

$proj       = Join-Path $PSScriptRoot "KillerShot.csproj"
$nativeInput = Join-Path $PSScriptRoot 'third_party\sqlcipher\e_sqlcipher.dll'

$tsaList = @(
    "http://timestamp.digicert.com",
    "http://timestamp.sectigo.com",
    "http://ts.ssl.com"
)

# origin/HEAD can go stale, so a candidate is only accepted if it still exists on the remote.
function Get-DefaultBranch {
    $remoteHeads = @(git ls-remote --heads origin 2>$null) |
        ForEach-Object { ($_ -split '\s+')[-1] -replace '^refs/heads/', '' }
    if (-not $remoteHeads) { return $null }
    $candidates = @()
    $originHead = git symbolic-ref --quiet refs/remotes/origin/HEAD 2>$null
    if ($originHead) { $candidates += (($originHead -replace '^refs/remotes/origin/', '').Trim()) }
    foreach ($c in @('main', 'master')) { if ($candidates -notcontains $c) { $candidates += $c } }
    foreach ($c in $candidates) { if ($c -and $remoteHeads -contains $c) { return $c } }
    return $null
}

function Assert-CleanSyncedBranch {
    $defaultBranch = Get-DefaultBranch
    if (-not $defaultBranch) { throw "Could not determine the default branch from origin" }
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne $defaultBranch) { throw "On branch '$branch', expected $defaultBranch" }
    $dirty = git status --porcelain
    if ($dirty) { throw "Working tree is not clean. Commit your changes first:`n$($dirty -join "`n")" }
    git fetch origin $defaultBranch --quiet
    if ($LASTEXITCODE -ne 0) { throw "Could not fetch origin/$defaultBranch" }
    if ((git rev-parse HEAD).Trim() -ne (git rev-parse "refs/remotes/origin/$defaultBranch").Trim()) {
        throw "Local $defaultBranch and origin/$defaultBranch differ. Push or pull first."
    }
    return $defaultBranch
}

function Read-StringMap([string]$Path) {
    [xml]$document = Get-Content -Path $Path -Raw -Encoding UTF8
    $map = @{}
    foreach ($node in $document.ResourceDictionary.ChildNodes) {
        if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
        $key = $node.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
        if ($key) { $map[$key] = [string]$node.InnerText }
    }
    return $map
}

function Get-ValidatedSourceCommit {
    Push-Location $PSScriptRoot
    try {
        $null = Assert-CleanSyncedBranch
        return (git rev-parse HEAD).Trim()
    } finally { Pop-Location }
}

if ($PSCmdlet.ParameterSetName -eq 'Accept') {
    $receipt = New-ReleaseAcceptance -Manifest $AcceptCandidate -ValidationRecord $ValidationRecord -ExpectedCommit (Get-ValidatedSourceCommit) -NativeInput $nativeInput -CertName $CertName -CertThumbprint $CertThumbprint
    Write-Host "Acceptance: $receipt"
    Write-Host "Retain this acceptance SHA256 separately: $((Get-FileHash -LiteralPath $receipt -Algorithm SHA256).Hash)"
    return
}

if ($PSCmdlet.ParameterSetName -eq 'Publish') {
    gh auth status
    if ($LASTEXITCODE -ne 0) { throw 'GitHub authentication failed.' }
    $login = (gh api user --jq '.login' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $login -ne 'SteveTheKiller') { throw 'Release publication requires the established SteveTheKiller identity.' }
    $getCommit = { Get-ValidatedSourceCommit }
    $publishStep = {
        param($step, $candidate)
        $tag = 'v' + $candidate.Data.Version
        Push-Location $PSScriptRoot
        try {
            switch ($step) {
                'tag' {
                    if (git tag --list $tag) { throw "Tag $tag already exists." }
                    if (git ls-remote --tags origin $tag) { throw "Tag $tag already exists on origin." }
                    git tag -a $tag $candidate.Data.SourceCommit -m "KillerShot $tag"
                    if ($LASTEXITCODE -ne 0) { throw 'Tag creation failed.' }
                }
                'push' {
                    git push origin $tag
                    if ($LASTEXITCODE -ne 0) { throw 'Tag push failed.' }
                }
                'release' {
                    gh release create $tag @($candidate.PublishPaths) --title "KillerShot $tag" --notes-file (Join-Path $candidate.Directory 'release-notes.md') --verify-tag
                    if ($LASTEXITCODE -ne 0) { throw 'Release creation failed.' }
                    gh release view $tag --json url --jq '.url'
                    if ($LASTEXITCODE -ne 0) { throw 'Release verification failed.' }
                }
            }
        } finally { Pop-Location }
    }
    Invoke-ReleasePromotion -Manifest $PublishCandidate -Acceptance $Acceptance -AcceptanceSha256 $AcceptanceSha256 -NativeInput $nativeInput -GetSourceCommit $getCommit -PublishStep $publishStep -CertName $CertName -CertThumbprint $CertThumbprint
    return
}

# --- 0. Native signature, first ---
# The exe embeds third_party\sqlcipher\e_sqlcipher.dll as it sits in the repo, and the source archive
# comes from the tagged commit, so the DLL is signed once per rebuild and committed, never inside this
# script. Checked first so an unsigned rebuild stops the run before any build or audit.
$nativeStatus = [string](Get-AuthenticodeSignature -LiteralPath $nativeInput).Status
if ($nativeStatus -ne 'Valid') {
    $fix = "third_party\sqlcipher\e_sqlcipher.dll is not signed ($nativeStatus). Sign it once and commit it:`n" +
        "  signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /n `"$CertName`" /d `"SQLCipher for KillerShot`" third_party\sqlcipher\e_sqlcipher.dll`n" +
        "then update the hash and Signature rows in third_party\sqlcipher\README.md."
    if ($SkipSign) { Write-Host "`n  WARNING: $fix" -ForegroundColor Yellow } else { throw $fix }
}

# --- 1. Release metadata preflight ---
Write-Host "`n==> Release metadata preflight..." -ForegroundColor Cyan
$csprojRaw = Get-Content -Path $proj -Raw
if ($csprojRaw -notmatch '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>') { throw "No <Version>x.y.z</Version> in KillerShot.csproj" }
$Version = $Matches[1]
$Tag = "v$Version"
$expectedFileVersion = "$Version.0"
foreach ($field in 'AssemblyVersion', 'FileVersion') {
    if ($csprojRaw -notmatch "<$field>([0-9.]+)</$field>") { throw "No <$field> in KillerShot.csproj" }
    if ($Matches[1] -ne $expectedFileVersion) { throw "KillerShot.csproj <$field> is $($Matches[1]), expected $expectedFileVersion" }
}
# The command line project carries its own version fields; keep them in step with the app.
$cliRaw = Get-Content -Path (Join-Path $PSScriptRoot 'KillerShot.Cli\KillerShot.Cli.csproj') -Raw
foreach ($pair in @(@('Version', $Version), @('AssemblyVersion', $expectedFileVersion), @('FileVersion', $expectedFileVersion))) {
    $field, $want = $pair
    if ($cliRaw -notmatch "<$field>([0-9.]+)</$field>") { throw "No <$field> in KillerShot.Cli.csproj" }
    if ($Matches[1] -ne $want) { throw "KillerShot.Cli.csproj <$field> is $($Matches[1]), expected $want" }
}
Write-Host "    Version: $Version (tag $Tag)"

$changelog = Get-Content -Path (Join-Path $PSScriptRoot 'CHANGELOG.md') -Raw
if ($changelog -notmatch [regex]::Escape("## [$Version]")) { throw "CHANGELOG.md has no [$Version] section" }
if ($changelog -match ('(?im)^## \[' + [regex]::Escape($Version) + '\] - Unreleased\s*$')) {
    throw "CHANGELOG.md section [$Version] is still marked Unreleased"
}
# The About card shows <ReleaseDate> beside the version; it must equal the CHANGELOG date.
if ($csprojRaw -notmatch '<ReleaseDate>([0-9]{4}-[0-9]{2}-[0-9]{2})</ReleaseDate>') {
    throw "No <ReleaseDate>yyyy-MM-dd</ReleaseDate> in KillerShot.csproj"
}
$releaseDate = $Matches[1]
if ($changelog -notmatch ('## \[' + [regex]::Escape($Version) + '\] - ([0-9]{4}-[0-9]{2}-[0-9]{2})')) {
    throw "CHANGELOG.md section [$Version] has no yyyy-MM-dd date"
}
if ($releaseDate -ne $Matches[1]) {
    throw "csproj <ReleaseDate> is $releaseDate but CHANGELOG [$Version] is dated $($Matches[1])."
}
Write-Host "    Release date: $releaseDate"

# --- 2. Git preflight ---
Write-Host "`n==> Git preflight..." -ForegroundColor Cyan
Push-Location $PSScriptRoot
try {
    $defaultBranch = Assert-CleanSyncedBranch
    if (git tag --list $Tag) { throw "Tag $Tag already exists" }
    if (git ls-remote --tags origin $Tag) { throw "Tag $Tag already exists on origin" }
    $releaseCommit = (git rev-parse HEAD).Trim()
    Write-Host "    $defaultBranch at $releaseCommit matches origin"
} finally { Pop-Location }

# --- 3. SimplySign preflight ---
if (-not $SkipSign -and -not (Get-Process -Name "SimplySignDesktop" -ErrorAction SilentlyContinue)) {
    Write-Warning "SimplySign Desktop does not appear to be running."
    Write-Host    "    Start it and wait for 'Connected', then press Enter. Ctrl+C aborts."
    $null = Read-Host
}

# --- 4. Translation parity ---
# Every language must carry the English key set with matching placeholders, or string.Format
# throws on a translation that dropped one.
Write-Host "`n==> Checking translations..." -ForegroundColor Cyan
$stringsDir = Join-Path $PSScriptRoot 'Strings'
$english = Read-StringMap (Join-Path $stringsDir 'en-US.xaml')
$localeFiles = @(Get-ChildItem $stringsDir -Filter '*.xaml')
foreach ($localeFile in $localeFiles) {
    if ($localeFile.Name -eq 'en-US.xaml') { continue }
    $localized = Read-StringMap $localeFile.FullName
    $missing = @($english.Keys | Where-Object { -not $localized.ContainsKey($_) })
    $extra   = @($localized.Keys | Where-Object { -not $english.ContainsKey($_) })
    $empty   = @($localized.Keys | Where-Object { [string]::IsNullOrWhiteSpace($localized[$_]) })
    $placeholders = @($english.Keys | Where-Object {
        $localized.ContainsKey($_) -and
        (@([regex]::Matches($english[$_], '\{\d+(?::[^}]*)?\}') | ForEach-Object Value | Sort-Object) -join '|') -ne
        (@([regex]::Matches($localized[$_], '\{\d+(?::[^}]*)?\}') | ForEach-Object Value | Sort-Object) -join '|') })
    if ($missing.Count -or $extra.Count -or $empty.Count -or $placeholders.Count) {
        throw "$($localeFile.Name): missing=$($missing -join ',') extra=$($extra -join ',') empty=$($empty -join ',') placeholders=$($placeholders -join ',')"
    }
}
Write-Host "    Translations OK: $($english.Count) keys across $($localeFiles.Count) languages" -ForegroundColor Green

# --- 5. Punctuation ---
# Family rule: no en or em dashes in shipped text.
$dashHits = @(Get-ChildItem $PSScriptRoot -Recurse -Include *.cs, *.xaml, *.md, *.html -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|third_party)\\' } |
    Select-String -Pattern ('[' + [char]0x2013 + [char]0x2014 + ']'))
if ($dashHits.Count) { throw "En or em dashes found:`n$($dashHits | Select-Object -First 10 | Out-String)" }

# --- 6. Vulnerability audit ---
Write-Host "`n==> Auditing package vulnerabilities..." -ForegroundColor Cyan
$auditText = (@(dotnet list $proj package --vulnerable --include-transitive 2>&1) | Out-String).TrimEnd()
if ($LASTEXITCODE -ne 0) { throw "Package vulnerability audit failed to run." }
Write-Host $auditText
if ($auditText -match '(?i)has the following vulnerable packages') { throw "Vulnerable packages were found." }

# --- 7. Publish ---
if (-not $SkipSign) {
    $nativeSigner = Assert-ReleaseSignature -Path $nativeInput -CertName $CertName -CertThumbprint $CertThumbprint -Label 'Native SQLCipher'
}
$nativeHashAtBuild = (Get-FileHash -LiteralPath $nativeInput -Algorithm SHA256).Hash
if (!$CandidateDirectory) {
    $candidateRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'scratch\KillerShot-candidates'
    $CandidateDirectory = Join-Path $candidateRoot ("$Version-" + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
}
$releaseDir = [IO.Path]::GetFullPath($CandidateDirectory)
if (Test-Path -LiteralPath $releaseDir) { throw 'CandidateDirectory already exists. Choose a new directory.' }
$null = New-Item -ItemType Directory -Path $releaseDir -ErrorAction Stop
$publishDir = Join-Path $releaseDir 'build'
$exe = Join-Path $releaseDir 'KillerShot.exe'
$licenseOut = Join-Path $releaseDir 'LICENSE.txt'
Write-Host "`n==> Publishing KillerShot.exe..." -ForegroundColor Cyan
dotnet publish $proj -p:PublishProfile=PortableSingleFile --output $publishDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
[IO.File]::Copy((Join-Path $publishDir 'KillerShot.exe'), $exe, $false)
[IO.File]::Copy((Join-Path $PSScriptRoot 'LICENSE.txt'), $licenseOut, $false)
$thirdPartyLicenses = @(Get-ChildItem (Join-Path $PSScriptRoot 'third_party\sqlcipher') -Filter 'LICENSE-*.txt' |
    ForEach-Object { $target=Join-Path $releaseDir $_.Name; [IO.File]::Copy($_.FullName, $target, $false); $target })
$actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
if ($actual -ne $expectedFileVersion) { throw "KillerShot.exe is file version $actual, expected $expectedFileVersion" }
# A small file means the single-file bundle lost its managed dependencies.
if ((Get-Item $exe).Length -lt 20MB) { throw "KillerShot.exe is only $('{0:N1}' -f ((Get-Item $exe).Length / 1MB)) MB; the bundle is incomplete." }

# --- 8. Sign ---
if (-not $SkipSign) {
    $kitBase = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $signtool = Get-ChildItem "$kitBase\*\x64\signtool.exe" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK." }
    $certArgs = if ($CertThumbprint) { @("/sha1", $CertThumbprint) } else { @("/n", $CertName) }

    Write-Host "`n==> Signing KillerShot.exe..." -ForegroundColor Cyan
    $signed = $false
    foreach ($tsa in $tsaList) {
        & $signtool sign /fd sha256 /tr $tsa /td sha256 @certArgs /d "KillerShot" /du "https://killershot.net" /v $exe
        if ($LASTEXITCODE -eq 0) { $signed = $true; break }
        Start-Sleep -Seconds 3
    }
    if (-not $signed) { throw "Signing failed on all TSA endpoints." }

    & $signtool verify /pa /v $exe
    if ($LASTEXITCODE -ne 0) { throw "signtool verify FAILED. DO NOT RELEASE." }
    $signature = Get-AuthenticodeSignature -LiteralPath $exe
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode validation failed with status $($signature.Status)."
    }
    $signer = $signature.SignerCertificate
    $actualCN = $signer.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    $actualThumb = $signer.Thumbprint
    # /n matches a subject substring, so confirm the certificate that signed is the one intended.
    if ($CertThumbprint -and $actualThumb -ne $CertThumbprint.ToUpperInvariant()) { throw "Signed with $actualThumb, expected $CertThumbprint." }
    if (-not $CertThumbprint -and $actualCN -ne $CertName) { throw "Signed by '$actualCN', expected '$CertName'." }
    $null = Assert-ReleaseSignature -Path $exe -CertName $CertName -CertThumbprint $CertThumbprint -Label 'Executable'
} else {
    Write-Host "`n  WARNING: -SkipSign is set. KillerShot.exe IS NOT SIGNED. Do not distribute it." -ForegroundColor Red
    $actualCN = "(not signed)"; $actualThumb = "(not signed)"
}

# --- 9. Command line smoke test ---
Write-Host "`n==> Command line smoke test..." -ForegroundColor Cyan
$versionOut = (& $exe version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "KillerShot.exe version failed with exit code $LASTEXITCODE." }
Write-Host "    $versionOut" -ForegroundColor Green

# Prove the native bytes embedded in this executable match the admitted signed input.
if ((Get-FileHash -LiteralPath $nativeInput -Algorithm SHA256).Hash -ne $nativeHashAtBuild) { throw 'Native input changed during preparation.' }
[IO.File]::Copy($nativeInput, (Join-Path $releaseDir 'e_sqlcipher.dll'), $false)
$nativeProof = Join-Path $releaseDir 'native-validation'
[void][IO.Directory]::CreateDirectory($nativeProof)
& $exe --selftest-library-native-probe (Join-Path $nativeProof 'cache') (Join-Path $nativeProof 'result.txt')
if ($LASTEXITCODE -ne 0) { throw 'Packaged native validation failed.' }
$extracted = @(Get-ChildItem -LiteralPath (Join-Path $nativeProof 'cache') -Filter 'e_sqlcipher.dll' -File -Recurse)
if ($extracted.Count -ne 1 -or (Get-FileHash -LiteralPath $extracted[0].FullName -Algorithm SHA256).Hash -ne $nativeHashAtBuild) { throw 'Embedded native differs from the admitted input.' }

# --- 10. GPLv3 source archive from the release commit ---
Push-Location $PSScriptRoot
try {
    $srcZip = Join-Path $releaseDir "KillerShot-$Version-src.zip"
    if (Test-Path -LiteralPath $srcZip) { throw 'Candidate source archive already exists.' }
    git archive --format=zip --prefix="KillerShot-$Version/" -o $srcZip $releaseCommit -- . ':(exclude)shot-landing'
    if ($LASTEXITCODE -ne 0) { throw "git archive failed." }
} finally { Pop-Location }

# --- 11. SHA256SUMS.txt ---
$assets = @($exe, $licenseOut) + $thirdPartyLicenses + @($srcZip)
$sumsPath = Join-Path $releaseDir "SHA256SUMS.txt"
# The standard "hash  name" form, so sha256sum -c can check a download.
$lines = foreach ($a in $assets) { "$((Get-FileHash -LiteralPath $a -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($a))" }
[IO.File]::WriteAllLines($sumsPath, [string[]]$lines, [Text.UTF8Encoding]::new($false))

Write-Host "`n==============================================================" -ForegroundColor Cyan
Write-Host   "  KillerShot $Tag release artifacts in $releaseDir"
$lines | ForEach-Object { Write-Host "  $_" -ForegroundColor Green }
Write-Host   "  Signer     : $actualCN"
Write-Host   "  Thumbprint : $actualThumb"
Write-Host "==============================================================" -ForegroundColor Cyan

# --- 12. Release notes, tag and GitHub release ---
$notes = New-Object System.Collections.Generic.List[string]
$inSection = $false
foreach ($line in (Get-Content -Path (Join-Path $PSScriptRoot 'CHANGELOG.md') -Encoding UTF8)) {
    if ($line -match "^## \[$([regex]::Escape($Version))\]") { $inSection = $true; continue }
    if ($inSection -and $line -match '^## \[') { break }
    if ($inSection) { $notes.Add($line) }
}
if ($notes.Count -eq 0) { throw "Could not extract [$Version] notes from CHANGELOG.md" }
$notes.Add('')
$notes.Add('KillerShot.exe runs as a portable copy and installs itself from the PORTABLE badge in the footer, or with "KillerShot.exe /silent" for deployment tools. It needs the .NET 10 Desktop Runtime (x64). The complete GPLv3 source for this release is attached as ' + [IO.Path]::GetFileName($srcZip) + '.')
$notesFile = Join-Path $releaseDir 'release-notes.md'
[IO.File]::WriteAllLines($notesFile, $notes, [Text.UTF8Encoding]::new($false))

if ((Get-ValidatedSourceCommit) -ne $releaseCommit) { throw 'HEAD changed during candidate preparation.' }
$manifest = New-ReleaseCandidate -Directory $releaseDir -SourceCommit $releaseCommit -Version $Version -DevelopmentOnly:$SkipSign -CertName $CertName -CertThumbprint $CertThumbprint
Write-Host "`nCandidate prepared: $manifest"
Write-Host 'Validate these exact files, then use -AcceptCandidate with the final validation record. Publish with -PublishCandidate and the independently retained acceptance SHA256.'
