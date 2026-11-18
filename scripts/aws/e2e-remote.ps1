# Runs ON the Windows test server as SYSTEM (scheduled task gvpndows-e2e), so an SSH drop cannot
# kill it. Args come from C:\gv\e2e-args.json: {plan, controller_ip, log_filter, capture}.
# Writes C:\gv\e2e-state.txt: "running" ... then "JOB-EXIT <code>".
$ErrorActionPreference = 'Continue'
$a = Get-Content C:\gv\e2e-args.json -Raw | ConvertFrom-Json
Set-Content C:\gv\e2e-state.txt 'running'
$env:LOCALAPPDATA = 'C:\Users\Administrator\AppData\Local'
if ($a.log_filter) { $env:GVPNDOWS_LOG = $a.log_filter }
$out = "$env:LOCALAPPDATA\gvpndows"
Remove-Item "$out\telemetry.csv", "$out\nerdstats.jsonl", "$out\e2e-result.json" -ErrorAction SilentlyContinue
Rename-Item "$out\gvpndows.log" "gvpndows-$(Get-Date -Format yyyyMMdd-HHmmss).log" -ErrorAction SilentlyContinue
# Keep the controller (SSH client) off the tunnel: a host route via the physical gateway.
$r = Get-NetRoute -DestinationPrefix 0.0.0.0/0 | Sort-Object RouteMetric | Select-Object -First 1
New-NetRoute -DestinationPrefix "$($a.controller_ip)/32" -InterfaceIndex $r.ifIndex -NextHop $r.NextHop -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Out-Null
"route to controller via $($r.NextHop) if $($r.ifIndex)" | Out-File C:\gv\e2e-driver.txt
if ($a.capture) {
    pktmon stop 2>&1 | Out-Null
    Remove-Item C:\gv\cap.etl, C:\gv\cap.pcapng -ErrorAction SilentlyContinue
    pktmon start --capture --comp nics --pkt-size 96 --file-name C:\gv\cap.etl --file-size 1024 2>&1 | Out-File -Append C:\gv\e2e-driver.txt
}
$p = Start-Process -FilePath C:\gv\gvpndows\gvpndows.exe -ArgumentList '--e2e', $a.plan -PassThru -Wait -WorkingDirectory C:\gv\gvpndows
$rc = $p.ExitCode
if ($a.capture) {
    pktmon stop 2>&1 | Out-File -Append C:\gv\e2e-driver.txt
    pktmon etl2pcap C:\gv\cap.etl --out C:\gv\cap.pcapng 2>&1 | Out-File -Append C:\gv\e2e-driver.txt
}
Set-Content C:\gv\e2e-state.txt "JOB-EXIT $rc"
