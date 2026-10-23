#Requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Folder)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ReleaseCandidate.ps1')
if (Test-Path -LiteralPath $Folder) { throw 'Use a new scratch folder.' }
[void][IO.Directory]::CreateDirectory($Folder)
$report = New-Object 'Collections.Generic.List[string]'
$utf8 = New-Object Text.UTF8Encoding($false)
$request = New-Object Security.Cryptography.X509Certificates.CertificateRequest('CN=Open Source Developer Stephen Riley',
    ([Security.Cryptography.RSA]::Create(2048)), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1), [DateTimeOffset]::UtcNow.AddDays(1))
$signature = [pscustomobject]@{ Status='Valid'; SignerCertificate=$certificate; TimeStamperCertificate=$certificate }
$readSignature = { param($path) $signature }.GetNewClosure()
$commit = 'a' * 40
$native = Join-Path $Folder 'native-input.dll'
[IO.File]::WriteAllText($native, 'synthetic native bytes', $utf8)
$evidence = Join-Path $Folder 'evidence.txt'
[IO.File]::WriteAllText($evidence, 'Synthetic acceptance evidence for inert boundary tests only.', $utf8)
try {
    foreach ($scenario in @('unchanged','executable','native','source-archive','commit','manifest','acceptance','evidence','development','write-during-publication')) {
        $candidate = Join-Path $Folder $scenario
        [void][IO.Directory]::CreateDirectory($candidate)
        foreach ($name in @('KillerShot.exe','e_sqlcipher.dll','KillerShot-1.0.0-src.zip','LICENSE.txt','LICENSE-SQLCipher.txt','LICENSE-LibTomCrypt.txt','SHA256SUMS.txt','release-notes.md')) {
            $value = if ($name -eq 'e_sqlcipher.dll') { 'synthetic native bytes' } else { 'synthetic ' + $name }
            [IO.File]::WriteAllText((Join-Path $candidate $name), $value, $utf8)
        }
        $manifest = New-ReleaseCandidate -Directory $candidate -SourceCommit $commit -Version '1.0.0' -DevelopmentOnly:($scenario -eq 'development') -ReadSignature $readSignature
        if ($scenario -eq 'development') {
            $refused = $false
            try { $null = New-ReleaseAcceptance -Manifest $manifest -ValidationRecord $evidence -ExpectedCommit $commit -NativeInput $native -ReadSignature $readSignature }
            catch { $refused = $true }
            if (!$refused) { throw 'Development-only candidate was accepted.' }
            $report.Add('development-only candidate cannot be accepted PASS')
            continue
        }
        $receipt = New-ReleaseAcceptance -Manifest $manifest -ValidationRecord $evidence -ExpectedCommit $commit -NativeInput $native -ReadSignature $readSignature
        $acceptanceHash = (Get-FileHash -LiteralPath $receipt -Algorithm SHA256).Hash
        $expectedCommit = $commit
        switch ($scenario) {
            'executable' { [IO.File]::AppendAllText((Join-Path $candidate 'KillerShot.exe'), 'changed') }
            'native' { [IO.File]::AppendAllText($native, 'changed') }
            'source-archive' { [IO.File]::AppendAllText((Join-Path $candidate 'KillerShot-1.0.0-src.zip'), 'changed') }
            'commit' { $expectedCommit = 'b' * 40 }
            'manifest' { [IO.File]::AppendAllText($manifest, ' ') }
            'acceptance' { [IO.File]::AppendAllText($receipt, ' ') }
            'evidence' { [IO.File]::AppendAllText((Join-Path $candidate 'validation-record'), 'changed') }
        }
        $state = [pscustomobject]@{ Steps=(New-Object 'Collections.Generic.List[string]'); Locked=$false }
        $getCommit = { $expectedCommit }.GetNewClosure()
        $publish = {
            param($step, $context)
            $state.Steps.Add($step)
            if ($scenario -eq 'write-during-publication') {
                try { [IO.File]::AppendAllText((Join-Path $candidate 'KillerShot.exe'), 'changed') }
                catch [IO.IOException] { $state.Locked=$true }
                if (!$state.Locked) { throw 'Candidate bytes could change during publication.' }
            }
        }.GetNewClosure()
        $failure = $null
        try { Invoke-ReleasePromotion -Manifest $manifest -Acceptance $receipt -AcceptanceSha256 $acceptanceHash -NativeInput $native -GetSourceCommit $getCommit -PublishStep $publish -ReadSignature $readSignature }
        catch { $failure=$_.Exception.Message }
        $valid = $scenario -in @('unchanged','write-during-publication')
        if ($valid -and ($failure -or ($state.Steps -join ',') -ne 'tag,push,release')) { throw "Valid promotion failed: $failure" }
        if (!$valid -and (!$failure -or $state.Steps.Count -ne 0)) { throw "Tampered $scenario reached publication." }
        $report.Add("$scenario immutable candidate promotion PASS")
        if ($scenario -eq 'native') { [IO.File]::WriteAllText($native, 'synthetic native bytes', $utf8) }
    }
    foreach ($status in @('NotSigned','HashMismatch','NotTrusted')) {
        $bad = { param($path) [pscustomobject]@{Status=$status;SignerCertificate=$certificate;TimeStamperCertificate=$certificate} }.GetNewClosure()
        $refused=$false
        try { $null=Assert-ReleaseSignature -Path $native -ReadSignature $bad }
        catch { $refused=$true }
        if (!$refused) { throw "Signature status $status was accepted." }
        $report.Add("signature $status refused PASS")
    }
    $refused=$false
    try { $null=Assert-ReleaseSignature -Path $native -CertName 'Wrong publisher' -ReadSignature $readSignature }
    catch { $refused=$true }
    if (!$refused) { throw 'Unexpected publisher was accepted.' }
    $signature.TimeStamperCertificate=$null
    $refused=$false
    try { $null=Assert-ReleaseSignature -Path $native -ReadSignature $readSignature }
    catch { $refused=$true }
    if (!$refused) { throw 'Missing timestamp was accepted.' }
    $report.Add('wrong publisher and absent timestamp refused PASS')
    $signature.TimeStamperCertificate=$certificate
    $fixture = Join-Path $Folder 'script-integration'
    [void][IO.Directory]::CreateDirectory((Join-Path $fixture 'scripts'))
    [void][IO.Directory]::CreateDirectory((Join-Path $fixture 'third_party\sqlcipher'))
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'release.ps1') -Destination $fixture
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ReleaseCandidate.ps1') -Destination (Join-Path $fixture 'scripts\ReleaseCandidate.ps1')
    Copy-Item -LiteralPath $native -Destination (Join-Path $fixture 'third_party\sqlcipher\e_sqlcipher.dll')
    $integration = [pscustomobject]@{ Builds=0; Tags=0; Pushes=0; Releases=0 }
    function Get-AuthenticodeSignature { param($LiteralPath) return $signature }
    function dotnet { $integration.Builds++; throw 'Promotion attempted a rebuild.' }
    function git {
        $global:LASTEXITCODE=0
        if ($args[0] -eq 'ls-remote' -and $args[1] -eq '--heads') { return $commit + "`trefs/heads/main" }
        if ($args[0] -eq 'symbolic-ref') { return 'refs/remotes/origin/main' }
        if ($args[0] -eq 'rev-parse') {
            if ($args -contains '--abbrev-ref') { return 'main' }
            return $commit
        }
        if ($args[0] -eq 'tag' -and $args[1] -eq '-a') { $integration.Tags++ }
        if ($args[0] -eq 'push') { $integration.Pushes++ }
    }
    function gh {
        $global:LASTEXITCODE=0
        if ($args[0] -eq 'api') { return 'SteveTheKiller' }
        if ($args[0] -eq 'release' -and $args[1] -eq 'create') { $integration.Releases++ }
        if ($args[0] -eq 'release' -and $args[1] -eq 'view') { return 'inert-release-result' }
    }
    $accepted = Join-Path $Folder 'unchanged'
    $receipt = Join-Path $accepted 'acceptance.json'
    $null = & (Join-Path $fixture 'release.ps1') -PublishCandidate (Join-Path $accepted 'candidate.json') -Acceptance $receipt -AcceptanceSha256 (Get-FileHash -LiteralPath $receipt -Algorithm SHA256).Hash
    if ($integration.Builds -ne 0 -or $integration.Tags -ne 1 -or $integration.Pushes -ne 1 -or $integration.Releases -ne 1) {
        throw 'Real release script did not promote through the inert publication boundary without rebuilding.'
    }
    $report.Add('real release script promotes accepted bytes with zero build/sign calls through inert Git/GitHub boundaries PASS')
} finally {
    $certificate.Dispose()
    [IO.File]::WriteAllLines((Join-Path $Folder 'release-candidate-report.txt'), $report, $utf8)
}
$report
