Remove-Item C:\gv\shots -Recurse -ErrorAction SilentlyContinue
$p = Start-Process C:\gv\app\GnosisVPN.exe -ArgumentList "--shots","C:\gv\shots-all.txt","C:\gv\shots" -PassThru
if (-not $p.WaitForExit(1500000)) { "timeout"; $p.Kill() } else { "exit " + $p.ExitCode }
Get-Content C:\gv\shots\shots.log -ErrorAction SilentlyContinue | Select-String -Pattern "FAIL" | % { $_.Line }
"ok: " + (Get-Content C:\gv\shots\shots.log | Select-String "^ok").Count
Compress-Archive -Force C:\gv\shots\* C:\gv\shots.zip
