Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VerificationTools.psm1')
$script:Documents = @('LICENCE','README.md','SOURCE_REFERENCES.md','THIRD_PARTY_NOTICES.md','VALIDATION.md','PATCH_MECHANISMS.md','VS2026_DEBUG.md')

function Invoke-SourceGit([string]$Root, [string]$Arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = 'git'
    $start.Arguments = '-C "' + $Root + '" ' + $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEnd()
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Source Git inspection failed: $errorText" }
        return $output
    } finally { $process.Dispose() }
}

function Get-TextHash([string]$Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-PclDbSourceState {
    param([Parameter(Mandatory)][string]$ProjectRoot)
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).ProviderPath
    $files = @()
    $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [string[]]$names = @((Invoke-SourceGit $ProjectRoot 'ls-files -z --cached --others --exclude-standard').Split([char]0) | Where-Object { $_ -and $unique.Add($_) })
    # .NET Framework and modern .NET disagree on culture sorting of punctuation.
    [Array]::Sort($names, [StringComparer]::Ordinal)
    foreach ($name in $names) {
        if ([IO.Path]::IsPathRooted($name) -or $name.Split('/') -contains '..') { throw 'Invalid source input path.' }
        $path = Join-Path $ProjectRoot $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $item = Get-Item -LiteralPath $path
            $files += [pscustomobject][ordered]@{Name=$name; Length=$item.Length; SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
        } else {
            $files += [pscustomobject][ordered]@{Name=$name; Length=-1; SHA256=$null}
        }
    }
    [pscustomobject][ordered]@{
        Commit=(Invoke-SourceGit $ProjectRoot 'rev-parse HEAD').Trim()
        Branch=(Invoke-SourceGit $ProjectRoot 'rev-parse --abbrev-ref HEAD').Trim()
        Dirty=![string]::IsNullOrWhiteSpace((Invoke-SourceGit $ProjectRoot 'status --porcelain=v1 --untracked-files=normal'))
        # Hash canonical UTF-8 records rather than host-dependent ConvertTo-Json escaping.
        InventorySHA256=(Get-TextHash ([string]::Join("`n", @($files | ForEach-Object {
            $_.Name + [char]0 + $_.Length.ToString([Globalization.CultureInfo]::InvariantCulture) + [char]0 + $_.SHA256
        }))))
        Files=$files
    }
}

function Assert-PclDbSourceState([object]$Expected, [object]$Actual) {
    if ($Expected.Commit -cne $Actual.Commit -or $Expected.Branch -cne $Actual.Branch -or
        $Expected.Dirty -ne $Actual.Dirty -or $Expected.InventorySHA256 -cne $Actual.InventorySHA256) {
        throw 'Source changed since candidate verification; build and verify a fresh candidate.'
    }
}

function Get-PclDbReleaseVersion([string]$Version, [string]$ProductVersion, [string]$Commit) {
    $pattern = '^' + [regex]::Escape($Version) + '(?:-(?:alpha|beta|rc)\.[1-9]\d*)?\+' + [regex]::Escape($Commit) + '$'
    if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $Commit -notmatch '^[0-9a-f]{40}$' -or $ProductVersion -cnotmatch $pattern) {
        throw 'EXE product version does not identify the release and inspected source commit.'
    }
    return $ProductVersion.Substring(0, $ProductVersion.IndexOf('+'))
}

function Test-PclDbCandidate {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectRoot, [Parameter(Mandatory)][string]$CandidateResultPath)
    $result = Get-Content -LiteralPath $CandidateResultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-PclDbCandidateVerification $result
    if (!$result.PSObject.Properties['ReleaseVersion'] -or
        $result.Succeeded -isnot [bool] -or !$result.Succeeded -or
        $result.ColdDirectoryContainsOnlyExe -isnot [bool] -or !$result.ColdDirectoryContainsOnlyExe -or $result.Version -notmatch '^\d+\.\d+\.\d+$' -or
        $result.Source.Commit -notmatch '^[0-9a-f]{40}$' -or $result.Source.InventorySHA256 -notmatch '^[0-9A-F]{64}$') { throw 'Invalid single EXE candidate report.' }
    Assert-PclDbSourceState $result.Source (Get-PclDbSourceState $ProjectRoot)
    $expectedNames = @('PCL-DB.exe') + $script:Documents
    if ($result.PayloadFiles.Count -ne 8 -or $result.EmbeddedDocuments.Count -ne 7 -or $result.EmbeddedAssemblies.Count -ne 2) { throw 'Incomplete candidate inventory.' }
    $seen = @{}
    foreach ($file in $result.PayloadFiles) {
        if ($file.Name -cnotin $expectedNames -or $seen.ContainsKey($file.Name) -or $file.SHA256 -notmatch '^[0-9A-F]{64}$') { throw 'Unexpected or duplicate candidate file.' }
        $seen[$file.Name] = $true
        $path = Join-Path $result.PayloadDirectory $file.Name
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $file.Length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $file.SHA256) { throw "Candidate bytes changed: $($file.Name)" }
        if ($file.Name -eq 'PCL-DB.exe') {
            if ($file.SHA256 -cne $result.CandidateSHA256 -or [Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString(3) -ne $result.Version) { throw 'Candidate EXE identity mismatch.' }
            $releaseVersion = Get-PclDbReleaseVersion $result.Version ([Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion) $result.Source.Commit
            if ($releaseVersion -cne $result.ReleaseVersion) { throw 'Candidate EXE release version mismatch.' }
        } else {
            $embedded = @($result.EmbeddedDocuments | Where-Object Name -CEQ $file.Name)
            if ($embedded.Count -ne 1 -or $embedded[0].SHA256 -cne $file.SHA256) { throw 'Candidate document does not match embedded bytes.' }
        }
    }
    if (@(Get-ChildItem -LiteralPath $result.PayloadDirectory -Force).Count -ne 8) { throw 'Candidate directory contains extra files.' }
    return $result
}

Export-ModuleMember -Function Get-PclDbSourceState, Assert-PclDbSourceState, Get-PclDbReleaseVersion, Test-PclDbCandidate
