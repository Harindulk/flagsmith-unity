"""Tests for everything outside Unity: the Agent Skill script, the docs site build and the demo server helper.

    python3 -m pip install markdown==3.7
    python3 -m unittest discover tests -v

The Unity package has its own tests in com.flagsmith.unity/Tests (see CONTRIBUTING.md).
"""
import importlib
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "skills" / "flagsmith-unity" / "scripts" / "experiment.sh"

# Stands in for the Flagsmith CLI: logs every call (and the body of the "complete" request) and answers the few reads.
FAKE_CLI = r"""#!/usr/bin/env bash
echo "$*" >> "$FAKE_LOG"
case "$*" in
  *"config --json --jq .environment.value"*) echo "${FAKE_ENV_KEY-envkey}" ;;
  *"config --json --jq .environment.name"*) echo "$FAKE_ENV_NAME" ;;
  *"experiments/?page_size"*) echo "${FAKE_EXPERIMENT-7 running}" ;;
  *"variant list"*) echo 42 ;;
  *complete/*) cat >> "$FAKE_LOG"; echo '{"status":"completed"}' ;;
esac
"""


class ExperimentScript(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        cli = Path(self.dir.name) / "flagsmith"
        cli.write_text(FAKE_CLI)
        cli.chmod(0o755)
        self.log = Path(self.dir.name) / "calls.log"
        self.log.write_text("")

    def run_script(self, *args, env_name="Development", **env):
        full = {"PATH": self.dir.name + os.pathsep + os.environ["PATH"], "FAKE_LOG": str(self.log), "FAKE_ENV_NAME": env_name, **env}
        return subprocess.run(["bash", str(SCRIPT), *args], capture_output=True, text=True, env=full)

    def test_rejects_names_that_could_inject_into_queries(self):
        for bad in ('a"b', "a;b", "a b", "$(id)"):
            r = self.run_script("status", bad)
            self.assertEqual(r.returncode, 1, bad)
            self.assertIn("must use only", r.stderr)
        self.assertEqual(self.log.read_text(), "", "the CLI must not be called with a bad name")

    def test_stops_when_no_environment_is_bound(self):
        r = self.run_script("status", "tutorial_style", FAKE_ENV_KEY="")
        self.assertEqual(r.returncode, 1)
        self.assertIn("no environment bound", r.stderr)

    def test_writes_need_explicit_approval(self):
        r = self.run_script("ship", "tutorial_style", "control")
        self.assertEqual(r.returncode, 1)
        self.assertIn("nothing changed", r.stderr)
        self.assertNotIn("complete/", self.log.read_text())

    def test_production_needs_the_exact_environment_name(self):
        for name in ("Production", "LIVE", "prd-eu"):
            r = self.run_script("ship", "tutorial_style", "control", env_name=name, FLAGSMITH_YES="1")
            self.assertEqual(r.returncode, 1, name)
            self.assertIn("looks like production", r.stderr)
            r = self.run_script("ship", "tutorial_style", "control", env_name=name, FLAGSMITH_YES="1", FLAGSMITH_CONFIRM_ENV="production")
            self.assertEqual(r.returncode, 1, "a near-miss confirmation must not pass for " + name)
        self.assertNotIn("complete/", self.log.read_text())
        r = self.run_script("ship", "tutorial_style", "control", env_name="Production", FLAGSMITH_YES="1", FLAGSMITH_CONFIRM_ENV="Production")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("complete/", self.log.read_text())

    def test_ship_sends_the_winner(self):
        r = self.run_script("ship", "tutorial_style", "control", FLAGSMITH_YES="1")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("experiments/7/complete/", self.log.read_text())
        self.assertIn('{"rollout_decision":"control"}', self.log.read_text())
        r = self.run_script("ship", "tutorial_style", "hand", FLAGSMITH_YES="1")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn('{"rollout_decision":"variant","rollout_variant":42}', self.log.read_text())

    def test_ship_needs_a_running_experiment(self):
        r = self.run_script("ship", "tutorial_style", "control", FLAGSMITH_YES="1", FAKE_EXPERIMENT="")
        self.assertEqual(r.returncode, 1)
        self.assertIn("no running experiment", r.stderr)


class SiteBuild(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT / "site"))
        cls.build = importlib.import_module("build")
        cls.build.main()
        cls.out = cls.build.OUT

    def test_headings_inside_code_fences_do_not_split_pages(self):
        pages = self.build.split_guide("# T\n\n## One\ntext\n```\n## not a heading\n```\n## Two words!\nmore\n")
        self.assertEqual([p["title"] for p in pages], ["One", "Two words!"])
        self.assertEqual([p["file"] for p in pages], ["index.html", "two-words.html"])
        self.assertIn("## not a heading", pages[0]["md"])

    def test_every_local_link_and_image_resolves(self):
        broken = []
        for page in self.out.rglob("*.html"):
            for target in re.findall(r'(?:href|src)="([^"#?]+)', page.read_text(encoding="utf-8")):
                if re.match(r"[a-z]+:|//", target):
                    continue
                if target.startswith("/flagsmith-unity/"):  # 404.html links from the site root
                    path = self.out / target[len("/flagsmith-unity/"):]
                else:
                    path = page.parent / target
                if not (path / "index.html" if path.is_dir() else path).exists():
                    broken.append(f"{page.relative_to(self.out)} -> {target}")
        self.assertEqual(broken, [])

    def test_agent_files_cover_every_docs_page(self):
        docs = {p.name for p in (self.out / "docs").glob("*.html")}
        listed = set(re.findall(r"docs/([a-z0-9-]+\.html)", (self.out / "llms.txt").read_text(encoding="utf-8")))
        self.assertEqual(listed, docs)
        self.assertIn("skills/flagsmith-unity/SKILL.md", (self.out / "llms-full.txt").read_text(encoding="utf-8"))


class FlagHelper(unittest.TestCase):
    def test_integer_flags_stay_integers(self):
        seed = {"api_url": "http://x/", "environment_key": "e", "admin_token": "t", "project_id": 1}
        sys.path.insert(0, str(ROOT / "server"))
        with mock.patch("builtins.open", mock.mock_open(read_data=json.dumps(seed))):
            flag = importlib.import_module("flag")
        current = {"jump_force": {"id": 1, "feature_state_value": 13}, "run_speed": {"id": 2, "feature_state_value": "7"},
                   "spike_walls": {"id": 3, "feature_state_value": None}}
        with mock.patch.object(flag, "states", return_value=current), mock.patch.object(flag, "call") as call:
            flag.set_flag("jump_force", "15")
            flag.set_flag("run_speed", "12")
            flag.set_flag("spike_walls", "off")
        self.assertEqual([c.args[2] for c in call.call_args_list],
                         [{"feature_state_value": 15}, {"feature_state_value": "12"}, {"enabled": False}])


if __name__ == "__main__":
    unittest.main()
