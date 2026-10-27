Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Bump the policy ID when the required suites change. Archive integrity never
# applies today's counts to an older manifest; current candidates always do.
$script:PolicyId = 'PCL-DB-checks/2026-10-28.3'
$script:CandidateScopes = @('standalone', 'all', 'motion', 'pack-search')
$script:Counts = @{standalone=25; all=3613; motion=2998; 'pack-search'=1854; appearance=191; developer=36; cards=134; navigation=1990; compatibility=49; 'control-motion'=677}
$script:Fields = @{standalone='StandaloneChecks'; all='UiChecks'; motion='MotionChecks'; 'pack-search'='PackSearchChecks'}

function Get-PclDbCheckPolicy {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Scope)
    if (!$script:Counts.ContainsKey($Scope)) { throw "Unknown check scope: $Scope" }
    $mode = if ($Scope -ceq 'all') { '--ui-checks' } elseif ($Scope -ceq 'cards') { '--card-checks' } else { '--' + $Scope + '-checks' }
    # Motion and search include complete native navigation/lifecycle fixtures.
    # Keep a finite budget in one place for ordinary and isolated verification.
    $timeout = if ($Scope -cin @('all','motion')) { 600 } elseif ($Scope -ceq 'pack-search') { 240 } else { 180 }
    [pscustomobject]@{Policy=$script:PolicyId; Scope=$Scope; Count=$script:Counts[$Scope]; Mode=$mode; TimeoutSeconds=$timeout}
}

function Test-CheckCount([object]$Value) {
    return (($Value -is [int] -or $Value -is [long]) -and $Value -gt 0 -and $Value -le 100000)
}

function Assert-PclDbCheckReport {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Report, [Parameter(Mandatory)][string]$Scope,
          [string]$ExecutableSHA256, [switch]$RequireStandalone, [switch]$StructuralOnly)
    foreach ($name in @('Scope','Succeeded','Count','Passed','ExecutableSHA256','Standalone','ClientAreaAnimation')) {
        if (!$Report.PSObject.Properties[$name]) { throw "Incomplete $Scope check report: missing $name." }
    }
    if ($Report.Scope -isnot [string] -or $Report.Scope -cne $Scope -or $Report.Succeeded -isnot [bool] -or !$Report.Succeeded -or
        !(Test-CheckCount $Report.Count) -or $Report.Passed -isnot [array] -or $Report.Passed.Count -ne $Report.Count -or
        $Report.Standalone -isnot [bool] -or $Report.ClientAreaAnimation -isnot [bool] -or
        $Report.ExecutableSHA256 -isnot [string] -or $Report.ExecutableSHA256 -cnotmatch '^[0-9A-F]{64}$') {
        throw "Invalid $Scope check report."
    }
    foreach ($label in $Report.Passed) {
        if ($label -isnot [string] -or [string]::IsNullOrWhiteSpace($label)) { throw "Invalid $Scope passed assertion." }
    }
    foreach ($name in @('FailedCheck','Exception')) {
        if ($Report.PSObject.Properties[$name] -and $null -ne $Report.$name) { throw "Failed $Scope check report." }
    }
    if (!$StructuralOnly -and $Report.Count -ne (Get-PclDbCheckPolicy $Scope).Count) { throw "Incomplete $Scope check count." }
    if ($RequireStandalone -and !$Report.Standalone) { throw "The $Scope receipt did not run the single EXE." }
    if ($ExecutableSHA256 -and $Report.ExecutableSHA256 -cne $ExecutableSHA256) { throw "The $Scope receipt belongs to a different executable." }
}

function Assert-PclDbVerification {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Verification, [Parameter(Mandatory)][string]$ExecutableSHA256, [switch]$Current)
    if (!$Verification.PSObject.Properties['Format'] -or $Verification.Format -isnot [string] -or $Verification.Format -cne 'PCL-DB-verification/v1' -or
        !$Verification.PSObject.Properties['Policy'] -or $Verification.Policy -isnot [string] -or
        $Verification.Policy -cnotmatch '^PCL-DB-checks/[0-9A-Za-z.-]+$' -or
        !$Verification.PSObject.Properties['Reports'] -or $Verification.Reports -isnot [array] -or $Verification.Reports.Count -ne 4) {
        throw 'Invalid verification evidence.'
    }
    if ($Current -and $Verification.Policy -cne $script:PolicyId) { throw 'The verification policy is not current; verify a fresh candidate.' }
    $seen = @{}
    foreach ($report in $Verification.Reports) {
        if (!$report -or !$report.PSObject.Properties['Scope'] -or $report.Scope -cnotin $script:CandidateScopes -or $seen.ContainsKey($report.Scope)) { throw 'Missing, duplicate or unexpected verification scope.' }
        Assert-PclDbCheckReport $report $report.Scope -ExecutableSHA256 $ExecutableSHA256 -RequireStandalone -StructuralOnly:(!$Current)
        $field = $script:Fields[$report.Scope]
        if (!$Verification.PSObject.Properties[$field] -or !(Test-CheckCount $Verification.$field) -or $Verification.$field -ne $report.Count) { throw "Verification total differs from its receipt: $field" }
        $seen[$report.Scope] = $true
    }
}

function New-PclDbVerification {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Reports, [Parameter(Mandatory)][string]$ExecutableSHA256)
    $result = [ordered]@{Format='PCL-DB-verification/v1'; Policy=$script:PolicyId}
    $normalized = @()
    foreach ($scope in $script:CandidateScopes) {
        $matches = @($Reports | Where-Object Scope -CEQ $scope)
        if ($matches.Count -ne 1 -or $Reports.Count -ne 4) { throw 'Missing, duplicate or unexpected verification scope.' }
        $report = $matches[0]
        Assert-PclDbCheckReport $report $scope -ExecutableSHA256 $ExecutableSHA256 -RequireStandalone
        $result[$script:Fields[$scope]] = $report.Count
        # Portable evidence: no local paths, logs, exception text or user settings.
        $normalized += [pscustomobject][ordered]@{Scope=$scope; Succeeded=$true; Count=$report.Count; Passed=@($report.Passed)
            ExecutableSHA256=$report.ExecutableSHA256; Standalone=$true; ClientAreaAnimation=$report.ClientAreaAnimation}
    }
    $result['Reports'] = $normalized
    [pscustomobject]$result
}

function Assert-PclDbCandidateVerification {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Candidate)
    if (!$Candidate.PSObject.Properties['Format'] -or $Candidate.Format -isnot [string] -or $Candidate.Format -cne 'PCL-DB-candidate/v3' -or
        !$Candidate.PSObject.Properties['Verification'] -or !$Candidate.PSObject.Properties['CandidateSHA256'] -or
        $Candidate.CandidateSHA256 -isnot [string] -or $Candidate.CandidateSHA256 -cnotmatch '^[0-9A-F]{64}$') { throw 'Invalid single EXE candidate report.' }
    Assert-PclDbVerification $Candidate.Verification $Candidate.CandidateSHA256 -Current
    foreach ($field in $script:Fields.Values) {
        if (!$Candidate.PSObject.Properties[$field] -or !(Test-CheckCount $Candidate.$field) -or $Candidate.$field -ne $Candidate.Verification.$field) { throw "Invalid single EXE candidate verification total: $field" }
    }
}

function Assert-PclDbEmbeddedAssemblies([object]$Assemblies) {
    if ($Assemblies -isnot [array] -or $Assemblies.Count -ne 2) { throw 'Incomplete embedded dependency inventory.' }
    $seen = @{}
    foreach ($assembly in $Assemblies) {
        if ($null -eq $assembly -or !$assembly.PSObject.Properties['Name'] -or !$assembly.PSObject.Properties['SHA256'] -or
            $assembly.Name -isnot [string] -or $assembly.Name -cnotin @('PclDownloadBridge.Core','Newtonsoft.Json') -or
            $seen.ContainsKey($assembly.Name) -or $assembly.SHA256 -isnot [string] -or $assembly.SHA256 -cnotmatch '^[0-9A-F]{64}$') {
            throw 'Invalid embedded dependency inventory.'
        }
        $seen[$assembly.Name] = $true
    }
}

Export-ModuleMember -Function Get-PclDbCheckPolicy, Assert-PclDbCheckReport, Assert-PclDbVerification, New-PclDbVerification, Assert-PclDbCandidateVerification, Assert-PclDbEmbeddedAssemblies
