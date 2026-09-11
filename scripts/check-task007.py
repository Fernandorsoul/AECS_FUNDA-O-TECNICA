import json, os, glob
d = os.path.expandvars(r"%LOCALAPPDATA%\AECS\evidence")
f = os.path.join(d, "30a4c16dd97e4c438f12a5ddfcb39e36.json")
e = json.load(open(f))["evidence"]
print("Task:", e.get("taskContract", {}).get("id"))
print("Decision:", e.get("finalDecision", {}).get("reason", "?")[:300])
print("\nBaseline:")
for v in e.get("baselineVerificationResults", []):
    print(f"  {v.get('verifier')}: status={v.get('status')} msg={v.get('message','')[:100]}")
print("\nVerification:")
for v in e.get("verificationResults", []):
    print(f"  {v.get('verifier')}: status={v.get('status')} msg={v.get('message','')[:150]}")
print("\nChanged files:", e.get("candidateChangeSet", {}).get("changedFiles"))
print("\nAgent output (first 500 chars):")
print(e.get("agentResult", {}).get("stdOut", "")[:500])
