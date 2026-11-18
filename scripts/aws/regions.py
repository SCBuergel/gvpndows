import sys, os, botocore
sys.path.insert(0, os.path.dirname(__file__))
from awscommon import session
for r in ["us-east-1","us-east-2","us-west-2","eu-west-1","eu-west-2","eu-central-1","eu-north-1","eu-south-1","eu-central-2","ap-southeast-1"]:
    try:
        n = len(session(r).client("ec2").describe_instance_types(InstanceTypes=["t3.large"])["InstanceTypes"])
        print(r, "describe ok")
    except botocore.exceptions.ClientError as e:
        print(r, e.response["Error"]["Code"], "SCP" if "service control policy" in str(e) else str(e)[:150])
s = session("us-east-1"); iam = s.client("iam")
for fn, kw in [("list_attached_user_policies", {"UserName":"perf-test"}), ("list_user_policies", {"UserName":"perf-test"}), ("list_groups_for_user", {"UserName":"perf-test"})]:
    try: print(fn, getattr(iam, fn)(**kw))
    except botocore.exceptions.ClientError as e: print(fn, e.response["Error"]["Code"])
