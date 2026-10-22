Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Only explicit outputs from this invocation are invalidated. A zero exit code
# without new output can never reuse yesterday's successful receipt or preview.
function Invoke-PclDbVerificationProcess {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name,
          [Parameter(Mandatory)][string]$FilePath,
          [string]$Arguments,
          [Parameter(Mandatory)][string]$WorkingDirectory,
          [Parameter(Mandatory)][string]$StdoutPath,
          [Parameter(Mandatory)][string]$StderrPath,
          [string[]]$ExpectedOutputPaths = @(),
          [ValidateRange(1,3600)][int]$TimeoutSeconds = 180)
    $executable = [IO.Path]::GetFullPath($FilePath)
    $working = [IO.Path]::GetFullPath($WorkingDirectory)
    $stdout = [IO.Path]::GetFullPath($StdoutPath)
    $stderr = [IO.Path]::GetFullPath($StderrPath)
    if (!(Test-Path -LiteralPath $executable -PathType Leaf) -or !(Test-Path -LiteralPath $working -PathType Container)) { throw "Invalid verification executable or working directory: $Name" }
    $outputs = @($ExpectedOutputPaths | ForEach-Object { [IO.Path]::GetFullPath($_) })
    $seen = @{}
    foreach ($path in (@($executable,$stdout,$stderr) + $outputs)) {
        if ($seen.ContainsKey($path)) { throw "Conflicting verification paths: $path" }
        $seen[$path] = $true
    }
    foreach ($path in (@($stdout,$stderr) + $outputs)) {
        if (Test-Path -LiteralPath $path) {
            $item = Get-Item -LiteralPath $path -Force
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Verification output must be an ordinary file: $path" }
        }
    }
    foreach ($path in $outputs) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    foreach ($path in (@($stdout,$stderr) + $outputs)) {
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    }
    $hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    $options = @{FilePath=$executable; WorkingDirectory=$working; WindowStyle='Hidden'; PassThru=$true; RedirectStandardOutput=$stdout; RedirectStandardError=$stderr}
    if ($Arguments) { $options.ArgumentList = $Arguments }
    $process = Start-Process @options
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        # Retain the handle before exit; Windows PowerShell can otherwise lose ExitCode.
        $null = $process.Handle
        while (!$process.WaitForExit(200)) {
            if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw "Verification timed out: $Name ($TimeoutSeconds seconds). Logs: $stdout ; $stderr" }
        }
        $process.Refresh()
        $code = $process.ExitCode
        if ($null -eq $code -or $code -ne 0) { throw "Verification failed: $Name (exit $code). Logs: $stdout ; $stderr" }
        foreach ($path in $outputs) {
            if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Verification produced no fresh output: $Name ($path)" }
        }
        if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -cne $hash) { throw "Verification executable changed during the run: $Name" }
        [pscustomobject]@{Name=$Name; ProcessId=$process.Id; ExitCode=$code; DurationMilliseconds=$timer.ElapsedMilliseconds;
            ExecutableSHA256=$hash; Stdout=$stdout; Stderr=$stderr; OutputPaths=$outputs}
    } finally {
        try {
            # Never kill by name or traverse another process tree. The Process
            # object owns the handle returned by this invocation's Start-Process.
            if (!$process.HasExited) { $process.Kill(); $null = $process.WaitForExit(2000) }
        } finally { $timer.Stop(); $process.Dispose() }
    }
}

Export-ModuleMember -Function Invoke-PclDbVerificationProcess
