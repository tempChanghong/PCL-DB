param([string]$ReportPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'VerificationTools.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'VerificationProcessTools.psm1') -Force
# Imported FunctionInfo is mutable: declaring the spy below replaces its body
# in place. Retain the original module-bound script block before shadowing it.
$script:policyBody = (Get-Command Get-PclDbCheckPolicy -Module VerificationTools).ScriptBlock
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
    $record = [pscustomobject]@{Name=$Name;FilePath=$FilePath;Arguments=$Arguments;WorkingDirectory=$WorkingDirectory;
        TimeoutSeconds=$TimeoutSeconds;Outputs=@($ExpectedOutputPaths);ExitCode=0;DurationMilliseconds=0;ExecutableSHA256=('A' * 64)}
    $script:invocations.Add($record)
    return $record
}

foreach ($scope in @('standalone','all','motion','pack-search','appearance','developer','cards','navigation','compatibility','control-motion')) {
    $policy = & $script:policyBody $scope
    $hasDeadline = $null -ne $policy.PSObject.Properties['TimeoutSeconds']
    $deadline = if ($hasDeadline) { $policy.TimeoutSeconds } else { $null }
    Assert-Case ($hasDeadline -and $deadline -is [int] -and $deadline -ge 1 -and $deadline -le 3600) ("policy exposes a finite process-compatible deadline: $scope")
    $expectedDeadline = if ($scope -cin @('all','motion','navigation')) { 3600 } elseif ($scope -ceq 'pack-search') { 1800 } else { 180 }
    Assert-Case ($hasDeadline -and $deadline -ceq $expectedDeadline) ("policy budgets the full UI, motion and search suites while retaining compact-scope limits: $scope")
}
$rejected = $false
try { & $script:policyBody 'unrecognised-scope' | Out-Null } catch { $rejected = $_.Exception.Message.Contains('Unknown check scope') }
Assert-Case $rejected 'unknown scope cannot acquire an arbitrary execution budget'

function Get-PclDbCheckPolicy([string]$Scope) {
    $script:queries++
    $result = & $script:policyBody $Scope
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

# Exercise the real VS profile functions under the same process spy. Do not
# dot-source the verifier: its script body builds projects and starts WPF.
$vsAst = Read-Ast (Join-Path $PSScriptRoot 'verify-vs2026.ps1')
foreach ($functionName in @('Expand-ProfileValue','Invoke-OfflineProfile','Get-AppProfileOutput')) {
    $definition = @($vsAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $functionName}.GetNewClosure(), $true))
    if ($definition.Count -ne 1) { throw "Expected one VS verifier function: $functionName" }
    . ([scriptblock]::Create($definition[0].Extent.Text))
}
$appProject = 'src\PclDownloadBridge.App\PclDownloadBridge.App.vbproj'
$testProject = 'tests\PclDownloadBridge.Tests\PclDownloadBridge.Tests.vbproj'
$output = Join-Path $fixture 'vs outputs'
$target = Join-Path $fixture 'never built target'
$null = [IO.Directory]::CreateDirectory($target)
$properties = @{}
foreach ($project in @($appProject,$testProject)) {
    $projectDirectory = [IO.Path]::GetDirectoryName((Join-Path $root $project)) + [IO.Path]::DirectorySeparatorChar
    $properties[$project] = [pscustomobject]@{ProjectDir=$projectDirectory;TargetDir=($target + [IO.Path]::DirectorySeparatorChar);
        TargetPath=(Join-Path $target 'never-started.exe');LaunchSettingsFilePath=(Join-Path $projectDirectory 'My Project\launchSettings.json')}
}
$runs = @()
$vsProfiles = @(
    [pscustomobject]@{Name='UI Checks';Scope='all'},
    [pscustomobject]@{Name='Appearance Checks';Scope='appearance'},
    [pscustomobject]@{Name='Developer Checks';Scope='developer'},
    [pscustomobject]@{Name='Card Checks';Scope='cards'},
    [pscustomobject]@{Name='Navigation Checks';Scope='navigation'},
    [pscustomobject]@{Name='Motion Checks';Scope='motion'},
    [pscustomobject]@{Name='Pack Search Checks';Scope='pack-search'},
    [pscustomobject]@{Name='Settings Compatibility';Scope='compatibility'},
    [pscustomobject]@{Name='Preview Components';Scope='snapshot'}
)
foreach ($profile in $vsProfiles) {
    $script:queries = 0; $script:invocations.Clear(); $script:sentinel++
    $run = Invoke-OfflineProfile $appProject $profile.Name
    $runs += $run
    $record = if ($script:invocations.Count -eq 1) { $script:invocations[0] } else { $null }
    $snapshot = $profile.Scope -ceq 'snapshot'
    $expectedQueries = if ($snapshot) { 0 } else { 1 }
    $expectedDeadline = if ($snapshot) { 180 } else { $script:sentinel }
    Assert-Case ($script:queries -eq $expectedQueries -and $null -ne $record -and $run.Scope -ceq $profile.Scope -and
        $run.TimeoutSeconds -eq $expectedDeadline -and $record.TimeoutSeconds -eq $expectedDeadline) ("VS profile resolves shared check policy or the snapshot deadline: $($profile.Name)")
    $directory = Split-Path $run.OutputPath -Parent
    $directoryItem = Get-Item -LiteralPath $directory
    Assert-Case ($run.OutputPath -cne $run.SourceProfileOutputPath -and
        [IO.Path]::GetFileName($run.OutputPath) -ceq [IO.Path]::GetFileName($run.SourceProfileOutputPath) -and
        [IO.Path]::GetDirectoryName($directory) -ceq $output -and
        [IO.Path]::GetFileName($directory) -cmatch ('^vs-' + $profile.Scope + '-[0-9a-f]{32}$') -and
        $directoryItem.PSIsContainer -and !($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) ("VS profile allocates its fresh ordinary fixture directory: $($profile.Name)")
    $settings = Get-Content -LiteralPath $properties[$appProject].LaunchSettingsFilePath -Raw | ConvertFrom-Json
    $sourceArguments = Expand-ProfileValue $settings.profiles.PSObject.Properties[$profile.Name].Value.commandLineArgs $properties[$appProject]
    Assert-Case ($null -ne $record -and $record.Outputs.Count -eq 1 -and $record.Outputs[0] -ceq $run.OutputPath -and
        $run.OriginalArguments -ceq $sourceArguments -and $record.Arguments -ceq $run.Arguments -and
        $run.Arguments -ceq $run.OriginalArguments.Replace(('"' + $run.SourceProfileOutputPath + '"'), ('"' + $run.OutputPath + '"')) -and
        $record.WorkingDirectory -ceq $properties[$appProject].ProjectDir) ("VS profile retains original and effective arguments with the actual expected output: $($profile.Name)")
    Assert-Case ((Get-AppProfileOutput $profile.Name) -ceq $run.OutputPath) ("VS summary reads the actual invocation output: $($profile.Name)")
    if ($snapshot) { Assert-Case ($run.Arguments.EndsWith(' --page components --size 850x700')) 'VS snapshot retains its page and size arguments' }
}
$firstOutput = Get-AppProfileOutput 'UI Checks'
$retry = Invoke-OfflineProfile $appProject 'UI Checks'
Assert-Case ($retry.OutputPath -cne $firstOutput -and (Split-Path $retry.OutputPath -Parent) -cne (Split-Path $firstOutput -Parent)) 'VS same-profile retry cannot reuse an earlier fixture directory'
$script:queries = 0; $script:invocations.Clear()
$console = Invoke-OfflineProfile $testProject 'Offline All'
$consoleRecord = if ($script:invocations.Count -eq 1) { $script:invocations[0] } else { $null }
Assert-Case ($script:queries -eq 0 -and $null -ne $consoleRecord -and $console.Scope -ceq 'core' -and
    $console.TimeoutSeconds -eq 900 -and $consoleRecord.TimeoutSeconds -eq 900 -and $null -eq $console.OutputPath -and
    $consoleRecord.Outputs.Count -eq 0 -and $console.Arguments -ceq $console.OriginalArguments -and
    $consoleRecord.Arguments -ceq $console.Arguments -and $consoleRecord.WorkingDirectory -ceq $properties[$testProject].ProjectDir) 'VS retains the original VB console profile and its separate 900-second deadline'
$script:queries = 0; $script:invocations.Clear()
$reason = ''
try { Invoke-OfflineProfile $appProject 'PCL-DB' | Out-Null } catch { $reason = $_.Exception.Message }
Assert-Case ($reason -like '*No diagnostic output*' -and $script:invocations.Count -eq 0 -and $script:queries -eq 0) 'VS offline verification rejects ordinary interactive startup before launching anything'

$failed = @($observations.Keys | Where-Object {!$observations[$_]})
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReportPath)))
[pscustomobject]@{Succeeded=($failed.Count -eq 0);Scope='verification-timeouts';Count=$observations.Count;Passed=@($passed);Failed=@($failed);
    PowerShellVersion=$PSVersionTable.PSVersion.ToString();RealProcessesLaunched=0;PackageCreated=$false;Observations=$observations;
    ProcessResultsAreSpies=$true;PolicyDeadlinesReplacedBySentinels=$true;
    VsProfileImplementation=(Join-Path $PSScriptRoot 'verify-vs2026.ps1');VsProfileRuns=$runs;VsConsoleRun=$console;VsRetryRun=$retry;FixtureRoot=$fixture} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
if ($failed.Count) { throw ($failed -join [Environment]::NewLine) }
Write-Output "PASS all $($passed.Count) verification timeout checks."
