param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
# A preinstalled architecture-specific root takes precedence over DOTNET_ROOT.
# Point native apphosts at the SDK installed for this job, including its desktop runtime.
$env:DOTNET_ROOT = $dotnetRoot
if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -eq 'Arm64') {
    $env:DOTNET_ROOT_ARM64 = $dotnetRoot
} else {
    $env:DOTNET_ROOT_X64 = $dotnetRoot
}
$source = (Resolve-Path -LiteralPath $Executable).Path
$smokeRoot = Join-Path (Get-Location) 'work/smoke'
New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
$target = Join-Path $smokeRoot 'PCL CE Restore.exe'
Copy-Item -LiteralPath $source -Destination $target -Force
$process = Start-Process -FilePath $target -WorkingDirectory $smokeRoot -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForInputIdle(30000)) { throw 'Launcher did not become responsive within 30 seconds.' }
    Start-Sleep -Seconds 5
    $process.Refresh()
    if ($process.HasExited) { throw "Launcher exited unexpectedly: $($process.ExitCode)" }
    Write-Output 'PASS WPF process became responsive and remained running.'
}
catch {
    $process.Refresh()
    $exitInfo = if ($process.HasExited) { "exit=$($process.ExitCode)" } else { 'still running' }
    $runtimeInfo = (dotnet --list-runtimes) -join '; '
    $detail = "WPF smoke failed: $($_.Exception.Message); $exitInfo; dotnet=$dotnetRoot; $runtimeInfo"
    Write-Output ('::error::' + $detail.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A'))
    throw
}
finally {
    $process.Refresh()
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}
