"""Shared AWS session: credentials from ~/.config/secret-store/aws-key (two lines, 'label: value').
Never prints the values."""
import os, boto3

KEY_FILE = os.path.expanduser("~/.config/secret-store/aws-key")
REGION = os.environ.get("AWS_REGION", "eu-north-1")
TAG = "gvpndows-test"

def session(region=REGION):
    vals = [l.rsplit(":", 1)[-1].strip() for l in open(KEY_FILE).read().splitlines() if l.strip()]
    kid = next(v for v in vals if v.startswith(("AKIA", "ASIA")))
    secret = next(v for v in vals if v != kid)
    return boto3.Session(aws_access_key_id=kid, aws_secret_access_key=secret, region_name=region)
