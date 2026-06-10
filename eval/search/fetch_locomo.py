#!/usr/bin/env python3
"""Fetch the LoCoMo benchmark into the eval data dir (not vendored — license-clean).

LoCoMo (snap-research/locomo): 10 multi-session conversations with human QA whose
`evidence` dia_ids pin the ground-truth session. We download rather than commit it,
so the public repo stays free of third-party dataset redistribution.
"""
import os, sys, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.environ.get("EVAL_DATA", os.path.join(HERE, "data"))
os.makedirs(DATA, exist_ok=True)
URL = "https://raw.githubusercontent.com/snap-research/locomo/main/data/locomo10.json"
dst = os.path.join(DATA, "locomo10.json")

if os.path.exists(dst):
    print(f"already present: {dst} ({os.path.getsize(dst)} bytes)")
    sys.exit(0)
print(f"downloading {URL}")
urllib.request.urlretrieve(URL, dst)
print(f"saved {dst} ({os.path.getsize(dst)} bytes)")
