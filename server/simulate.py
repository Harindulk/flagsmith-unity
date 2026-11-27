"""Sends synthetic players through the tutorial experiment so the results page has data to show.

    python3 simulate.py 400

Every simulated identity is named "sim-..." and every event carries metadata {"synthetic": true},
so it can't be mistaken for real players. Players in the "hand" variant finish their first run a
little more often (62% vs 50%), which gives the dashboard a visible difference to report.
Then the experiment's exposure and result calculations are refreshed.
"""
import json
import random
import sys
import time
import urllib.error
import urllib.request

from flag import SEED, call

API = SEED["api_url"]
ENV = SEED["environment_key"]
EXPERIMENT = SEED["experiment_id"]
FINISH_RATE = {"hand": 0.62, "control": 0.50}


def client(path, body):
    req = urllib.request.Request(API + path, method="POST", data=json.dumps(body).encode(),
                                 headers={"X-Environment-Key": ENV, "Content-Type": "application/json"})
    with urllib.request.urlopen(req) as res:
        return json.loads(res.read() or b"null")


def main(n, first=0):
    rng = random.Random(7 + first)
    now = int(time.time() * 1000)
    events, seen = [], {"hand": 0, "control": 0}
    for i in range(first, first + n):
        identity = f"sim-{i:04d}"
        flag = next(f for f in client("identities/", {"identifier": identity, "traits": []})["flags"]
                    if f["feature"]["name"] == "tutorial_style")
        variant = flag.get("variant")
        if variant not in FINISH_RATE:
            continue
        seen[variant] += 1
        meta = {"synthetic": True, "experiment_id": EXPERIMENT}
        events.append({"event": "$flag_exposure", "feature_name": "tutorial_style", "identifier": identity,
                       "value": variant, "traits": {}, "metadata": meta, "timestamp": now})
        if rng.random() < FINISH_RATE[variant]:
            events.append({"event": "first_run_complete", "feature_name": None, "identifier": identity,
                           "value": None, "traits": {}, "metadata": {"synthetic": True}, "timestamp": now + 1})
    for start in range(0, len(events), 500):
        client("events/", {"events": events[start:start + 500]})
    print(f"Sent {len(events)} synthetic events for {sum(seen.values())} identities: {seen}")
    for what in ("exposures", "results"):
        try:
            call("POST", f"environments/{ENV}/experiments/{EXPERIMENT}/{what}/refresh/", {})
        except urllib.error.HTTPError as e:
            if e.code != 429:  # 429: a refresh was requested moments ago and is still queued
                raise
    print("Refresh requested. Results appear in the dashboard's experiment page after the task processor runs.")


if __name__ == "__main__":
    # python3 simulate.py [count] [first]: a second batch must start after the first, or players are counted twice.
    main(int(sys.argv[1]) if len(sys.argv) > 1 else 400, int(sys.argv[2]) if len(sys.argv) > 2 else 0)
