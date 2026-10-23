param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
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
finally {
    $process.Refresh()
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}
