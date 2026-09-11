import json, os, glob
d = os.path.expandvars(r"%LOCALAPPDATA%\AECS\evidence")
fs = sorted(glob.glob(os.path.join(d, "*.json")), key=os.path.getmtime, reverse=True)
e = json.load(open(fs[1]))["evidence"]
cmds = [c for c in e.get("baselineCommands", []) if "build" in str(c.get("arguments", ""))]
print(f"Build commands: {len(cmds)}")
for c in cmds:
    print(f"  exit={c.get('exitCode')} dur={c.get('duration','')[:10]}")
