"""R8-4: down.py treats an instance EC2 no longer lists as gone, then deletes the security group and
sweeps; other describe errors still fail. Fake EC2 and botocore, no AWS.
Run: python3 -I scripts/aws/test_r8_4_down_gone.py"""
import json, os, runpy, sys, tempfile, types

class ClientError(Exception):
    def __init__(self, code): self.response = {"Error": {"Code": code}}

class FakeEc2:
    def __init__(self, describe_code): self.describe_code, self.calls = describe_code, []
    def terminate_instances(self, InstanceIds): raise ClientError("InvalidInstanceID.NotFound")
    def describe_instances(self, InstanceIds=None, Filters=None):
        self.calls.append("describe_instances")
        if InstanceIds: raise ClientError(self.describe_code)
        return {"Reservations": []}
    def delete_security_group(self, GroupId): self.calls.append("delete_sg")
    def describe_security_groups(self, GroupIds): raise ClientError("InvalidGroup.NotFound")

def run(code):
    bc = types.ModuleType("botocore"); bc.exceptions = types.SimpleNamespace(ClientError=ClientError)
    ec2 = FakeEc2(code)
    ac = types.ModuleType("awscommon"); ac.session = lambda: types.SimpleNamespace(client=lambda _: ec2)
    sys.modules.update(botocore=bc, awscommon=ac)
    d = tempfile.mkdtemp()
    json.dump({"instance": "i-1", "sg": "sg-1"}, open(os.path.join(d, "state.json"), "w"))
    sys.argv = ["down.py", d]
    try: runpy.run_path(os.path.join(os.path.dirname(os.path.abspath(__file__)), "down.py"), run_name="__main__")
    except SystemExit as e: return e.code, ec2.calls
    except ClientError as e: return e.response["Error"]["Code"], ec2.calls
    return None, ec2.calls

code, calls = run("InvalidInstanceID.NotFound")
assert code == 0 and calls == ["describe_instances", "delete_sg", "describe_instances"], (code, calls)
code, calls = run("RequestLimitExceeded")
assert code == "RequestLimitExceeded" and "delete_sg" not in calls, (code, calls)
print("ok")
