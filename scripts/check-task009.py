import json, os, glob
d = os.path.expandvars(r"%LOCALAPPDATA%\AECS\evidence")
fs = sorted(glob.glob(os.path.join(d, "*.json")), key=os.path.getmtime, reverse=True)
for f in fs[:15]:
    e = json.load(open(f)).get("evidence", {})
    tid = e.get("taskContract", {}).get("id", "?")
    if tid == "TASK-009":
        print("Found TASK-009:", os.path.basename(f))
        print("Decision:", e.get("finalDecision", {}).get("reason", "?")[:300])
        print("\nVerification:")
        for v in e.get("verificationResults", []):
            print(f"  {v.get('verifier')}: status={v.get('status')} msg={v.get('message','')[:200]}")
        print("\nChanged files:", e.get("candidateChangeSet", {}).get("changedFiles"))
        print("\nAgent output (first 800 chars):")
        print(e.get("agentResult", {}).get("stdOut", "")[:800])
        break
