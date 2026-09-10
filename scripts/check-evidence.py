import json, os, glob
evidence_dir = os.path.expandvars(r"%LOCALAPPDATA%\AECS\evidence")
files = sorted(glob.glob(os.path.join(evidence_dir, "*.json")), key=os.path.getmtime, reverse=True)
if not files:
    print("No evidence files")
    exit()
with open(files[0]) as f:
    data = json.load(f)
ev = data.get("evidence", {})
print("=== Acceptance Criteria ===")
for ac in ev.get("acceptanceCriteriaResults", []):
    print(f"  {ac.get('criterionId')}: status={ac.get('status')} msg={ac.get('message','')[:200]}")
print("\n=== Candidate Changeset ===")
cs = ev.get("candidateChangeSet", {})
print(f"  changedFiles: {cs.get('changedFiles')}")
print(f"  hasChanges: {cs.get('hasChanges')}")
print("\n=== Verification Results ===")
for v in ev.get("verificationResults", []):
    print(f"  {v.get('verifier')}: status={v.get('status')} msg={v.get('message','')[:150]}")
print("\n=== Candidate Commands (acceptance) ===")
for c in ev.get("candidateCommands", []):
    if "acceptance" in str(c.get("phase", "")) or "test" in str(c.get("arguments", "")):
        print(f"  {c.get('fileName')} {c.get('arguments')} exit={c.get('exitCode')}")
        if c.get("standardOutput"):
            print(f"    stdout: {c.get('standardOutput','')[:300]}")
        if c.get("standardError"):
            print(f"    stderr: {c.get('standardError','')[:300]}")
