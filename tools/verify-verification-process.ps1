param([string]$ReportPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'VerificationProcessTools.psm1')
if (!$ReportPath) { $ReportPath = Join-Path $root 'artifacts/verification-process-checks.json' }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('PCL-DB Checks ' + [char]0x9A8C + [char]0x8BC1 + ' ' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($fixture)
$helper = Join-Path $fixture 'process fixture.ps1'
$hostExe = Join-Path $PSHOME $(if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh.exe' } else { 'powershell.exe' })
@'
param([string]$Mode, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
[Console]::Out.WriteLine('fixture stdout')
[Console]::Error.WriteLine('fixture stderr')
switch ($Mode) {
    'write' { [IO.File]::WriteAllText($OutputPath, (Get-Location).Path); exit 0 }
    'empty' { [IO.File]::WriteAllText($OutputPath, ''); exit 0 }
    'fail' { [IO.File]::WriteAllText($OutputPath, '{"Succeeded":true}'); exit 7 }
    'wait' { [IO.File]::WriteAllText($OutputPath, [string]$PID); Start-Sleep -Seconds 30; exit 0 }
    'none' { exit 0 }
    default { exit 9 }
}
'@ | Set-Content -LiteralPath $helper -Encoding UTF8
$passed = New-Object 'Collections.Generic.List[string]'
$output = Join-Path $fixture 'current receipt.json'
$unrelated = $null
$script:sequence = 0
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
function Invoke-Fixture([string]$Mode, [string[]]$Expected=@($output), [int]$Timeout=20) {
    $script:sequence++
    Invoke-PclDbVerificationProcess -Name ('fixture-' + $Mode) -FilePath $hostExe -Arguments ('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $helper + '" ' + $Mode + ' "' + $output + '"') -WorkingDirectory $fixture -StdoutPath (Join-Path $fixture "$script:sequence.stdout.log") -StderrPath (Join-Path $fixture "$script:sequence.stderr.log") -ExpectedOutputPaths $Expected -TimeoutSeconds $Timeout
}
try {
    $sentinel = Join-Path $fixture 'unrelated receipt.json'
    [IO.File]::WriteAllText($sentinel, 'retain this prior receipt')
    [IO.File]::WriteAllText($output, '{"Succeeded":true,"Run":"old"}')
    Assert-Rejected { Invoke-Fixture 'none' } 'no fresh output' 'exit zero without output cannot reuse a prior success receipt'
    Assert-Case (!(Test-Path -LiteralPath $output)) 'the stale expected receipt is invalidated before launch'
    Assert-Case ([IO.File]::ReadAllText($sentinel) -ceq 'retain this prior receipt') 'unrelated historical output is preserved'
    $run = Invoke-Fixture 'write'
    Assert-Case ($run.ExitCode -eq 0 -and $run.ExecutableSHA256 -ceq (Get-FileHash -LiteralPath $hostExe -Algorithm SHA256).Hash) 'successful execution retains its real exit code and executable hash'
    Assert-Case ([IO.File]::ReadAllText($output) -ceq $fixture) 'quoted Unicode paths and the requested working directory survive process launch'
    Assert-Case ((Get-Content -LiteralPath $run.Stdout -Raw).Contains('fixture stdout') -and (Get-Content -LiteralPath $run.Stderr -Raw).Contains('fixture stderr')) 'both output streams remain available in separate logs'
    Assert-Rejected { Invoke-Fixture 'fail' } 'exit 7' 'a nonzero exit is rejected even when it writes a successful-looking report'
    Assert-Rejected { Invoke-Fixture 'empty' } 'no fresh output' 'an empty output does not satisfy the receipt contract'
    $preview = Join-Path $fixture 'old preview.png'
    [IO.File]::WriteAllText($preview, 'old image fixture')
    Assert-Rejected { Invoke-Fixture 'write' @($output,$preview) } 'no fresh output' 'all requested outputs must be fresh, including a preview image'
    Assert-Case (!(Test-Path -LiteralPath $preview)) 'a missing new preview cannot inherit a stale image'
    $run = Invoke-Fixture 'none' @()
    Assert-Case ($run.ExitCode -eq 0) 'console-only profiles may intentionally produce no report file'
    Assert-Rejected { Invoke-Fixture 'write' @($hostExe) } 'Conflicting verification paths' 'output cannot alias the verification executable'
    Assert-Rejected { Invoke-Fixture 'write' @($output,$output.ToUpperInvariant()) } 'Conflicting verification paths' 'case-insensitive output aliases are rejected before deletion'
    [IO.File]::WriteAllText($output, 'keep on invalid input')
    Assert-Rejected { Invoke-Fixture 'write' @($output,$fixture) } 'ordinary file' 'directory outputs fail before any expected file is invalidated'
    Assert-Case ([IO.File]::ReadAllText($output) -ceq 'keep on invalid input') 'invalid path configuration does not delete another expected file'
    $unrelatedPid = Join-Path $fixture 'other process.pid'
    $unrelated = Start-Process -FilePath $hostExe -ArgumentList ('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $helper + '" wait "' + $unrelatedPid + '"') -WorkingDirectory $fixture -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $fixture 'other.stdout.log') -RedirectStandardError (Join-Path $fixture 'other.stderr.log')
    $null = $unrelated.Handle
    Assert-Rejected { Invoke-Fixture 'wait' @() 3 } 'timed out' 'a hung verification process is bounded by its deadline'
    $ownedPid = [int][IO.File]::ReadAllText($output)
    $remaining = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
    Assert-Case ($null -eq $remaining) 'timeout cleanup terminates the process owned by the failed invocation'
    Assert-Case (!$unrelated.HasExited) 'timeout cleanup leaves a separate same-name process running'
    $run = Invoke-Fixture 'write'
    Assert-Case ($run.ExitCode -eq 0 -and [IO.File]::ReadAllText($output) -ceq $fixture) 'a retry after timeout requires and accepts a new complete output'
    $result = [pscustomobject]@{Succeeded=$true; Scope='verification-process'; Count=$passed.Count; Passed=@($passed); Fixture=$fixture; PowerShell=$PSVersionTable.PSVersion.ToString()}
} catch {
    $result = [pscustomobject]@{Succeeded=$false; Scope='verification-process'; Count=$passed.Count; Passed=@($passed); Fixture=$fixture; Error=$_.Exception.ToString()}
    throw
} finally {
    if ($unrelated) {
        try { if (!$unrelated.HasExited) { $unrelated.Kill(); $null = $unrelated.WaitForExit(2000) } } finally { $unrelated.Dispose() }
    }
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReportPath)))
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}
