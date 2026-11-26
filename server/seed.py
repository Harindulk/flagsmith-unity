#!/usr/bin/env python3
"""Seed a fresh self-hosted Flagsmith with the demo project, flags and experiment.

Safe to rerun: everything is looked up by name before it is created.
Usage: python3 server/seed.py [--url http://localhost:8000]
Prints the environment's client-side key and writes it to server/.seed.json.
"""
import argparse
import json
import os
import urllib.error
import urllib.request

EMAIL, PASSWORD = "admin@example.com", "ToggleWars!2026"  # local demo login only
ORG, PROJECT, ENV = "Toggle Wars Demo", "Flag Runner", "Development"

# name -> (type, initial value, enabled, multivariate options)
FLAGS = {
    "run_speed": ("STANDARD", "7", True, None),
    "jump_force": ("STANDARD", "13", True, None),
    "gravity_scale": ("STANDARD", "3.2", True, None),
    "coin_value": ("STANDARD", "1", True, None),
    "player_color": ("STANDARD", "#FFB020", True, None),
    "double_jump": ("STANDARD", None, True, None),
    "spike_walls": ("STANDARD", None, True, None),  # the "buggy feature" kill switch
    "tutorial_style": ("MULTIVARIATE", "text", True, [("hand", 50)]),
}
METRIC_EVENT = "first_run_complete"


def call(base, method, path, body=None, token=None):
    req = urllib.request.Request(base + path, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", f"Token {token}")
    data = json.dumps(body).encode() if body is not None else None
    try:
        with urllib.request.urlopen(req, data, timeout=30) as r:
            raw = r.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        raise SystemExit(f"{method} {path} -> {e.code}: {e.read().decode()[:500]}")


def results(page):
    return page["results"] if isinstance(page, dict) and "results" in page else page


def find(items, **match):
    return next((i for i in items if all(i.get(k) == v for k, v in match.items())), None)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://localhost:8000")
    base = ap.parse_args().url.rstrip("/")

    try:
        token = call(base, "POST", "/api/v1/auth/login/", {"email": EMAIL, "password": PASSWORD})["key"]
    except SystemExit:
        token = call(base, "POST", "/api/v1/auth/users/", {
            "email": EMAIL, "password": PASSWORD, "re_password": PASSWORD,
            "first_name": "Demo", "last_name": "Admin"})["key"]

    org = find(results(call(base, "GET", "/api/v1/organisations/", token=token)), name=ORG) \
        or call(base, "POST", "/api/v1/organisations/", {"name": ORG}, token)
    project = find(results(call(base, "GET", f"/api/v1/projects/?organisation={org['id']}", token=token)), name=PROJECT) \
        or call(base, "POST", "/api/v1/projects/", {"name": PROJECT, "organisation": org["id"]}, token)
    env = find(results(call(base, "GET", f"/api/v1/environments/?project={project['id']}", token=token)), name=ENV) \
        or call(base, "POST", "/api/v1/environments/", {"name": ENV, "project": project["id"]}, token)
    key = env["api_key"]

    features = results(call(base, "GET", f"/api/v1/projects/{project['id']}/features/?page_size=100", token=token))
    for name, (ftype, value, enabled, mv) in FLAGS.items():
        if find(features, name=name):
            continue
        body = {"name": name, "type": ftype, "default_enabled": enabled, "initial_value": value}
        if mv:
            body["multivariate_options"] = [
                {"type": "unicode", "string_value": v, "default_percentage_allocation": pct} for v, pct in mv]
        features.append(call(base, "POST", f"/api/v1/projects/{project['id']}/features/", body, token))

    env_path = f"/api/v1/environments/{key}"
    # Store SDK events in Flagsmith's own Postgres, so /api/v1/events/ accepts them (no outside warehouse).
    builtin = {"source": "flagsmith_database"}
    for conn in results(call(base, "GET", f"{env_path}/warehouse-connections/", token=token)):
        if conn.get("config") != builtin:
            call(base, "DELETE", f"{env_path}/warehouse-connections/{conn['id']}/", token=token)
    if not results(call(base, "GET", f"{env_path}/warehouse-connections/", token=token)):
        call(base, "POST", f"{env_path}/warehouse-connections/",
             {"warehouse_type": "postgres", "name": "Flagsmith database", "config": builtin}, token)

    metric = find(results(call(base, "GET", f"{env_path}/experiment-metrics/", token=token)), name="First run complete") \
        or call(base, "POST", f"{env_path}/experiment-metrics/", {
            "name": "First run complete", "description": "Player finishes their first run",
            "aggregation": "occurrence", "direction": "up",
            "definition": {"version": 1, "event": METRIC_EVENT}}, token)

    feature = find(features, name="tutorial_style")
    for opt in feature["multivariate_options"]:
        if not opt.get("key"):  # key is only writable on the mv-options endpoint
            # Full PUT: a partial PATCH returns a 500 (validate() reads attrs["feature"] and
            # attrs["default_percentage_allocation"] without falling back to the instance).
            call(base, "PUT", f"/api/v1/projects/{project['id']}/features/{feature['id']}/mv-options/{opt['id']}/",
                 {"key": opt["string_value"], "feature": feature["id"], "type": opt["type"],
                  "string_value": opt["string_value"],
                  "default_percentage_allocation": opt["default_percentage_allocation"]}, token)
    experiment = find(results(call(base, "GET", f"{env_path}/experiments/", token=token)), name="Tutorial style")
    if not experiment:
        experiment = call(base, "POST", f"{env_path}/experiments/", {
            "name": "Tutorial style", "feature": feature["id"],
            "hypothesis": "A pointing-hand tutorial gets more new players through their first run than a text hint.",
            # 100% of identities enrolled; 50% see "hand", the rest get the control value "text".
            "experiment_rollout": {
                "enabled": True, "rollout_percentage": 100,
                "feature_state_value": {"type": "string", "value": "text"},
                "multivariate_feature_state_values": [
                    {"multivariate_feature_option": o["id"], "percentage_allocation": 50}
                    for o in feature["multivariate_options"]]}}, token)
        call(base, "POST", f"{env_path}/experiments/{experiment['id']}/metrics/",
             {"metric": metric["id"], "expected_direction": "increase"}, token)
    if experiment.get("status") == "created":
        call(base, "POST", f"{env_path}/experiments/{experiment['id']}/start/", {}, token)

    out = {"api_url": f"{base}/api/v1/", "environment_key": key, "admin_token": token,
           "project_id": project["id"], "experiment_id": experiment["id"]}
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".seed.json"), "w") as f:
        json.dump(out, f, indent=2)
    print(f"Dashboard: {base}  login {EMAIL} / {PASSWORD}")
    print(f"Environment key (client-side, safe to ship): {key}")


if __name__ == "__main__":
    main()
