# Install (or update) the Gnosis VPN service on the test server: C:\GnosisVPN\gvpndows.exe --service,
# demand start (never by itself), LocalSystem. Then start it and ask the pipe for x_state.
param([string]$Exe = "C:\gv\gvpndows-svc.exe")
$ErrorActionPreference = "Continue"
if (Get-Service GnosisVPN -ErrorAction SilentlyContinue) { Stop-Service GnosisVPN -Force -ErrorAction SilentlyContinue; Start-Sleep 2 }
Get-Process gvpndows -ErrorAction SilentlyContinue | Where-Object Path -eq "C:\GnosisVPN\gvpndows.exe" | Stop-Process -Force
New-Item -ItemType Directory -Force C:\GnosisVPN | Out-Null
Copy-Item $Exe C:\GnosisVPN\gvpndows.exe -Force
if (-not (Test-Path C:\GnosisVPN\gnosis)) { Copy-Item C:\gv\gvpndows\gnosis C:\GnosisVPN\gnosis -Recurse }
if (-not (Get-Service GnosisVPN -ErrorAction SilentlyContinue)) {
  New-Service -Name GnosisVPN -BinaryPathName '"C:\GnosisVPN\gvpndows.exe" --service' -DisplayName "Gnosis VPN" -StartupType Manual -Description "Runs the Gnosis VPN client core and the tunnel for the Gnosis VPN app." | Out-Null
}
Start-Service GnosisVPN; Start-Sleep 5
"service: " + (Get-Service GnosisVPN).Status
try {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream(".", "gnosisvpn", [System.IO.Pipes.PipeDirection]::InOut); $c.Connect(5000)
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $rd = New-Object System.IO.StreamReader($c)
  $w.WriteLine('{"type":"x_state"}'); $l = $rd.ReadLine(); "pipe: " + $l.Substring(0, [Math]::Min(300, $l.Length)); $c.Dispose()
} catch { "pipe: " + $_.Exception.Message }
Get-Content C:\ProgramData\gvpndows\gvpndows.log -Tail 8 -ErrorAction SilentlyContinue | % { $_.Substring(0, [Math]::Min(200, $_.Length)) }
