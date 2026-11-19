"""Delete everything a run created (instance, security group) and verify it is gone. Idempotent.
Usage: down.py RUN_DIR. Exit 0 only when verified gone."""
import json, os, sys, time, botocore
sys.path.insert(0, os.path.dirname(__file__))
from awscommon import session
state = json.load(open(os.path.join(sys.argv[1], "state.json")))
ec2 = session().client("ec2")
iid = state.get("instance")
if iid:
    try:
        ec2.terminate_instances(InstanceIds=[iid])
        ec2.get_waiter("instance_terminated").wait(InstanceIds=[iid], WaiterConfig={"Delay": 10, "MaxAttempts": 60})
    except botocore.exceptions.ClientError as e:
        if "NotFound" not in e.response["Error"]["Code"]: raise
    try: st = ec2.describe_instances(InstanceIds=[iid])["Reservations"]
    except botocore.exceptions.ClientError as e:  # an id terminated over an hour ago is not listed any more
        if e.response["Error"]["Code"] != "InvalidInstanceID.NotFound": raise
        st = []
    st = st[0]["Instances"][0]["State"]["Name"] if st else "gone"
    print("instance", iid, st)
    if st not in ("terminated", "gone"): print("!!!!! INSTANCE NOT TERMINATED", iid, "https://eu-north-1.console.aws.amazon.com/ec2"); sys.exit(1)
    if "created" in state: print("lifetime_s", int(time.time() - state["created"]))
sg = state.get("sg")
if sg:
    for i in range(30):
        try: ec2.delete_security_group(GroupId=sg); break
        except botocore.exceptions.ClientError as e:
            c = e.response["Error"]["Code"]
            if c == "InvalidGroup.NotFound": break
            if c != "DependencyViolation": raise
            time.sleep(10)
    try: ec2.describe_security_groups(GroupIds=[sg]); print("!!!!! SG STILL THERE", sg); sys.exit(1)
    except botocore.exceptions.ClientError as e: print("security group", sg, "gone" if "NotFound" in e.response["Error"]["Code"] else e)
left = [i["InstanceId"] for r in ec2.describe_instances(Filters=[{"Name": "tag:purpose", "Values": ["gvpndows-test"]},
        {"Name": "instance-state-name", "Values": ["pending", "running", "stopping", "stopped", "shutting-down"]}])["Reservations"] for i in r["Instances"]]
print("tagged instances still alive:", left or "none")
sys.exit(1 if left else 0)
