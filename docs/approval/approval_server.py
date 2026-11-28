"""A tiny MCP server with one tool, used as Claude Code's --permission-prompt-tool for the recorded run.
A command that carries FLAGSMITH_YES=1 (an approved write) opens a macOS dialog and waits for a person to click.
Everything is logged to approvals.jsonl with timestamps."""
import json, os, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(HERE, "approvals.jsonl")


def log(**row):
    row["at"] = time.strftime("%H:%M:%S")
    open(LOG, "a").write(json.dumps(row) + "\n")


def ask_person(command):
    text = ("Recording for the Flagsmith demo.\n\nThe coding agent wants to run this in the Development environment:\n\n"
            + command + "\n\nApprove it?")
    script = ('with timeout of 900 seconds\ntell application "System Events"\nactivate\ndisplay dialog ' + json.dumps(text) +
              ' with title "Claude Code: approve a Flagsmith change?" buttons {"Deny", "Approve"} default button "Deny" giving up after 600\nend tell\nend timeout')
    t0 = time.time()
    subprocess.run(["osascript", "-e", 'display notification "Click Approve or Deny in the dialog" with title "Flagsmith demo: the agent is waiting for you" sound name "Glass"'])
    shot = subprocess.Popen(["sh", os.path.join(HERE, "shot.sh")])
    out = subprocess.run(["osascript", "-e", script], capture_output=True, text=True).stdout
    shot.wait()
    waited = round(time.time() - t0, 1)
    if "button returned:Approve" in out:
        return True, "a person clicked Approve", waited
    if "button returned:Deny" in out and "gave up:true" not in out:
        return False, "a person clicked Deny", waited
    return False, "no answer: the dialog closed after %s s without a click" % waited, waited


def decide(args):
    tool, inp = args.get("tool_name"), args.get("input") or {}
    cmd = inp.get("command", "") if tool == "Bash" else ""
    if "FLAGSMITH_YES=1" in cmd or "FLAGSMITH_CONFIRM_ENV" in cmd:
        ok, why, waited = ask_person(cmd)
        log(tool=tool, command=cmd, decision="allow" if ok else "deny", by=why, waited_s=waited)
        return {"behavior": "allow", "updatedInput": inp} if ok else {"behavior": "deny", "message": "The user did not approve this write: " + why}
    # Reads pass without a dialog. Anything that could write, and is not an approved script call, is refused.
    writes = (" update", " create", " delete", " set ", "rm ", "mv ", ">", "curl", "wget", " -X", "sudo", "git ", "login", "logout", "init")
    if tool == "Bash" and cmd and not any(w in cmd for w in writes):
        log(tool=tool, command=cmd, decision="allow", by="rule: read-only command")
        return {"behavior": "allow", "updatedInput": inp}
    log(tool=tool, input=inp, decision="deny", by="rule: not read-only and not an approved write")
    return {"behavior": "deny", "message": "Refused by the user's client: only read-only commands and writes made through the skill's script are allowed here."}


for line in sys.stdin:
    try:
        msg = json.loads(line)
    except ValueError:
        continue
    mid, method = msg.get("id"), msg.get("method")
    if method == "initialize":
        res = {"protocolVersion": msg["params"].get("protocolVersion", "2024-11-05"), "capabilities": {"tools": {}},
               "serverInfo": {"name": "approval", "version": "1"}}
    elif method == "tools/list":
        res = {"tools": [{"name": "approve", "description": "Asks the person at the computer to approve a tool call.",
                          "inputSchema": {"type": "object", "properties": {"tool_name": {"type": "string"}, "input": {"type": "object"},
                                                                         "tool_use_id": {"type": "string"}}, "required": ["tool_name", "input"]}}]}
    elif method == "tools/call":
        res = {"content": [{"type": "text", "text": json.dumps(decide(msg["params"].get("arguments") or {}))}]}
    elif mid is None:
        continue
    else:
        res = {}
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": mid, "result": res}) + "\n")
    sys.stdout.flush()
