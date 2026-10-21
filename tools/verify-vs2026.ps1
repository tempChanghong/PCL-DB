param([switch]$Full)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\vs2026'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer / vswhere.exe was not found.' }
$installation = (& $vswhere -latest -products '*' -version '[18.0,19.0)' -requires Microsoft.VisualStudio.Workload.ManagedDesktop Microsoft.Net.Component.4.8.TargetingPack -format json -utf8 | ConvertFrom-Json | Select-Object -First 1)
if (!$installation) { throw 'VS 2026 needs .NET desktop development and the .NET Framework 4.8 targeting pack.' }
$msbuild = Join-Path $installation.installationPath 'MSBuild\Current\Bin\amd64\MSBuild.exe'
if (!(Test-Path -LiteralPath $msbuild)) { throw 'VS 2026 MSBuild.exe was not found.' }
$appProject = 'src\PclDownloadBridge.App\PclDownloadBridge.App.vbproj'
$testProject = 'tests\PclDownloadBridge.Tests\PclDownloadBridge.Tests.vbproj'
$projects = @($appProject, 'src\PclDownloadBridge.Core\PclDownloadBridge.Core.vbproj', $testProject)
$properties = @{}

function Expand-ProfileValue([string]$value, $evaluated) {
    $expanded = $value.Replace('$(ProjectDir)', $evaluated.ProjectDir).Replace('$(TargetDir)', $evaluated.TargetDir)
    if ($expanded.Contains('$(')) { throw "Unsupported/unexpanded profile macro: $expanded" }
    return $expanded
}

function Invoke-OfflineProfile([string]$project, [string]$name) {
    $evaluated = $properties[$project]
    $settings = Get-Content -LiteralPath $evaluated.LaunchSettingsFilePath -Raw | ConvertFrom-Json
    $profile = $settings.profiles.PSObject.Properties[$name].Value
    if (!$profile -or $profile.commandName -ne 'Project') { throw "Missing Project launch profile: $name" }
    $arguments = Expand-ProfileValue $profile.commandLineArgs $evaluated
    $working = Expand-ProfileValue $profile.workingDirectory $evaluated
    if ($arguments -match '--live') { throw 'This verifier only runs offline launch profiles.' }
    if (!(Test-Path -LiteralPath $working -PathType Container)) { throw "Invalid profile working directory: $working" }
    $prefix = if ($project -eq $appProject) { 'app-' } else { 'tests-' }
    $stem = $prefix + ($name -replace '[^A-Za-z0-9_-]', '_')
    $stdout = Join-Path $output ($stem + '.stdout.log')
    $stderr = Join-Path $output ($stem + '.stderr.log')
    $options = @{FilePath=$evaluated.TargetPath; WorkingDirectory=$working; WindowStyle='Hidden'; Wait=$true; PassThru=$true; RedirectStandardOutput=$stdout; RedirectStandardError=$stderr}
    if ($arguments) { $options.ArgumentList = $arguments }
    $process = Start-Process @options
    if ($process.ExitCode -ne 0) {
        Get-Content -LiteralPath $stderr -Tail 20
        Get-Content -LiteralPath $stdout -Tail 12
        throw "Profile failed: $name (exit $($process.ExitCode))"
    }
    return [pscustomobject]@{Project=$project; Profile=$name; ExitCode=$process.ExitCode; WorkingDirectory=$working; Arguments=$arguments; Stdout=$stdout; Stderr=$stderr}
}

Push-Location -LiteralPath $root
try {
    $buildLog = Join-Path $output 'debug-build.log'
    & $msbuild PclDownloadBridge.sln -restore -t:Build -p:Configuration=Debug '-p:Platform=Any CPU' -nologo -v:minimal *> $buildLog
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $buildLog -Tail 30; throw 'VS 2026 Debug build failed.' }
    foreach ($project in $projects) {
        $json = (& $msbuild $project -nologo -p:Configuration=Debug -p:Platform=AnyCPU -getProperty:TargetPath,TargetDir,ProjectDir,DebugType,DebugSymbols,Optimize,LaunchSettingsFilePath) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "Cannot evaluate project: $project" }
        $evaluated = ($json | ConvertFrom-Json).Properties
        if ($evaluated.DebugType -ne 'portable' -or $evaluated.DebugSymbols -ne 'true' -or $evaluated.Optimize -ne 'false') { throw "Invalid Debug symbol/optimization settings: $project" }
        $pdb = [IO.Path]::ChangeExtension($evaluated.TargetPath, '.pdb')
        if (!(Test-Path -LiteralPath $pdb)) { throw "Missing PDB: $pdb" }
        if ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($pdb), 0, 4) -ne 'BSJB') { throw "Expected portable PDB: $pdb" }
        $properties[$project] = $evaluated
    }
    $runs = @()
    if ($Full) {
        $runs += Invoke-OfflineProfile $testProject 'Offline All'
    } else {
        foreach ($name in @('Download Plan', 'Native Libraries', 'Cleanup Cancellation', 'Final Commit', 'Transfer Completion', 'PCL File Reuse', 'Copy Fallback', 'Library Metadata', 'Library Selection', 'Metadata Transfers', 'Processor Log Recovery', 'Java Runtime Probe', 'Folder Discovery')) { $runs += Invoke-OfflineProfile $testProject $name }
    }
    $runs += Invoke-OfflineProfile $appProject 'UI Checks'
    $runs += Invoke-OfflineProfile $appProject 'Appearance Checks'
    $runs += Invoke-OfflineProfile $appProject 'Developer Checks'
    $runs += Invoke-OfflineProfile $appProject 'Card Checks'
    $runs += Invoke-OfflineProfile $appProject 'Navigation Checks'
    $runs += Invoke-OfflineProfile $appProject 'Settings Compatibility'
    $runs += Invoke-OfflineProfile $appProject 'Preview Components'
    $uiPath = Join-Path $properties[$appProject].TargetDir 'vs-ui-checks.json'
    $ui = Get-Content -LiteralPath $uiPath -Raw | ConvertFrom-Json
    if (!$ui.Succeeded -or $ui.Scope -ne 'all' -or $ui.Count -ne 620 -or $ui.Count -ne $ui.Passed.Count) { throw 'The UI profile did not produce a complete report.' }
    $appearancePath = Join-Path $properties[$appProject].TargetDir 'vs-appearance-checks.json'
    $appearance = Get-Content -LiteralPath $appearancePath -Raw | ConvertFrom-Json
    if (!$appearance.Succeeded -or $appearance.Scope -ne 'appearance' -or $appearance.Count -ne 129 -or $appearance.Count -ne $appearance.Passed.Count) { throw 'The appearance profile did not produce a complete report.' }
    $developerPath = Join-Path $properties[$appProject].TargetDir 'vs-developer-checks.json'
    $developer = Get-Content -LiteralPath $developerPath -Raw | ConvertFrom-Json
    if (!$developer.Succeeded -or $developer.Scope -ne 'developer' -or $developer.Count -ne 36 -or $developer.Count -ne $developer.Passed.Count) { throw 'The developer profile did not produce a complete report.' }
    $cardPath = Join-Path $properties[$appProject].TargetDir 'vs-card-checks.json'
    $cardChecks = Get-Content -LiteralPath $cardPath -Raw | ConvertFrom-Json
    if (!$cardChecks.Succeeded -or $cardChecks.Scope -ne 'cards' -or $cardChecks.Count -ne 66 -or $cardChecks.Count -ne $cardChecks.Passed.Count) { throw 'The card profile did not produce a complete report.' }
    $navigationPath = Join-Path $properties[$appProject].TargetDir 'vs-navigation-checks.json'
    $navigation = Get-Content -LiteralPath $navigationPath -Raw | ConvertFrom-Json
    if (!$navigation.Succeeded -or $navigation.Scope -ne 'navigation' -or $navigation.Count -ne 31 -or $navigation.Count -ne $navigation.Passed.Count) { throw 'The navigation profile did not produce a complete report.' }
    $compatibilityPath = Join-Path $properties[$appProject].TargetDir 'vs-compatibility-checks.json'
    $compatibility = Get-Content -LiteralPath $compatibilityPath -Raw | ConvertFrom-Json
    if (!$compatibility.Succeeded -or $compatibility.Scope -ne 'compatibility' -or $compatibility.Count -ne 49 -or $compatibility.Count -ne $compatibility.Passed.Count) { throw 'The settings compatibility profile did not produce a complete report.' }
    $snapshot = Join-Path $properties[$appProject].TargetDir 'vs-components.png'
    if (!(Test-Path -LiteralPath $snapshot)) { throw 'The preview profile did not produce its image.' }
    $consoleCount = 0
    foreach ($run in $runs | Where-Object Project -eq $testProject) {
        $consoleCount += @(Get-Content -LiteralPath $run.Stdout | Where-Object { $_ -match '^PASS ' }).Count
    }
    $report = [ordered]@{VisualStudio=$installation.displayName; InstallationVersion=$installation.installationVersion; MSBuild=$msbuild; Sdk=(& dotnet --version); Configuration='Debug|Any CPU'; PortableSymbols=$true; Optimized=$false; FullConsoleRun=[bool]$Full; ConsoleChecks=$consoleCount; UiChecks=$ui.Count; UiReport=$uiPath; AppearanceChecks=$appearance.Count; AppearanceReport=$appearancePath; CardChecks=$cardChecks.Count; CardReport=$cardPath; NavigationChecks=$navigation.Count; NavigationReport=$navigationPath; CompatibilityChecks=$compatibility.Count; CompatibilityReport=$compatibilityPath; Snapshot=$snapshot; IdeF5BreakpointVerified=$false; Runs=$runs}
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'report.json') -Encoding UTF8
    Write-Output "PASS VS 2026 Debug build, portable PDBs, $consoleCount console checks, $($ui.Count) UI checks; focused appearance $($appearance.Count), cards $($cardChecks.Count), navigation $($navigation.Count), compatibility $($compatibility.Count)."
    Write-Output "Report: $(Join-Path $output 'report.json')"
} finally {
    Pop-Location
}
