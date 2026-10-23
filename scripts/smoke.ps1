param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$env:DOTNET_ROOT = $dotnetRoot
$env:DOTNET_ROOT_X64 = $dotnetRoot
$source = (Resolve-Path -LiteralPath $Executable).Path
$smokeRoot = Join-Path (Get-Location) 'work/smoke'
New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
$target = Join-Path $smokeRoot 'PCL CE Restore.exe'
Copy-Item -LiteralPath $source -Destination $target -Force
$launcherProcess = $null
try {
    $launcherProcess = Start-Process -FilePath $target -WorkingDirectory $smokeRoot -WindowStyle Hidden -PassThru
    if (-not $launcherProcess.WaitForInputIdle(30000)) { throw 'Launcher did not become responsive within 30 seconds.' }
    Start-Sleep -Seconds 5
    $launcherProcess.Refresh()
    if ($launcherProcess.HasExited) { throw "Launcher exited unexpectedly: $($launcherProcess.ExitCode)" }
    Write-Output 'PASS WPF process became responsive and remained running.'
}
catch {
    $detail = "WPF smoke failed: $($_.Exception.Message); dotnet=$dotnetRoot"
    Write-Output ('::error::' + $detail.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A'))
    throw
}
finally {
    if ($launcherProcess) {
        $launcherProcess.Refresh()
        if (-not $launcherProcess.HasExited) { Stop-Process -Id $launcherProcess.Id -Force }
    }
}
