import json, os, glob
d = os.path.expandvars(r"%LOCALAPPDATA%\AECS\evidence")
fs = sorted(glob.glob(os.path.join(d, "*.json")), key=os.path.getmtime, reverse=True)
for f in fs[:15]:
    e = json.load(open(f)).get("evidence", {})
    tid = e.get("taskContract", {}).get("id", "?")
    if tid == "TASK-009":
        print(e.get("agentResult", {}).get("stdOut", ""))
        break
