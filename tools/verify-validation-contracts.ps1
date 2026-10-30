param([string]$ReportPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'VerificationTools.psm1')
Import-Module (Join-Path $PSScriptRoot 'PackageCandidateTools.psm1')
Import-Module (Join-Path $PSScriptRoot 'PackageTools.psm1')
$requiredSearchCount = (Get-PclDbCheckPolicy 'pack-search').Count
if (!$ReportPath) { $ReportPath = Join-Path $root 'artifacts/validation-contract-checks.json' }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('PCL-DB-ValidationContracts-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$passed = New-Object 'Collections.Generic.List[string]'
$packageModule = Get-Module PackageTools

function Assert-Case([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw $Name }
    $passed.Add($Name)
    Write-Output "PASS $Name"
}
function Assert-Rejected([scriptblock]$Action, [string]$Pattern, [string]$Name) {
    $reason = ''
    try { & $Action | Out-Null } catch { $reason = $_.Exception.Message }
    if (!$reason -or $reason -notmatch $Pattern) { throw "$Name -- expected /$Pattern/, got: $reason" }
    Assert-Case $true $Name
}
function Copy-Object([object]$Value) { $Value | ConvertTo-Json -Depth 15 | ConvertFrom-Json }
function Hash-Bytes([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '') } finally { $sha.Dispose() }
}
function New-Receipt([string]$Scope, [string]$Hash, [bool]$Animations=$true) {
    $count = (Get-PclDbCheckPolicy $Scope).Count
    [pscustomobject]@{Scope=$Scope; Succeeded=$true; Count=$count; Passed=@(1..$count | ForEach-Object { "Controlled $Scope assertion $_" })
        ExecutableSHA256=$Hash; Standalone=$true; ClientAreaAnimation=$Animations; FailedCheck=$null; Exception=$null}
}
function Write-FixtureZip([string]$Path, [object]$Manifest, [scriptblock]$Edit, [byte[]]$ManifestBytes) {
    # Tiny parser fixtures only: the EXE payload is text, never executable or a
    # candidate. This script never calls New-PclDbPackage / verify-single-exe.
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $payload.Keys) {
            $stream = $zip.CreateEntry('PclDownloadBridge/' + $name).Open()
            try { $stream.Write($payload[$name], 0, $payload[$name].Length) } finally { $stream.Dispose() }
        }
        $bytes = if ($null -ne $ManifestBytes) { $ManifestBytes } else { [Text.Encoding]::UTF8.GetBytes(($Manifest | ConvertTo-Json -Depth 15)) }
        $stream = $zip.CreateEntry('PclDownloadBridge/build-manifest.json').Open()
        try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    } finally { $zip.Dispose() }
    if ($Edit) {
        $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Update)
        try { & $Edit $zip } finally { $zip.Dispose() }
    }
}
function Assert-Manifest([object]$Manifest) { & $packageModule { param($value) Assert-BuildManifest $value } $Manifest }
function Assert-BoundManifest([object]$Manifest, [object]$Candidate) { & $packageModule { param($value,$candidate) Assert-ManifestCandidate $value $candidate } $Manifest $Candidate }

try {
    $payload = [ordered]@{}
    foreach ($name in @('PCL-DB.exe','LICENCE','README.md','SOURCE_REFERENCES.md','THIRD_PARTY_NOTICES.md','VALIDATION.md','PATCH_MECHANISMS.md','VS2026_DEBUG.md')) {
        $payload[$name] = [Text.Encoding]::UTF8.GetBytes("Parser fixture only, not a distribution: $name")
    }
    $hash = Hash-Bytes $payload['PCL-DB.exe']
    $receipts = @('standalone','all','motion','pack-search' | ForEach-Object { New-Receipt $_ $hash })
    foreach ($receipt in $receipts) {
        Assert-PclDbCheckReport $receipt $receipt.Scope -ExecutableSHA256 $hash -RequireStandalone
        Assert-Case $true ("current receipt accepts its complete suite: " + $receipt.Scope)
    }
    $reduced = New-Receipt 'pack-search' $hash $false
    Assert-PclDbCheckReport $reduced 'pack-search' -ExecutableSHA256 $hash -RequireStandalone
    Assert-Case ($reduced.Count -eq $receipts[3].Count) 'Windows animations on and off require the same complete search suite'
    foreach ($count in (@(146,170,171,($requiredSearchCount - 1)) | Select-Object -Unique)) {
        $bad = Copy-Object $reduced; $bad.Count = $count; $bad.Passed = @($bad.Passed | Select-Object -First $count)
        Assert-Rejected { Assert-PclDbCheckReport $bad 'pack-search' } 'check count' ("truncated search suite cannot pass: $count")
    }
    $mutations = @(
        @{Name='wrong scope'; Edit={param($r) $r.Scope='motion'}; Error='check report'},
        @{Name='array scope'; Edit={param($r) $r.Scope=@('pack-search')}; Error='check report'},
        @{Name='failed status'; Edit={param($r) $r.Succeeded=$false}; Error='check report'},
        @{Name='string success'; Edit={param($r) $r.Succeeded='true'}; Error='check report'},
        @{Name='missing success'; Edit={param($r) $r.PSObject.Properties.Remove('Succeeded')}; Error='missing Succeeded'},
        @{Name='string count'; Edit={param($r) $r.Count='171'}; Error='check report'},
        @{Name='zero count'; Edit={param($r) $r.Count=0}; Error='check report'},
        @{Name='fractional count'; Edit={param($r) $r.Count=171.5}; Error='check report'},
        @{Name='short passed list'; Edit={param($r) $r.Passed=@($r.Passed | Select-Object -Skip 1)}; Error='check report'},
        @{Name='scalar passed'; Edit={param($r) $r.Passed='one'}; Error='check report'},
        @{Name='empty assertion'; Edit={param($r) $r.Passed[0]=' '}; Error='passed assertion'},
        @{Name='object assertion'; Edit={param($r) $r.Passed[0]=[pscustomobject]@{Success=$true}}; Error='passed assertion'},
        @{Name='failed check'; Edit={param($r) $r.FailedCheck='failure'}; Error='Failed'},
        @{Name='exception'; Edit={param($r) $r.Exception='failure'}; Error='Failed'},
        @{Name='wrong executable'; Edit={param($r) $r.ExecutableSHA256=('0'*64)}; Error='different executable'},
        @{Name='missing executable'; Edit={param($r) $r.PSObject.Properties.Remove('ExecutableSHA256')}; Error='missing ExecutableSHA256'},
        @{Name='ordinary multi-file build'; Edit={param($r) $r.Standalone=$false}; Error='single EXE'},
        @{Name='string standalone'; Edit={param($r) $r.Standalone='true'}; Error='check report'},
        @{Name='missing animation branch'; Edit={param($r) $r.PSObject.Properties.Remove('ClientAreaAnimation')}; Error='missing ClientAreaAnimation'}
    )
    foreach ($mutation in $mutations) {
        $bad = Copy-Object $receipts[3]; & $mutation.Edit $bad
        Assert-Rejected { Assert-PclDbCheckReport $bad 'pack-search' -ExecutableSHA256 $hash -RequireStandalone } $mutation.Error ("receipt rejects " + $mutation.Name)
    }
    $verification = New-PclDbVerification $receipts $hash
    Assert-PclDbVerification $verification $hash -Current
    Assert-Case ($verification.Reports.Count -eq 4 -and $verification.PackSearchChecks -eq $requiredSearchCount) 'portable evidence retains all four scopes and complete assertion lists'
    $disabledEvidence = New-PclDbVerification @($receipts[0],$receipts[1],$receipts[2],$reduced) $hash
    Assert-PclDbVerification $disabledEvidence $hash -Current
    Assert-Case (!$disabledEvidence.Reports[3].ClientAreaAnimation) 'portable search evidence retains the observed Windows animation branch'
    Assert-Rejected { New-PclDbVerification @($receipts[0],$receipts[1],$receipts[2]) $hash } 'verification scope' 'aggregate builder refuses missing search evidence'
    Assert-Rejected { New-PclDbVerification @($receipts[0],$receipts[1],$receipts[2],$receipts[2]) $hash } 'verification scope' 'aggregate builder refuses duplicate scopes'
    $bad = Copy-Object $verification; $bad.Reports=@($bad.Reports | Select-Object -First 3)
    Assert-Rejected { Assert-PclDbVerification $bad $hash -Current } 'verification evidence' 'aggregate parser refuses missing search evidence'
    $bad = Copy-Object $verification; $bad.Reports[3]=$bad.Reports[2]
    Assert-Rejected { Assert-PclDbVerification $bad $hash -Current } 'verification scope' 'aggregate parser refuses duplicate scopes'
    $bad = Copy-Object $verification; $bad.PackSearchChecks=170
    Assert-Rejected { Assert-PclDbVerification $bad $hash -Current } 'total differs' 'aggregate totals must match complete receipts'
    $bad = Copy-Object $verification; $bad.Format=@($bad.Format)
    Assert-Rejected { Assert-PclDbVerification $bad $hash -Current } 'verification evidence' 'aggregate format cannot be a JSON array'
    $historicEvidence = Copy-Object $verification; $historicEvidence.Policy='PCL-DB-checks/older-fixture'; $historicEvidence.Reports[3].Count=170
    $historicEvidence.Reports[3].Passed=@($historicEvidence.Reports[3].Passed | Select-Object -First 170); $historicEvidence.PackSearchChecks=170
    Assert-PclDbVerification $historicEvidence $hash
    Assert-Case $true 'archive evidence validates its own structure without imposing current counts'
    Assert-Rejected { Assert-PclDbVerification $historicEvidence $hash -Current } 'not current' 'historical policy cannot pass current candidate acceptance'
    $bad = Copy-Object $historicEvidence; $bad.Policy=$verification.Policy
    Assert-Rejected { Assert-PclDbVerification $bad $hash -Current } 'check count' 'renaming an old policy does not promote its incomplete suite'

    $candidate = [pscustomobject]@{Format='PCL-DB-candidate/v4';CandidateSHA256=$hash;Verification=$verification
        StandaloneChecks=$verification.StandaloneChecks;UiChecks=$verification.UiChecks;MotionChecks=$verification.MotionChecks;PackSearchChecks=$verification.PackSearchChecks;Version='0.0.0';ReleaseVersion='0.0.0-alpha.1'
        Source=[pscustomobject]@{Commit=('1'*40);Branch='fixture';Dirty=$false;InventorySHA256=('2'*64)};DotnetSDK='fixture'
        PayloadFiles=@($payload.Keys | ForEach-Object { [pscustomobject]@{Name=$_;Length=$payload[$_].Length;SHA256=(Hash-Bytes $payload[$_])} })
        EmbeddedAssemblies=@([pscustomobject]@{Name='PclDownloadBridge.Core';SHA256=('3'*64)},[pscustomobject]@{Name='Newtonsoft.Json';SHA256=('4'*64)},[pscustomobject]@{Name='PclDownloadBridge.Presentation';SHA256=('5'*64)})}
    Assert-PclDbCandidateVerification $candidate
    Assert-Case $true 'candidate evidence parser accepts a complete controlled receipt set (not an executed candidate)'
    foreach ($mode in @('legacy','old-v3','array-format','missing-search-total','wrong-search-total','missing-search-receipt','ordinary-exe','failed-search')) {
        $bad = Copy-Object $candidate
        switch ($mode) {
            'legacy' {$bad.PSObject.Properties.Remove('Format')}
            'old-v3' {$bad.Format='PCL-DB-candidate/v3'}
            'array-format' {$bad.Format=@($bad.Format)}
            'missing-search-total' {$bad.PSObject.Properties.Remove('PackSearchChecks')}
            'wrong-search-total' {$bad.PackSearchChecks=170}
            'missing-search-receipt' {$bad.Verification.Reports=@($bad.Verification.Reports | Select-Object -First 3)}
            'ordinary-exe' {$bad.Verification.Reports[3].Standalone=$false}
            'failed-search' {$bad.Verification.Reports[3].Succeeded=$false}
        }
        $path=Join-Path $fixture ($mode+'.json'); $bad | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $path -Encoding UTF8
        Assert-Rejected { Test-PclDbCandidate -ProjectRoot $root -CandidateResultPath $path } 'candidate|verification evidence|single EXE|check report' ("real candidate entry rejects before payload or source access: " + $mode)
    }

    $manifest = & $packageModule {param($c) New-PclDbBuildManifest $c} $candidate
    Assert-Manifest $manifest
    foreach ($mode in @('duplicate','unknown','array-name','array-hash','invalid-hash','missing','legacy-two','extra','missing-presentation','null')) {
        $bad = Copy-Object $candidate
        switch ($mode) {
            'duplicate' {$bad.EmbeddedAssemblies[1]=$bad.EmbeddedAssemblies[0]}
            'unknown' {$bad.EmbeddedAssemblies[0].Name='Unexpected.Core'}
            'array-name' {$bad.EmbeddedAssemblies[0].Name=@($bad.EmbeddedAssemblies[0].Name)}
            'array-hash' {$bad.EmbeddedAssemblies[0].SHA256=@($bad.EmbeddedAssemblies[0].SHA256)}
            'invalid-hash' {$bad.EmbeddedAssemblies[0].SHA256='invalid'}
            'missing' {$bad.EmbeddedAssemblies=@($bad.EmbeddedAssemblies[0])}
            'legacy-two' {$bad.EmbeddedAssemblies=@($bad.EmbeddedAssemblies | Select-Object -First 2)}
            'extra' {$bad.EmbeddedAssemblies+=@([pscustomobject]@{Name='Extra.Assembly';SHA256=('6'*64)})}
            'missing-presentation' {$bad.EmbeddedAssemblies[2].Name='PclDownloadBridge.Other'}
            'null' {$bad.EmbeddedAssemblies=$null}
        }
        Assert-Rejected { & $packageModule {param($c) New-PclDbBuildManifest $c} $bad } 'embedded dependency inventory' ("manifest builder rejects malformed dependency inventory: " + $mode)
        $path=Join-Path $fixture ('dependencies-'+$mode+'.json'); $bad | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $path -Encoding UTF8
        Assert-Rejected { Test-PclDbCandidate -ProjectRoot $root -CandidateResultPath $path } 'embedded dependency inventory' ("candidate rejects malformed dependencies before source or payload access: " + $mode)
        $badManifest=Copy-Object $manifest; $badManifest.EmbeddedAssemblies=$bad.EmbeddedAssemblies
        Assert-Rejected { Assert-Manifest $badManifest } 'embedded dependency inventory' ("archive parser rejects malformed dependency inventory: " + $mode)
    }
    Assert-BoundManifest $manifest $candidate
    Assert-Case ($manifest.Format -ceq 'PCL-DB-single-exe/v4' -and $manifest.Verification.PackSearchChecks -eq $requiredSearchCount -and $manifest.EmbeddedAssemblies.Count -eq 3) 'production v4 manifest binds complete search evidence and the three-dependency model'
    $bad=Copy-Object $manifest; $bad.Format=@($bad.Format)
    Assert-Rejected { Assert-Manifest $bad } 'Invalid build manifest' 'archive format cannot be a JSON array'
    $archive = Join-Path $fixture 'v4-fixture.zip'; Write-FixtureZip $archive $manifest
    $report = Test-PclDbPackage -ProjectRoot $root -PackagePath $archive
    Assert-Case ($report.Succeeded -and !$report.CandidateVerified -and $report.VerificationLevel -ceq 'ArchiveIntegrity') 'v4 archive-only check makes no current candidate or execution claim'
    $historicV3 = Copy-Object $manifest; $historicV3.Format='PCL-DB-single-exe/v3'; $historicV3.EmbeddedAssemblies=@($historicV3.EmbeddedAssemblies | Select-Object -First 2); $historicV3.Verification=$historicEvidence
    Assert-Manifest $historicV3
    $archive = Join-Path $fixture 'v3-fixture.zip'; Write-FixtureZip $archive $historicV3
    $report = Test-PclDbPackage -ProjectRoot $root -PackagePath $archive
    Assert-Case ($report.Succeeded -and !$report.CandidateVerified -and $report.ManifestFormat -ceq 'PCL-DB-single-exe/v3') 'historical v3 archives retain their exact two-dependency model'
    Assert-Rejected { Assert-BoundManifest $historicV3 $candidate } 'historical manifest' 'a historical v3 archive cannot satisfy current candidate verification'
    foreach ($format in @('v2','v3')) {
        $bad=Copy-Object $manifest; $bad.Format='PCL-DB-single-exe/'+$format
        Assert-Rejected { Assert-Manifest $bad } 'embedded dependency inventory' ("a three-dependency manifest cannot be relabelled as historical " + $format)
    }
    $bad=Copy-Object $historicV3; $bad.Format='PCL-DB-single-exe/v4'
    Assert-Rejected { Assert-Manifest $bad } 'embedded dependency inventory' 'relabeling a two-dependency archive cannot promote it to v4'
    $legacy = Copy-Object $historicV3; $legacy.Format='PCL-DB-single-exe/v2'; $legacy.Verification=[pscustomobject]@{StandaloneChecks=25;UiChecks=658;MotionChecks=328}
    $archive = Join-Path $fixture 'v2-fixture.zip'; Write-FixtureZip $archive $legacy
    $report = Test-PclDbPackage -ProjectRoot $root -PackagePath $archive
    Assert-Case ($report.Succeeded -and !$report.CandidateVerified -and $report.ManifestFormat -ceq 'PCL-DB-single-exe/v2') 'historical Beta 658/328 totals remain valid for archive integrity'
    Assert-Rejected { Assert-BoundManifest $legacy $candidate } 'historical manifest' 'a v2 archive cannot masquerade as the current candidate'
    foreach ($value in @(0,-1,'328',328.5,$null)) {
        $bad=Copy-Object $legacy; $bad.Verification.MotionChecks=$value
        Assert-Rejected { Assert-Manifest $bad } 'Invalid build manifest' ("v2 rejects invalid historical count: " + $value)
    }
    $bad=Copy-Object $legacy; $bad.Verification.PSObject.Properties.Remove('UiChecks')
    Assert-Rejected { Assert-Manifest $bad } 'Invalid build manifest' 'v2 still requires all declared suite totals'
    $bad=Copy-Object $manifest; $bad.Verification=$historicEvidence
    Assert-Manifest $bad
    Assert-Case $true 'a structurally complete older v4 policy remains inspectable as an archive'
    Assert-Rejected { Assert-BoundManifest $bad $candidate } 'not current' 'archived v4 evidence does not satisfy current candidate policy'
    foreach ($mode in @('missing-search','failed-search','wrong-scope','short-passed','wrong-exe','wrong-total')) {
        $bad=Copy-Object $manifest
        switch ($mode) {
            'missing-search' {$bad.Verification.Reports=@($bad.Verification.Reports | Select-Object -First 3)}
            'failed-search' {$bad.Verification.Reports[3].Succeeded=$false}
            'wrong-scope' {$bad.Verification.Reports[3].Scope='all'}
            'short-passed' {$bad.Verification.Reports[3].Passed=@('incomplete')}
            'wrong-exe' {$bad.Verification.Reports[3].ExecutableSHA256=('0'*64)}
            'wrong-total' {$bad.Verification.PackSearchChecks=170}
        }
        $path=Join-Path $fixture ($mode+'.zip'); Write-FixtureZip $path $bad
        Assert-Rejected { Test-PclDbPackage -ProjectRoot $root -PackagePath $path } 'Invalid build manifest' ("archive entry rejects malformed search evidence: " + $mode)
    }
    $bad=Copy-Object $manifest; $bad.Verification.Reports[3].Passed[0]='different controlled assertion'
    Assert-Rejected { Assert-BoundManifest $bad $candidate } 'selected candidate verification' 'candidate binding compares assertion evidence, not just totals'
    $bad=Copy-Object $manifest; $bad.Build.SourceCommit=('0'*40)
    Assert-Rejected { Assert-BoundManifest $bad $candidate } 'selected candidate source' 'current evidence does not waive source commit binding'
    $bad=Copy-Object $manifest; $bad.Files[0].SHA256=('0'*64)
    Assert-Rejected { Assert-BoundManifest $bad $candidate } 'selected candidate bytes' 'current evidence does not waive exact payload binding'
    $bad=Copy-Object $manifest; $bad.EmbeddedAssemblies[0].SHA256=('0'*64)
    Assert-Rejected { Assert-BoundManifest $bad $candidate } 'selected embedded dependencies' 'current evidence does not waive embedded dependency binding'
    foreach ($format in @('v2','v3','v4')) {
        $source=if($format -eq 'v2'){$legacy}elseif($format -eq 'v3'){$historicV3}else{$manifest}
        $path=Join-Path $fixture ($format+'-corrupt.zip')
        Write-FixtureZip $path $source {param($zip)
            $name='PclDownloadBridge/README.md'; $zip.GetEntry($name).Delete(); $bytes=[byte[]]$payload['README.md'].Clone(); $bytes[0]=$bytes[0] -bxor 1
            $stream=$zip.CreateEntry($name).Open(); try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
        }
        Assert-Rejected { Test-PclDbPackage -ProjectRoot $root -PackagePath $path } 'differs from its manifest' ("$format still rejects same-length payload corruption")
    }
    foreach ($case in @(
        @{Name='extra';Error='exactly 9';Edit={param($zip) $null=$zip.CreateEntry('PclDownloadBridge/extra')}},
        @{Name='missing';Error='exactly 9';Edit={param($zip) $zip.GetEntry('PclDownloadBridge/LICENCE').Delete()}},
        @{Name='duplicate';Error='duplicate';Edit={param($zip) $zip.GetEntry('PclDownloadBridge/LICENCE').Delete();$null=$zip.CreateEntry('PclDownloadBridge/README.md')}},
        @{Name='traversal';Error='Unexpected';Edit={param($zip) $zip.GetEntry('PclDownloadBridge/LICENCE').Delete();$null=$zip.CreateEntry('PclDownloadBridge/../LICENCE')}}
    )) {
        $path=Join-Path $fixture ($case.Name+'.zip'); Write-FixtureZip $path $legacy $case.Edit
        Assert-Rejected { Test-PclDbPackage -ProjectRoot $root -PackagePath $path } $case.Error ("historical archive rejects " + $case.Name + ' entries')
    }

    # Beta 8's full evidence outgrew the legacy 1 MiB boundary. Keep the
    # new 4 MiB boundary explicit here, independent of the implementation.
    # These remain synthetic parser fixtures, never app acceptance.
    $manifestLimit = 4194304
    $compactTarget = $manifestLimit - 8576
    $large = Copy-Object $manifest
    $large.Verification.Reports[0].Passed[0] += " Unicode label: $([char]0x6D4B)$([char]0x8BD5) `"quoted`"`nnext line "
    $encoding = [Text.UTF8Encoding]::new($false)
    $compact = $large | ConvertTo-Json -Depth 12 -Compress
    $padding = $compactTarget - $encoding.GetByteCount($compact)
    if ($padding -le 0) { throw 'Large manifest fixture needs a larger bounded receipt target.' }
    $large.Verification.Reports[0].Passed[0] += 'x' * $padding
    $compact = $large | ConvertTo-Json -Depth 12 -Compress
    Assert-Case ($encoding.GetByteCount($compact) -eq $compactTarget -and $encoding.GetByteCount($compact) -gt 1048576 -and $encoding.GetByteCount(($large | ConvertTo-Json -Depth 12)) -gt $manifestLimit) 'complete evidence exceeds the legacy limit and fits the bounded archive only with compact formatting'
    $serialized = Join-Path $fixture 'complete-manifest.json'
    & $packageModule { param($value,$path) Write-PclDbBuildManifest $value $path } $large $serialized
    $serializedBytes = [IO.File]::ReadAllBytes($serialized)
    Assert-Case ($serializedBytes.Length -eq $encoding.GetByteCount($compact)) 'production manifest writer emits compact UTF-8 without BOM or trailing whitespace'
    $roundTrip = [IO.File]::ReadAllText($serialized, $encoding) | ConvertFrom-Json
    Assert-Case (($roundTrip | ConvertTo-Json -Depth 12 -Compress) -ceq $compact) 'manifest round trip preserves every assertion, Unicode, quotes and nested verification field'
    $bound = Copy-Object $candidate; $bound.Verification = $large.Verification
    Assert-BoundManifest $roundTrip $bound
    Assert-Case $true 'compact manifest retains exact source, payload, dependency and complete assertion binding'
    $path = Join-Path $fixture 'complete-compact.zip'
    Write-FixtureZip $path $large $null $serializedBytes
    $compactReport = Test-PclDbPackage -ProjectRoot $root -PackagePath $path -ManifestPath $serialized
    Assert-Case ($compactReport.Succeeded -and !$compactReport.CandidateVerified -and $compactReport.FileCount -eq 9) 'default production serialization passes full archive integrity without a current candidate claim'

    $boundary = Copy-Object $large
    $boundary.Verification.Reports[0].Passed[0] += 'x' * 8576
    $atLimit = Join-Path $fixture 'boundary-manifest.json'
    & $packageModule { param($value,$path) Write-PclDbBuildManifest $value $path } $boundary $atLimit
    Assert-Case ((Get-Item -LiteralPath $atLimit).Length -eq $manifestLimit) 'writer accepts a manifest exactly at the declared 4 MiB byte limit'
    $path = Join-Path $fixture 'boundary.zip'
    Write-FixtureZip $path $boundary $null ([IO.File]::ReadAllBytes($atLimit))
    $null = Test-PclDbPackage -ProjectRoot $root -PackagePath $path
    Assert-Case $true 'archive reader accepts the same exact byte boundary as the writer'

    # A multibyte replacement keeps character count unchanged but exceeds the
    # byte limit. Reject before touching either an old file or a new destination.
    $oversized = Copy-Object $boundary
    $label = $oversized.Verification.Reports[0].Passed[0]
    $oversized.Verification.Reports[0].Passed[0] = $label.Substring(0, $label.Length - 1) + [char]0x6D4B
    $oversizedJson = $oversized | ConvertTo-Json -Depth 12 -Compress
    Assert-Case ($oversizedJson.Length -lt $manifestLimit -and $encoding.GetByteCount($oversizedJson) -eq ($manifestLimit + 2)) 'size enforcement uses UTF-8 bytes rather than JSON character count'
    $oldHash = (Get-FileHash -LiteralPath $atLimit -Algorithm SHA256).Hash
    Assert-Rejected { & $packageModule { param($value,$path) Write-PclDbBuildManifest $value $path } $oversized $atLimit } 'Oversized build manifest' 'oversized compact receipts fail before overwriting an existing manifest'
    Assert-Case ((Get-FileHash -LiteralPath $atLimit -Algorithm SHA256).Hash -ceq $oldHash) 'rejected serialization preserves the prior manifest byte for byte'
    $missing = Join-Path $fixture 'oversized-new.json'
    Assert-Rejected { & $packageModule { param($value,$path) Write-PclDbBuildManifest $value $path } $oversized $missing } 'Oversized build manifest' 'oversized compact receipts fail before creating a new manifest'
    Assert-Case (!(Test-Path -LiteralPath $missing)) 'oversized serialization leaves no partial new manifest'
    $path = Join-Path $fixture 'oversized.zip'
    Write-FixtureZip $path $oversized $null ($encoding.GetBytes($oversizedJson))
    Assert-Rejected { Test-PclDbPackage -ProjectRoot $root -PackagePath $path } 'Oversized build manifest' 'archive byte limit remains enforced for genuinely oversized compact receipts'
    $result=[pscustomobject]@{Succeeded=$true;Scope='validation-contracts';Count=$passed.Count;Passed=@($passed);Fixture=$fixture
        RealCandidateExecuted=$false;DistributionUpdated=$false;Evidence='Controlled receipts and tiny non-executable parser ZIP fixtures only.'}
} catch {
    $result=[pscustomobject]@{Succeeded=$false;Scope='validation-contracts';Count=$passed.Count;Passed=@($passed);Fixture=$fixture;Error=$_.Exception.ToString()}
    throw
} finally {
    New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($ReportPath)) -Parent) -Force | Out-Null
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}
