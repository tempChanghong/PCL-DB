param([string]$ReportPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'VerificationTools.psm1') -Force
$script:policyCommand = Get-Command Get-PclDbCheckPolicy -Module VerificationTools
if (!$ReportPath) { $ReportPath = Join-Path $root 'artifacts/verification-timeout-checks.json' }
$observations = [ordered]@{}
$passed = New-Object 'Collections.Generic.List[string]'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('PCL-DB Deadline ' + [char]0x9A8C + ' ' + [Guid]::NewGuid().ToString('N'))
$script:invocations = New-Object 'Collections.Generic.List[object]'
$script:queries = 0
$script:sentinel = 701
function Assert-Case([bool]$Condition, [string]$Name) {
    $observations[$Name] = $Condition
    if ($Condition) { $passed.Add($Name); Write-Output "PASS $Name" }
}
function Read-Ast([string]$Path) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "Invalid PowerShell source: $Path" }
    return $ast
}
function Find-Commands($Ast, [string]$Name) {
    return @($Ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq $Name}.GetNewClosure(), $true))
}
function Invoke-PclDbVerificationProcess {
    param([string]$Name, [string]$FilePath, [string]$Arguments, [string]$WorkingDirectory, [string]$StdoutPath, [string]$StderrPath,
          [string[]]$ExpectedOutputPaths=@(), [int]$TimeoutSeconds)
    # Capture the real callers' bound arguments. Never launch or build anything.
    $record = [pscustomobject]@{Name=$Name;FilePath=$FilePath;Arguments=$Arguments;TimeoutSeconds=$TimeoutSeconds;Outputs=@($ExpectedOutputPaths)}
    $script:invocations.Add($record)
    return $record
}

foreach ($scope in @('standalone','all','motion','pack-search','appearance','developer','cards','navigation','compatibility','control-motion')) {
    $policy = & $script:policyCommand $scope
    $hasDeadline = $null -ne $policy.PSObject.Properties['TimeoutSeconds']
    $deadline = if ($hasDeadline) { $policy.TimeoutSeconds } else { $null }
    Assert-Case ($hasDeadline -and $deadline -is [int] -and $deadline -ge 1 -and $deadline -le 3600) ("policy exposes a finite process-compatible deadline: $scope")
    $expectedDeadline = if ($scope -cin @('all','motion')) { 600 } else { 180 }
    Assert-Case ($hasDeadline -and $deadline -ceq $expectedDeadline) ("policy budgets the full UI and motion suites while retaining compact-scope limits: $scope")
}
$rejected = $false
try { & $script:policyCommand 'unrecognised-scope' | Out-Null } catch { $rejected = $_.Exception.Message.Contains('Unknown check scope') }
Assert-Case $rejected 'unknown scope cannot acquire an arbitrary execution budget'

function Get-PclDbCheckPolicy([string]$Scope) {
    $script:queries++
    $result = & $script:policyCommand $Scope
    # Distinct test budgets prove consumers use metadata, not matching literals.
    $result | Add-Member -NotePropertyName TimeoutSeconds -NotePropertyValue $script:sentinel -Force
    return $result
}
$buildAst = Read-Ast (Join-Path $root 'build.ps1')
$singleAst = Read-Ast (Join-Path $PSScriptRoot 'verify-single-exe.ps1')
foreach ($caller in @(
    [pscustomobject]@{Name='ordinary build';Ast=$buildAst;Scopes=@('all','motion','pack-search')},
    [pscustomobject]@{Name='isolated candidate';Ast=$singleAst;Scopes=@('standalone','all','motion','pack-search')}
)) {
    $query = @(Find-Commands $caller.Ast 'Get-PclDbCheckPolicy')
    if ($query.Count -ne 1) { throw "Expected one policy query in $($caller.Name)." }
    $assignment = $query[0]
    while ($null -ne $assignment -and $assignment -isnot [Management.Automation.Language.AssignmentStatementAst]) { $assignment = $assignment.Parent }
    if ($null -eq $assignment) { throw "Policy query must assign its result in $($caller.Name)." }
    $launches = @(Find-Commands $caller.Ast 'Invoke-PclDbVerificationProcess' | Where-Object {$_.Extent.Text.Contains('$policy.Mode')})
    if ($launches.Count -ne 1) { throw "Expected one scoped process invocation in $($caller.Name)." }
    foreach ($scope in $caller.Scopes) {
        $script:queries = 0; $script:invocations.Clear(); $script:sentinel++
        $app = Join-Path $fixture 'ordinary.exe'; $candidate = Join-Path $fixture 'candidate.exe'
        $cold = $fixture; $operation = $fixture; $name = $scope + '-fixture'
        $path = Join-Path $fixture ($scope + '.json')
        $expected = @($path)
        if ($scope -ceq 'standalone') { $expected += Join-Path $fixture 'embedded-content.json' }
        # Execute only the two source expressions under process spies, never the
        # build/package scripts or their single-executable generation bodies.
        . ([scriptblock]::Create($assignment.Extent.Text))
        $launch = [scriptblock]::Create('param($PSScriptRoot)' + [Environment]::NewLine + $launches[0].Extent.Text)
        $null = & $launch (Split-Path $caller.Ast.Extent.File -Parent)
        $record = if ($script:invocations.Count -eq 1) { $script:invocations[0] } else { $null }
        $label = $caller.Name + '/' + $scope
        Assert-Case ($script:queries -eq 1) ("caller resolves this scope through shared policy: $label")
        Assert-Case ($null -ne $record -and $record.TimeoutSeconds -eq $script:sentinel) ("caller forwards the resolved deadline unchanged: $label")
        Assert-Case ($null -ne $record -and $record.Arguments -ceq ($policy.Mode + ' "' + $path + '"') -and
            ($record.Outputs -join '|') -ceq ($expected -join '|')) ("deadline selection retains check mode and required output paths: $label")
    }
}
$core = @(Find-Commands $buildAst 'Invoke-PclDbVerificationProcess' | Where-Object {$_.Extent.Text.Contains("'core fixtures'")})
if ($core.Count -ne 1) { throw 'Expected one core fixture invocation.' }
$script:invocations.Clear()
$coreLaunch = [scriptblock]::Create('param($PSScriptRoot)' + [Environment]::NewLine + $core[0].Extent.Text)
$null = & $coreLaunch $root
Assert-Case ($script:invocations.Count -eq 1 -and $script:invocations[0].TimeoutSeconds -eq 900) 'ordinary build retains the separate core fixture deadline'
$processAst = Read-Ast (Join-Path $PSScriptRoot 'VerificationProcessTools.psm1')
$parameter = @($processAst.FindAll({param($node) $node -is [Management.Automation.Language.ParameterAst] -and $node.Name.VariablePath.UserPath -ceq 'TimeoutSeconds'}, $true))
Assert-Case ($parameter.Count -eq 1 -and $parameter[0].DefaultValue.SafeGetValue() -eq 180) 'generic process launcher keeps its bounded default for unrelated callers'

$failed = @($observations.Keys | Where-Object {!$observations[$_]})
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReportPath)))
[pscustomobject]@{Succeeded=($failed.Count -eq 0);Scope='verification-timeouts';Count=$observations.Count;Passed=@($passed);Failed=@($failed);
    PowerShellVersion=$PSVersionTable.PSVersion.ToString();RealProcessesLaunched=0;PackageCreated=$false;Observations=$observations} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
if ($failed.Count) { throw ($failed -join [Environment]::NewLine) }
Write-Output "PASS all $($passed.Count) verification timeout checks."
