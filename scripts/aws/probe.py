"""Harmless permission probe: identity, catalogue, create+delete a key pair, delete of a made-up id."""
import sys, botocore
sys.path.insert(0, __import__("os").path.dirname(__file__))
from awscommon import session, REGION
s = session()
ident = s.client("sts").get_caller_identity()
print("account", ident["Account"], "arn", ident["Arn"].split(":")[-1])
ec2 = s.client("ec2")
imgs = ec2.describe_images(Owners=["amazon"], Filters=[{"Name": "name", "Values": ["Windows_Server-2022-English-Full-Base-*"]}])["Images"]
imgs.sort(key=lambda i: i["CreationDate"])
print("windows 2022 images", len(imgs), "latest", imgs[-1]["ImageId"], imgs[-1]["Name"] if imgs else None)
offs = ec2.describe_instance_type_offerings(Filters=[{"Name": "instance-type", "Values": ["t3.large", "t3.xlarge", "m6i.large"]}])["InstanceTypeOfferings"]
print("offered", sorted({o["InstanceType"] for o in offs}))
print("running instances", sum(len(r["Instances"]) for r in ec2.describe_instances(Filters=[{"Name": "instance-state-name", "Values": ["pending","running","stopping","stopped"]}])["Reservations"]))
def dry(fn, **kw):
    try: fn(DryRun=True, **kw)
    except botocore.exceptions.ClientError as e: return e.response["Error"]["Code"]
print("dryrun RunInstances:", dry(ec2.run_instances, ImageId=imgs[-1]["ImageId"], InstanceType="t3.large", MinCount=1, MaxCount=1))
print("dryrun TerminateInstances:", dry(ec2.terminate_instances, InstanceIds=["i-0123456789abcdef0"]))
print("dryrun AuthorizeSG:", dry(ec2.create_security_group, GroupName="gvpndows-probe", Description="probe"))
kp = ec2.create_key_pair(KeyName="gvpndows-probe")
ec2.delete_key_pair(KeyName="gvpndows-probe")
print("key pair create+delete ok")
try:
    q = s.client("service-quotas").get_service_quota(ServiceCode="ec2", QuotaCode="L-1216C47A")
    print("on-demand standard vCPU quota", q["Quota"]["Value"])
except Exception as e: print("quota read:", type(e).__name__)
