#!/usr/bin/env python3
"""Generate AECS experiment metrics dashboard."""
import json
import os
import glob
from collections import defaultdict
from datetime import datetime

def load_evidence():
    """Load all evidence files."""
    d = os.path.expandvars(r"%LOCALAPPDATA%\AECS\evidence")
    files = glob.glob(os.path.join(d, "*.json"))
    results = []
    for f in files:
        try:
            with open(f) as fh:
                data = json.load(fh)
            e = data.get("evidence", {})
            task_id = e.get("taskContract", {}).get("id", "?")
            decision = e.get("finalDecision", {}).get("decision", -1)
            # decision: 0=Verified, 1=Rejected, 2=HumanReview
            decision_str = {0: "VERIFIED", 1: "REJECTED", 2: "HUMAN_REVIEW"}.get(decision, "UNKNOWN")
            duration = e.get("budgetUsage", {}).get("wallClockElapsed", "00:00:00")
            cost = e.get("agentRun", {}).get("estimatedCost", 0)
            files_changed = len(e.get("candidateChangeSet", {}).get("changedFiles", []))
            risk = e.get("taskContract", {}).get("constraints", {}).get("securityRisk", 0)
            results.append({
                "task_id": task_id,
                "decision": decision_str,
                "duration": duration,
                "cost": cost,
                "files_changed": files_changed,
                "risk": risk,
                "file": os.path.basename(f)
            })
        except Exception:
            pass
    return results

def format_duration(td_str):
    """Format timedelta string to seconds."""
    try:
        parts = td_str.split(":")
        if len(parts) == 3:
            h, m, s = parts
            return int(h) * 3600 + int(m) * 60 + float(s)
    except Exception:
        pass
    return 0

def generate_dashboard(results):
    """Generate markdown dashboard."""
    print("=" * 60)
    print("AECS EXPERIMENT DASHBOARD")
    print("=" * 60)
    print()
    
    # Overall stats
    total = len(results)
    verified = sum(1 for r in results if r["decision"] == "VERIFIED")
    rejected = sum(1 for r in results if r["decision"] == "REJECTED")
    rate = (verified / total * 100) if total > 0 else 0
    total_cost = sum(r["cost"] for r in results)
    avg_cost = total_cost / total if total > 0 else 0
    cpvc = total_cost / verified if verified > 0 else 0
    
    print(f"Total experiments: {total}")
    print(f"VERIFIED: {verified} ({rate:.1f}%)")
    print(f"REJECTED: {rejected}")
    print(f"Total cost: ${total_cost:.4f}")
    print(f"Average cost: ${avg_cost:.4f}")
    print(f"CPVC: ${cpvc:.4f}")
    print()
    
    # By task
    print("BY TASK:")
    print("-" * 60)
    task_stats = defaultdict(lambda: {"verified": 0, "rejected": 0, "cost": 0})
    for r in results:
        tid = r["task_id"]
        if r["decision"] == "VERIFIED":
            task_stats[tid]["verified"] += 1
        else:
            task_stats[tid]["rejected"] += 1
        task_stats[tid]["cost"] += r["cost"]
    
    for tid in sorted(task_stats.keys()):
        s = task_stats[tid]
        total_runs = s["verified"] + s["rejected"]
        v_rate = (s["verified"] / total_runs * 100) if total_runs > 0 else 0
        print(f"  {tid}: {s['verified']}/{total_runs} verified ({v_rate:.0f}%) cost=${s['cost']:.4f}")
    print()
    
    # By risk
    print("BY RISK LEVEL:")
    print("-" * 60)
    risk_stats = defaultdict(lambda: {"verified": 0, "rejected": 0, "cost": 0})
    for r in results:
        risk = f"R{r['risk']}"
        if r["decision"] == "VERIFIED":
            risk_stats[risk]["verified"] += 1
        else:
            risk_stats[risk]["rejected"] += 1
        risk_stats[risk]["cost"] += r["cost"]
    
    for risk in sorted(risk_stats.keys()):
        s = risk_stats[risk]
        total_runs = s["verified"] + s["rejected"]
        v_rate = (s["verified"] / total_runs * 100) if total_runs > 0 else 0
        print(f"  {risk}: {s['verified']}/{total_runs} verified ({v_rate:.0f}%) cost=${s['cost']:.4f}")
    print()
    
    # Recent results
    print("RECENT RESULTS (last 10):")
    print("-" * 60)
    for r in results[-10:]:
        print(f"  {r['task_id']}: {r['decision']} ({r['files_changed']} files, ${r['cost']:.4f})")

if __name__ == "__main__":
    results = load_evidence()
    generate_dashboard(results)
