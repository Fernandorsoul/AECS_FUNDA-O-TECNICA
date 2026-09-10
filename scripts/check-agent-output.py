import json, os, glob
evidence_dir = os.path.expandvars(r"%LOCALAPPDATA%\AECS\evidence")
files = sorted(glob.glob(os.path.join(evidence_dir, "*.json")), key=os.path.getmtime, reverse=True)
if not files:
    print("No evidence files")
    exit()
with open(files[0]) as f:
    data = json.load(f)
ev = data.get("evidence", {})
agent = ev.get("agentResult", {})
stdout = agent.get("stdOut", "")
print("=== Agent Output (first 2000 chars) ===")
print(stdout[:2000])
print("\n=== Agent filesChanged ===")
print(agent.get("filesChanged"))
