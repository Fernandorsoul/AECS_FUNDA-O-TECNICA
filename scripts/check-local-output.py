import json, os, glob
d = os.path.expandvars(r"%LOCALAPPDATA%\AECS\evidence")
fs = sorted(glob.glob(os.path.join(d, "*.json")), key=os.path.getmtime, reverse=True)
for f in fs[:5]:
    e = json.load(open(f)).get("evidence", {})
    tid = e.get("taskContract", {}).get("id", "?")
    stdout = e.get("agentResult", {}).get("stdOut", "")
    files = e.get("candidateChangeSet", {}).get("changedFiles", [])
    print(f"{tid}: {len(files)} files, stdout length={len(stdout)}")
    if len(stdout) > 0 and len(files) == 0:
        print(f"  First 200 chars: {stdout[:200]}")
    print()
