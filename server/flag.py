"""Read or change a flag in the local demo environment through the Management API.

    python3 flag.py                    list every flag
    python3 flag.py run_speed 12       set a value
    python3 flag.py spike_walls off    switch a flag off (or on)

Uses the admin token that seed.py saved in .seed.json, so run seed.py first.
"""
import json
import os
import sys
import urllib.request

SEED = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".seed.json")))
BASE = SEED["api_url"]
ENV = SEED["environment_key"]


def call(method, path, body=None):
    req = urllib.request.Request(BASE + path, method=method,
                                 headers={"Authorization": "Token " + SEED["admin_token"], "Content-Type": "application/json"},
                                 data=None if body is None else json.dumps(body).encode())
    with urllib.request.urlopen(req) as res:
        raw = res.read()
        return json.loads(raw) if raw else None


def states():
    features = {f["id"]: f["name"] for f in call("GET", f"projects/{SEED['project_id']}/features/?page_size=999")["results"]}
    return {features[s["feature"]]: s for s in call("GET", f"environments/{ENV}/featurestates/?page_size=999")["results"]
            if s["identity"] is None and s["feature_segment"] is None and s["feature"] in features}


def set_flag(name, value):
    state = states()[name]
    body = {"enabled": value == "on"} if value in ("on", "off") else {"feature_state_value": value}
    call("PATCH", f"environments/{ENV}/featurestates/{state['id']}/", body)


if __name__ == "__main__":
    if len(sys.argv) == 3:
        set_flag(sys.argv[1], sys.argv[2])
        print(f"{sys.argv[1]} -> {sys.argv[2]}")
    else:
        for name, s in sorted(states().items()):
            print(f"{name:16} {'on ' if s['enabled'] else 'off'}  {s['feature_state_value']}")
