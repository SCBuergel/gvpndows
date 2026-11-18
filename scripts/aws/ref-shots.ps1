# Reference screenshots of the HTML prototype (C:\gv\proto\windows) with headless Edge, one per deep link.
$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
$out = "C:\gv\ref"; Remove-Item $out -Recurse -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $out | Out-Null
$states = "s00","s01","s07","s08","s10","s13","s14","s18c","l1","arc","i1","i0","e1","s01-pick","s01-set","s01-usage","s01-add","s14-payout","i1-direct","s01-traffic","s01-demo","s01-balance","s08-off","s01-tray","s01-menu","s01-nc","s01-closed","s07-pick","s00-connect"
$list = @()
foreach ($s in $states) { foreach ($v in "simple","d") { foreach ($t in "light","dark") { $list += "$s-$v-$t" } } }
foreach ($z in "wide","std","compact","narrow","max") { foreach ($t in "light","dark") { $list += "s01-d-$z-$t"; $list += "s01-simple-$z-$t" } }
foreach ($h in $list) {
  $u = "file:///C:/gv/proto/windows/index.html#$h-static"
  Start-Process $edge -ArgumentList "--headless=new","--disable-gpu","--hide-scrollbars","--window-size=1400,880","--virtual-time-budget=3000","--screenshot=$out\$h.png",$u -Wait
}
"ref shots: " + (Get-ChildItem $out).Count
Compress-Archive -Force -Path "$out\*" -DestinationPath C:\gv\ref.zip
