#Requires -Version 5.1
# Pure candidate controls. Loading this file performs no build, signing, Git, or publication.

function Assert-ReleaseSignature {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [string]$CertName = 'Open Source Developer Stephen Riley',
        [string]$CertThumbprint = '',
        [string]$Label = 'Artifact',
        [scriptblock]$ReadSignature = { param($file) Get-AuthenticodeSignature -LiteralPath $file }
    )
    $signature = & $ReadSignature $Path
    if ($null -eq $signature -or [string]$signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
        throw "$Label signature is not valid: $Path"
    }
    if ($null -eq $signature.TimeStamperCertificate) { throw "$Label signature has no trusted timestamp: $Path" }
    $certificate = $signature.SignerCertificate
    $name = $certificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if ($CertThumbprint) {
        if ($CertThumbprint -notmatch '^[a-fA-F0-9]{40}$' -or $certificate.Thumbprint -ne $CertThumbprint.ToUpperInvariant()) {
            throw "$Label signature has an unexpected signing certificate."
        }
    } elseif ($name -ne $CertName) { throw "$Label signature has an unexpected publisher: $name" }
    return [pscustomobject]@{ Name=$name; Thumbprint=$certificate.Thumbprint; TimestampThumbprint=$signature.TimeStamperCertificate.Thumbprint }
}

function Write-CandidateJson {
    param([string]$Path, [object]$Value)
    $json = $Value | ConvertTo-Json -Depth 12
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json)
    $file = New-Object IO.FileStream($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $file.Write($bytes, 0, $bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
}

function Resolve-CandidateFile {
    param([string]$Directory, [string]$Name)
    if (!$Name -or $Name -ne [IO.Path]::GetFileName($Name) -or $Name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $Name -in @('.', '..')) {
        throw 'Candidate contains an invalid artifact name.'
    }
    $root = Get-Item -LiteralPath $Directory -Force
    if (!$root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Candidate directory must be an ordinary directory.' }
    $path = Join-Path $root.FullName $Name
    $file = Get-Item -LiteralPath $path -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Candidate artifacts must be ordinary files.' }
    return $file.FullName
}

function Get-CandidateNames {
    param([string]$Version)
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid candidate version.' }
    return [ordered]@{
        Executable='KillerShot.exe'; Native='e_sqlcipher.dll'; Source="KillerShot-$Version-src.zip";
        License='LICENSE.txt'; SqlCipherLicense='LICENSE-SQLCipher.txt'; CryptoLicense='LICENSE-LibTomCrypt.txt';
        Checksums='SHA256SUMS.txt'; Notes='release-notes.md'
    }
}

function New-ReleaseCandidate {
    param([string]$Directory, [string]$SourceCommit, [string]$Version, [switch]$DevelopmentOnly,
        [string]$CertName='Open Source Developer Stephen Riley', [string]$CertThumbprint='',
        [scriptblock]$ReadSignature={param($file) Get-AuthenticodeSignature -LiteralPath $file})
    if ($SourceCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid candidate source commit.' }
    $names = Get-CandidateNames $Version
    $files = @($names.GetEnumerator() | ForEach-Object {
        $path = Resolve-CandidateFile $Directory $_.Value
        [pscustomobject]@{ Role=$_.Key; Name=$_.Value; Length=(Get-Item -LiteralPath $path).Length;
            Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;
            Publish=($_.Key -notin @('Native','Notes')) }
    })
    $signers = @()
    if (!$DevelopmentOnly) {
        foreach ($role in @('Executable','Native')) {
            $file = $files | Where-Object Role -eq $role
            $signer = Assert-ReleaseSignature -Path (Join-Path $Directory $file.Name) -CertName $CertName -CertThumbprint $CertThumbprint -Label $role -ReadSignature $ReadSignature
            $signers += [pscustomobject]@{ Role=$role; Thumbprint=$signer.Thumbprint }
        }
    }
    $manifest = Join-Path $Directory 'candidate.json'
    Write-CandidateJson $manifest ([ordered]@{ Schema=1; Version=$Version; SourceCommit=$SourceCommit;
        DevelopmentOnly=[bool]$DevelopmentOnly; CreatedUtc=[DateTime]::UtcNow.ToString('o'); Files=$files; Signers=$signers })
    return $manifest
}

function Assert-ReleaseCandidate {
    param([string]$Manifest, [string]$ExpectedCommit, [string]$NativeInput,
        [string]$CertName='Open Source Developer Stephen Riley', [string]$CertThumbprint='',
        [scriptblock]$ReadSignature={param($file) Get-AuthenticodeSignature -LiteralPath $file})
    $directory = Split-Path -Parent ([IO.Path]::GetFullPath($Manifest))
    $canonicalManifest = Resolve-CandidateFile $directory 'candidate.json'
    if ([IO.Path]::GetFullPath($Manifest) -ne $canonicalManifest) { throw 'Select the canonical candidate.json file.' }
    $manifest = $canonicalManifest
    $data = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($data.Schema -ne 1 -or $data.DevelopmentOnly -isnot [bool] -or $data.DevelopmentOnly) { throw 'Only a signed production candidate can be accepted or promoted.' }
    if ($data.SourceCommit -notmatch '^[a-fA-F0-9]{40}$' -or $data.SourceCommit -ne $ExpectedCommit) { throw 'Source commit differs from the candidate.' }
    $names = Get-CandidateNames $data.Version
    if (@($data.Files).Count -ne $names.Count -or @($data.Signers).Count -ne 2) { throw 'Candidate artifact inventory is incomplete.' }
    $paths = New-Object 'Collections.Generic.List[string]'
    foreach ($entry in $names.GetEnumerator()) {
        $records = @($data.Files | Where-Object Role -eq $entry.Key)
        if ($records.Count -ne 1) { throw 'Candidate has a missing or duplicate artifact role.' }
        $record = $records[0]
        if ($record.Name -ne $entry.Value -or $record.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
            $record.Publish -isnot [bool] -or $record.Publish -ne ($entry.Key -notin @('Native','Notes'))) { throw 'Candidate artifact metadata is invalid.' }
        $path = Resolve-CandidateFile $directory $record.Name
        if ((Get-Item -LiteralPath $path).Length -ne $record.Length -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $record.Sha256) {
            throw "Candidate artifact changed: $($record.Name)"
        }
        if ($entry.Key -in @('Executable','Native')) {
            $signer = Assert-ReleaseSignature -Path $path -CertName $CertName -CertThumbprint $CertThumbprint -Label $entry.Key -ReadSignature $ReadSignature
            $recorded = @($data.Signers | Where-Object Role -eq $entry.Key)
            if ($recorded.Count -ne 1 -or $recorded[0].Thumbprint -ne $signer.Thumbprint) { throw 'Candidate signer changed.' }
        }
        if ($record.Publish) { $paths.Add($path) }
    }
    $nativeRecord = $data.Files | Where-Object Role -eq 'Native'
    if ((Get-FileHash -LiteralPath $NativeInput -Algorithm SHA256).Hash -ne $nativeRecord.Sha256) { throw 'Native input differs from the accepted candidate.' }
    return [pscustomobject]@{ Data=$data; Directory=$directory; Manifest=$manifest; PublishPaths=$paths.ToArray() }
}

function New-ReleaseAcceptance {
    param([string]$Manifest, [string]$ValidationRecord, [string]$ExpectedCommit, [string]$NativeInput,
        [string]$CertName='Open Source Developer Stephen Riley', [string]$CertThumbprint='',
        [scriptblock]$ReadSignature={param($file) Get-AuthenticodeSignature -LiteralPath $file})
    $locks = New-Object 'Collections.Generic.List[IDisposable]'
    try {
        $locks.Add([IO.File]::Open($Manifest, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        $locks.Add([IO.File]::Open($NativeInput, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        $locks.Add([IO.File]::Open($ValidationRecord, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        $candidate = Assert-ReleaseCandidate -Manifest $Manifest -ExpectedCommit $ExpectedCommit -NativeInput $NativeInput -CertName $CertName -CertThumbprint $CertThumbprint -ReadSignature $ReadSignature
        foreach ($record in $candidate.Data.Files) {
            $locks.Add([IO.File]::Open((Resolve-CandidateFile $candidate.Directory $record.Name), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        }
        $candidate = Assert-ReleaseCandidate -Manifest $Manifest -ExpectedCommit $ExpectedCommit -NativeInput $NativeInput -CertName $CertName -CertThumbprint $CertThumbprint -ReadSignature $ReadSignature
        if ((Get-Item -LiteralPath $ValidationRecord).Length -eq 0) { throw 'A nonempty final validation record is required.' }
        $evidence = Join-Path $candidate.Directory 'validation-record'
        [IO.File]::Copy([IO.Path]::GetFullPath($ValidationRecord), $evidence, $false)
        $receipt = Join-Path $candidate.Directory 'acceptance.json'
        Write-CandidateJson $receipt ([ordered]@{ Schema=1; SourceCommit=$ExpectedCommit;
            ManifestSha256=(Get-FileHash -LiteralPath $candidate.Manifest -Algorithm SHA256).Hash;
            EvidenceSha256=(Get-FileHash -LiteralPath $evidence -Algorithm SHA256).Hash;
            AcceptedUtc=[DateTime]::UtcNow.ToString('o') })
        return $receipt
    } finally { foreach ($handle in $locks) { $handle.Dispose() } }
}

function Invoke-ReleasePromotion {
    param([string]$Manifest, [string]$Acceptance, [string]$AcceptanceSha256, [string]$NativeInput,
        [Parameter(Mandatory=$true)][scriptblock]$GetSourceCommit,
        [Parameter(Mandatory=$true)][scriptblock]$PublishStep,
        [string]$CertName='Open Source Developer Stephen Riley', [string]$CertThumbprint='',
        [scriptblock]$ReadSignature={param($file) Get-AuthenticodeSignature -LiteralPath $file})
    if ($AcceptanceSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'The independently retained acceptance SHA256 is required.' }
    $locks = New-Object 'Collections.Generic.List[IDisposable]'
    try {
        $locks.Add([IO.File]::Open($Manifest, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        $locks.Add([IO.File]::Open($Acceptance, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        $locks.Add([IO.File]::Open($NativeInput, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        $expectedCommit = [string](& $GetSourceCommit)
        $candidate = Assert-ReleaseCandidate -Manifest $Manifest -ExpectedCommit $expectedCommit -NativeInput $NativeInput -CertName $CertName -CertThumbprint $CertThumbprint -ReadSignature $ReadSignature
        foreach ($record in $candidate.Data.Files) {
            $path = Resolve-CandidateFile $candidate.Directory $record.Name
            $locks.Add([IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        }
        $evidence = Resolve-CandidateFile $candidate.Directory 'validation-record'
        $locks.Add([IO.File]::Open($evidence, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        foreach ($step in @('tag','push','release')) {
            if ((Get-FileHash -LiteralPath $Acceptance -Algorithm SHA256).Hash -ne $AcceptanceSha256) { throw 'Acceptance manifest changed.' }
            $receipt = Get-Content -LiteralPath $Acceptance -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($receipt.Schema -ne 1 -or $receipt.SourceCommit -ne $expectedCommit -or
                $receipt.ManifestSha256 -ne (Get-FileHash -LiteralPath $Manifest -Algorithm SHA256).Hash -or
                $receipt.EvidenceSha256 -ne (Get-FileHash -LiteralPath $evidence -Algorithm SHA256).Hash) { throw 'Candidate acceptance binding changed.' }
            $currentCommit = [string](& $GetSourceCommit)
            $candidate = Assert-ReleaseCandidate -Manifest $Manifest -ExpectedCommit $currentCommit -NativeInput $NativeInput -CertName $CertName -CertThumbprint $CertThumbprint -ReadSignature $ReadSignature
            & $PublishStep $step $candidate
        }
    } finally {
        foreach ($handle in $locks) { $handle.Dispose() }
    }
}
