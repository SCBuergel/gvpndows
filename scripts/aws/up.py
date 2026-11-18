"""Create one Windows Server test instance in eu-north-1, reachable by SSH (key auth) from this
machine's public IP only. Usage: up.py RUN_DIR [hours_on_machine_deadline]
Writes RUN_DIR/state.json as soon as each resource exists, so down.py can always clean up.
Safeguard on the machine itself: it shuts down after N hours and shutdown = terminate."""
import json, os, subprocess, sys, time, urllib.request
sys.path.insert(0, os.path.dirname(__file__))
from awscommon import session, TAG

run_dir = sys.argv[1]; hours = float(sys.argv[2]) if len(sys.argv) > 2 else 6
itype = os.environ.get("ITYPE", "t3.large"); image_pat = os.environ.get("IMAGE", "Windows_Server-2025-English-Full-Base-*")
os.makedirs(run_dir, mode=0o700, exist_ok=True)
state_path = os.path.join(run_dir, "state.json")
state = json.load(open(state_path)) if os.path.exists(state_path) else {}
def save(): json.dump(state, open(state_path, "w"), indent=1)
name = os.path.basename(os.path.abspath(run_dir))
key = os.path.join(run_dir, "id_ed25519")
if not os.path.exists(key):
    subprocess.run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-C", name, "-f", key], check=True)
pub = open(key + ".pub").read().strip()
my_ip = urllib.request.urlopen("https://api.ipify.org", timeout=10).read().decode().strip()

ec2 = session().client("ec2")
tags = [{"Key": "Name", "Value": name}, {"Key": "purpose", "Value": TAG}]
t0 = time.time()
vpc = ec2.describe_vpcs(Filters=[{"Name": "isDefault", "Values": ["true"]}])["Vpcs"][0]["VpcId"]
sg = ec2.create_security_group(GroupName=name, Description="gvpndows test ssh", VpcId=vpc,
        TagSpecifications=[{"ResourceType": "security-group", "Tags": tags}])["GroupId"]
state["sg"] = sg; save()
ec2.authorize_security_group_ingress(GroupId=sg, IpPermissions=[{"IpProtocol": "tcp", "FromPort": 22, "ToPort": 22,
        "IpRanges": [{"CidrIp": f"{my_ip}/32", "Description": "this machine"}]}])
img = sorted(ec2.describe_images(Owners=["amazon"], Filters=[{"Name": "name", "Values": [image_pat]}])["Images"],
        key=lambda i: i["CreationDate"])[-1]
secs = int(hours * 3600)
userdata = f"""<powershell>
shutdown /s /t {secs} /c "gvpndows-test deadline"
$ErrorActionPreference = 'Continue'
if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {{ Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 }}
Set-Content -Path C:\\ProgramData\\ssh\\administrators_authorized_keys -Value '{pub}' -Encoding ascii -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force C:\\ProgramData\\ssh | Out-Null
Set-Content -Path C:\\ProgramData\\ssh\\administrators_authorized_keys -Value '{pub}' -Encoding ascii
icacls C:\\ProgramData\\ssh\\administrators_authorized_keys /inheritance:r /grant "*S-1-5-32-544:F" /grant "SYSTEM:F"
New-ItemProperty -Path HKLM:\\SOFTWARE\\OpenSSH -Name DefaultShell -Value C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -PropertyType String -Force
Set-Service sshd -StartupType Automatic; Start-Service sshd
New-NetFirewallRule -Name sshd-gvpndows -DisplayName 'OpenSSH (gvpndows)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -ErrorAction SilentlyContinue
</powershell>"""
r = ec2.run_instances(ImageId=img["ImageId"], InstanceType=itype, MinCount=1, MaxCount=1,
        SecurityGroupIds=[sg], UserData=userdata, InstanceInitiatedShutdownBehavior="terminate",
        MetadataOptions={"HttpTokens": "required"},
        BlockDeviceMappings=[{"DeviceName": img["RootDeviceName"], "Ebs": {"VolumeSize": 30, "VolumeType": "gp3", "DeleteOnTermination": True}}],
        TagSpecifications=[{"ResourceType": t, "Tags": tags} for t in ("instance", "volume")])
iid = r["Instances"][0]["InstanceId"]; state["instance"] = iid; state["image"] = img["Name"]; state["type"] = itype
state["created"] = time.time(); save()
print("STEP create", int(time.time() - t0), iid, img["Name"], itype, flush=True)
ec2.get_waiter("instance_running").wait(InstanceIds=[iid])
ip = ec2.describe_instances(InstanceIds=[iid])["Reservations"][0]["Instances"][0]["PublicIpAddress"]
state["ip"] = ip; save()
print("STEP running", int(time.time() - t0), ip, flush=True)
ssh = ["ssh", "-i", key, "-o", "StrictHostKeyChecking=accept-new", "-o", f"UserKnownHostsFile={run_dir}/known_hosts",
       "-o", "ConnectTimeout=8", "-o", "BatchMode=yes", f"Administrator@{ip}"]
for i in range(120):
    if subprocess.run(ssh + ["echo ssh-ok"], capture_output=True, text=True).stdout.strip() == "ssh-ok":
        print("STEP ssh", int(time.time() - t0), flush=True); break
    time.sleep(10)
else:
    print("ssh never came up"); sys.exit(2)
