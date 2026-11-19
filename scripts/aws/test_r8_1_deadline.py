"""R8-1: the on-machine deadline is an absolute UTC time checked at boot and periodically.
Run: python3 -I scripts/aws/test_r8_1_deadline.py"""
import os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from userdata import userdata

ud, deadline = userdata("ssh-ed25519 AAAA test", 1_000_000.7, 6)
assert deadline == 1_000_000 + 6 * 3600, deadline
lines = ud.splitlines()
assert lines[0] == "<powershell>" and lines[1].startswith("shutdown /s /t 21600 "), lines[:2]
check = next(l for l in lines if "gvpndows-deadline.ps1 -Encoding" in l)
assert f"ToUnixTimeSeconds() -ge {deadline}) {{ shutdown /a; shutdown /s /f /t 0 " in check, check
reg = next(l for l in lines if l.startswith("Register-ScheduledTask"))
assert "-User SYSTEM" in reg and "-Trigger $t" in reg, reg
assert "-AtStartup" in ud and "-RepetitionInterval (New-TimeSpan -Minutes 5)" in ud
assert ud.count("{") == ud.count("}") and "{{" not in ud and "Set-Service sshd" in ud
assert re.search(r"administrators_authorized_keys -Value 'ssh-ed25519 AAAA test'", ud)
print("ok")
