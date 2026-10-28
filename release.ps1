#Requires -Version 5.1
<#
.SYNOPSIS
    KillerShot release script: check, build, sign, verify, bundle source, and publish.
.DESCRIPTION
    1. Preflight: version, CHANGELOG date, ReleaseDate, translations, package vulnerabilities, and a
       clean default branch that matches origin.
    2. Publishes and signs KillerShot.PrintScreen.exe, the helper that catches Print Screen over
       windows run as administrator (PrintScreenHelper/), then publishes KillerShot.exe, which
       carries that signed helper inside, as one framework-dependent win-x64 file
       (Properties\PublishProfiles\PortableSingleFile.pubxml). The same file is the download, the
       portable copy and the installer: the footer's PORTABLE badge installs it per user, and
       "KillerShot.exe /silent" installs it machine-wide with admin rights or per user without.
       The silent install refuses any build without a valid Authenticode signature.
    3. Signs it, retrying the timestamp across three TSA endpoints, and runs
       "signtool verify /pa /v" as a post-sign gate.
    4. Builds the GPLv3 source archive from the exact tagged commit.
    5. Writes SHA256SUMS.txt, extracts release notes from the CHANGELOG, tags, and creates the
       GitHub release.

    6. After the GitHub release exists, writes the version, release date, size and SHA-256 into
       the landing site (shot-landing) and commits and pushes it. A dry run checks that every
       one of those facts can still be found in the site's markup, without writing anything.

    It also builds and signs KillerShot-Store.exe, the same app with the .NET runtime inside, and
    attaches it to the release for the Microsoft Store's package URL
    (killershot.net/store/<version>/KillerShot-Store.exe). Submitting it in Partner Center stays
    manual. WinGet and Chocolatey publish from their GitHub workflows when the release appears.

    The self-tests need an interactive desktop and are not run here. Run the full suite on the
    release candidate first.

.PARAMETER CertThumbprint
    Preferred. SHA1 thumbprint of the code-signing certificate (40 hex chars, no spaces).
.PARAMETER CertName
    Fallback. CN (Subject) of the certificate in the Windows cert store.
.PARAMETER SkipSign
    Skip signing for local test builds. Only allowed with -DryRun.
.PARAMETER DryRun
    Build and validate without tagging or publishing.

.EXAMPLE
    .\release.ps1 -DryRun -SkipSign
.EXAMPLE
    .\release.ps1 -CertThumbprint "AABBCC..."
#>
param(
    [string]$CertThumbprint = "",
    [string]$CertName       = "Open Source Developer Stephen Riley",
    [switch]$SkipSign,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($SkipSign -and -not $DryRun) {
    throw "-SkipSign is only allowed with -DryRun. Unsigned builds cannot be published."
}

$proj       = Join-Path $PSScriptRoot "KillerShot.csproj"
$publishDir = Join-Path $PSScriptRoot "bin\Release\publish\single-file-win-x64"
$releaseDir = Join-Path $PSScriptRoot "bin\Release\release"
$exe        = Join-Path $releaseDir "KillerShot.exe"
$licenseOut = Join-Path $releaseDir "LICENSE.txt"

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

# Landing-page find/replace that refuses to silently do nothing, as in the KillerNotes script. A
# plain -replace whose pattern no longer matches leaves the text untouched, the page then reads as
# already current, and the release ships with a stale fact on the site. Changed markup stops the
# release instead.
function Edit-SiteFact {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Replacement,
        [Parameter(Mandatory)][string]$What
    )
    if ($Text -notmatch $Pattern) {
        throw "Landing page: could not find $What. The markup changed; update the pattern in release.ps1 (Update-LandingFacts)."
    }
    return ($Text -replace $Pattern, $Replacement)
}

# The release facts on shot-landing: index.html's hero block (version, released date, size,
# sha256) and the verEgg footer version on every page. -CheckOnly runs every edit in memory and
# writes nothing, so a dry run finds changed markup before a real release does. Returns the
# pages whose text changed. ReadAllText/WriteAllText keep the files BOM-less UTF-8.
function Update-LandingFacts {
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$ReleaseDate,
        [Parameter(Mandatory)][string]$ExePath,
        [switch]$CheckOnly,
        [string]$SiteDir = ''
    )
    $siteDir = if ($SiteDir) { $SiteDir } else { Join-Path $PSScriptRoot 'shot-landing' }
    $exeHash = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $exeSize = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:N2} MB exe', (Get-Item -LiteralPath $ExePath).Length / 1MB)
    $changed = New-Object System.Collections.Generic.List[string]

    $indexPath = Join-Path $siteDir 'index.html'
    $indexRaw  = [IO.File]::ReadAllText($indexPath)
    $indexNew  = Edit-SiteFact $indexRaw '(data-i18n="ui_version">version</span>&nbsp;<span class="v">)KillerShot v[0-9]+\.[0-9]+\.[0-9]+' ('${1}' + "KillerShot v$Version") 'the hero version'
    $indexNew  = Edit-SiteFact $indexNew '(data-i18n="ui_released">released</span>&nbsp;<span class="v">)[0-9]{4}-[0-9]{2}-[0-9]{2}' ('${1}' + $ReleaseDate) 'the hero released date'
    $indexNew  = Edit-SiteFact $indexNew '(data-i18n="ui_size">size</span>&nbsp;<span class="v">)[^<]*' ('${1}' + $exeSize) 'the hero size'
    $indexNew  = Edit-SiteFact $indexNew '<span class="v hash">[0-9a-f]{32}<br>[0-9a-f]{32}</span>' ('<span class="v hash">' + $exeHash.Substring(0, 32) + '<br>' + $exeHash.Substring(32, 32) + '</span>') 'the hero sha256'
    if ($indexNew -ne $indexRaw) {
        if (-not $CheckOnly) { [IO.File]::WriteAllText($indexPath, $indexNew) }
        $changed.Add('index.html')
    }
    foreach ($page in 'index.html', 'about.html', 'help.html', 'technical.html', 'mcp.html') {
        $path = Join-Path $siteDir $page
        $text = if ($page -eq 'index.html' -and -not $CheckOnly) { [IO.File]::ReadAllText($path) } elseif ($page -eq 'index.html') { $indexNew } else { [IO.File]::ReadAllText($path) }
        $new  = Edit-SiteFact $text '(id="verEgg"[^>]*>)v[0-9]+\.[0-9]+\.[0-9]+' ('${1}' + "v$Version") "the verEgg footer version in $page"
        if ($new -ne $text) {
            if (-not $CheckOnly) { [IO.File]::WriteAllText($path, $new) }
            if (-not $changed.Contains($page)) { $changed.Add($page) }
        }
    }
    Write-Host "    shot-landing: KillerShot v$Version, released $ReleaseDate, $exeSize, sha256 $exeHash"
    return ,$changed
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
    Select-String -Pattern "[–—]")
if ($dashHits.Count) { throw "En or em dashes found:`n$($dashHits | Select-Object -First 10 | Out-String)" }

# --- 6. Vulnerability audit ---
Write-Host "`n==> Auditing package vulnerabilities..." -ForegroundColor Cyan
$auditText = (@(dotnet list $proj package --vulnerable --include-transitive 2>&1) | Out-String).TrimEnd()
if ($LASTEXITCODE -ne 0) { throw "Package vulnerability audit failed to run." }
Write-Host $auditText
if ($auditText -match '(?i)has the following vulnerable packages') { throw "Vulnerable packages were found." }

# Signs one file, retrying the timestamp across the TSA endpoints, then checks the signature and
# that the intended certificate made it. Sets $script:actualCN and $script:actualThumb.
$script:signtool = $null
function Invoke-CodeSign([string]$Path, [string]$Description) {
    if (-not $script:signtool) {
        $kitBase = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
        $script:signtool = Get-ChildItem "$kitBase\*\x64\signtool.exe" -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        if (-not $script:signtool) { throw "signtool.exe not found. Install the Windows SDK." }
    }
    $certArgs = if ($CertThumbprint) { @("/sha1", $CertThumbprint) } else { @("/n", $CertName) }

    Write-Host "`n==> Signing $(Split-Path $Path -Leaf)..." -ForegroundColor Cyan
    $signed = $false
    foreach ($tsa in $tsaList) {
        & $script:signtool sign /fd sha256 /tr $tsa /td sha256 @certArgs /d $Description /du "https://killershot.net" /v $Path
        if ($LASTEXITCODE -eq 0) { $signed = $true; break }
        Start-Sleep -Seconds 3
    }
    if (-not $signed) { throw "Signing $(Split-Path $Path -Leaf) failed on all TSA endpoints." }

    & $script:signtool verify /pa /v $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool verify FAILED for $(Split-Path $Path -Leaf). DO NOT RELEASE." }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode validation of $(Split-Path $Path -Leaf) failed with status $($signature.Status)."
    }
    $signer = $signature.SignerCertificate
    $script:actualCN = $signer.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    $script:actualThumb = $signer.Thumbprint
    # /n matches a subject substring, so confirm the certificate that signed is the one intended.
    if ($CertThumbprint -and $script:actualThumb -ne $CertThumbprint.ToUpperInvariant()) { throw "Signed with $($script:actualThumb), expected $CertThumbprint." }
    if (-not $CertThumbprint -and $script:actualCN -ne $CertName) { throw "Signed by '$($script:actualCN)', expected '$CertName'." }
}

# --- 7. Publish ---
# The Print Screen helper first. Windows grants it UI Access (Print Screen over admin windows)
# only with its own valid signature, so it is signed before KillerShot.exe carries it inside.
Write-Host "`n==> Publishing KillerShot.PrintScreen.exe..." -ForegroundColor Cyan
$helperDir = Join-Path $PSScriptRoot "bin\PrintScreenHelper"
$helperExe = Join-Path $helperDir "KillerShot.PrintScreen.exe"
if (Test-Path $helperDir) { Remove-Item $helperDir -Recurse -Force }
dotnet publish (Join-Path $PSScriptRoot "PrintScreenHelper\KillerShot.PrintScreen.csproj") -c Release -o $helperDir `
    "-p:Version=$Version" "-p:FileVersion=$expectedFileVersion" "-p:AssemblyVersion=$expectedFileVersion" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Publishing the Print Screen helper failed." }
if (-not (Test-Path $helperExe)) { throw "The Print Screen helper was not produced: $helperExe" }
if (-not ([IO.File]::ReadAllText($helperExe) -match 'uiAccess="true"')) { throw "The Print Screen helper lost its UI Access manifest." }
if (-not $SkipSign) { Invoke-CodeSign $helperExe "KillerShot Print Screen helper" }

Write-Host "`n==> Publishing KillerShot.exe..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $proj -p:PublishProfile=PortableSingleFile -p:PrintScreenHelperPrebuilt=true --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
if (-not [IO.Directory]::Exists($releaseDir)) { [void][IO.Directory]::CreateDirectory($releaseDir) }
Copy-Item -LiteralPath (Join-Path $publishDir 'KillerShot.exe') -Destination $exe -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE.txt') -Destination $licenseOut -Force
$thirdPartyLicenses = @(Get-ChildItem (Join-Path $PSScriptRoot 'third_party\sqlcipher') -Filter 'LICENSE-*.txt' |
    ForEach-Object { Copy-Item $_.FullName -Destination $releaseDir -Force; Join-Path $releaseDir $_.Name })
$actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
if ($actual -ne $expectedFileVersion) { throw "KillerShot.exe is file version $actual, expected $expectedFileVersion" }
# A small file means the single-file bundle lost its managed dependencies. Since 1.1.0 dropped
# the 25 MB Windows SDK projection the complete exe is about 7 MB, so the floor is 5 MB.
if ((Get-Item $exe).Length -lt 5MB) { throw "KillerShot.exe is only $('{0:N1}' -f ((Get-Item $exe).Length / 1MB)) MB; the bundle is incomplete." }

# --- 8. Sign ---
if (-not $SkipSign) {
    Invoke-CodeSign $exe "KillerShot"
} else {
    Write-Host "`n  WARNING: -SkipSign is set. KillerShot.exe IS NOT SIGNED. Do not distribute it." -ForegroundColor Red
    $actualCN = "(not signed)"; $actualThumb = "(not signed)"
}

# --- 8b. Microsoft Store package ---
# The Store installs KillerShot-Store.exe, which killershot.net/store/<version>/ serves from this
# release. It is the same app with the .NET runtime inside, so a silent Store install never needs
# the runtime on the PC, and it carries the same signed Print Screen helper.
Write-Host "`n==> Publishing KillerShot-Store.exe..." -ForegroundColor Cyan
$storeDir = Join-Path $PSScriptRoot "bin\Release\publish\store-win-x64"
$storeExe = Join-Path $releaseDir "KillerShot-Store.exe"
if (Test-Path $storeDir) { Remove-Item $storeDir -Recurse -Force }
dotnet publish $proj -p:PublishProfile=PortableSingleFile -p:SelfContained=true -p:EnableCompressionInSingleFile=true `
    -p:PrintScreenHelperPrebuilt=true "-p:PublishDir=$storeDir\" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Publishing the Store package failed." }
Copy-Item -LiteralPath (Join-Path $storeDir 'KillerShot.exe') -Destination $storeExe -Force
$storeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($storeExe).FileVersion
if ($storeVersion -ne $expectedFileVersion) { throw "KillerShot-Store.exe is file version $storeVersion, expected $expectedFileVersion" }
# With the runtime inside it is about 65 MB; much less means the runtime did not go in.
if ((Get-Item $storeExe).Length -lt 40MB) { throw "KillerShot-Store.exe is only $('{0:N1}' -f ((Get-Item $storeExe).Length / 1MB)) MB; the runtime is missing." }
if (-not $SkipSign) { Invoke-CodeSign $storeExe "KillerShot" }
$storeOut = (& $storeExe version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $storeOut -ne $Version) { throw "KillerShot-Store.exe version answered '$storeOut' (exit $LASTEXITCODE), expected $Version." }

# --- 9. Command line smoke test ---
Write-Host "`n==> Command line smoke test..." -ForegroundColor Cyan
$versionOut = (& $exe version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "KillerShot.exe version failed with exit code $LASTEXITCODE." }
Write-Host "    $versionOut" -ForegroundColor Green

# --- 10. GPLv3 source archive from the release commit ---
Push-Location $PSScriptRoot
try {
    $srcZip = Join-Path $releaseDir "KillerShot-$Version-src.zip"
    if (Test-Path $srcZip) { Remove-Item $srcZip -Force }
    git archive --format=zip --prefix="KillerShot-$Version/" -o $srcZip $releaseCommit -- . ':(exclude)shot-landing'
    if ($LASTEXITCODE -ne 0) { throw "git archive failed." }
} finally { Pop-Location }

# --- 11. SHA256SUMS.txt ---
$assets = @($exe, $storeExe, $licenseOut) + $thirdPartyLicenses + @($srcZip)
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
$notesFile = Join-Path $env:TEMP "KillerShot-$Version-notes.md"
[IO.File]::WriteAllLines($notesFile, $notes, [Text.UTF8Encoding]::new($false))

if ($DryRun) {
    # The landing facts are written only on a real release, after the GitHub release exists. A dry
    # run checks that every one of them can still be found, so changed markup turns up here.
    Write-Host "`n==> Checking the landing page release facts..." -ForegroundColor Cyan
    $null = Update-LandingFacts -Version $Version -ReleaseDate $releaseDate -ExePath $exe -CheckOnly
    Write-Host "`n==> DryRun: stopping before tag and release. Notes: $notesFile" -ForegroundColor Yellow
    exit 0
}

Push-Location $PSScriptRoot
try {
    $null = Assert-CleanSyncedBranch
    if ((git rev-parse HEAD).Trim() -ne $releaseCommit) { throw "HEAD changed during the build. Rerun the release." }
    git tag -a $Tag -m "KillerShot $Tag"
    if ($LASTEXITCODE -ne 0) { throw "git tag failed" }
    git push origin $Tag
    if ($LASTEXITCODE -ne 0) { throw "Tag push failed" }
    gh release create $Tag @($assets + $sumsPath) --title "KillerShot $Tag" --notes-file $notesFile --verify-tag
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
    gh release view $Tag --json url --jq '.url'

    # --- 13. Landing page release facts ---
    # After the release exists, so the site never announces a version whose download is not up
    # yet. Only shot-landing changes; it is committed and pushed on its own.
    Write-Host "`n==> Updating the landing page release facts..." -ForegroundColor Cyan
    $pages = Update-LandingFacts -Version $Version -ReleaseDate $releaseDate -ExePath $exe
    if ($pages.Count) {
        git add -- shot-landing
        git commit -m "Landing: $Tag release facts" --quiet -- shot-landing
        if ($LASTEXITCODE -ne 0) { throw "Landing page commit failed. The release is published; commit shot-landing by hand." }
        git push origin $defaultBranch --quiet
        if ($LASTEXITCODE -ne 0) { throw "Landing page push failed. The release is published; push $defaultBranch by hand." }
        Write-Host "    shot-landing updated to $Tag and pushed: $($pages -join ', ')" -ForegroundColor Green
    } else {
        Write-Host '    shot-landing already current'
    }
} finally { Pop-Location }
