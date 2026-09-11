# AECS Local vs Cloud Comparison

**Date**: 2026-09-11  
**Tasks**: 11 (same set for both)

## Summary

| Metric | Local (Ollama qwen2.5-coder:3b) | Cloud (Groq openai/gpt-oss-20b) |
|---|---|---|
| **VERIFIED** | 4/11 (36%) | 9/11 (82%) |
| **CPVC** | **$0.0033** | $0.0072 |
| **Total cost** | **$0.0131** | $0.0645 |
| **Duration** | 1096s (18 min) | 880s (15 min) |
| **Avg files changed** | 2.1 | 2.1 |
| **Timeouts** | 0 | 0 |

## Optimizations Applied

1. **Improved prompt**: Step-by-step instructions for local model
2. **Increased timeout**: 10 minutes for Ollama HttpClient
3. **Fixed FileApplicator**: Accepts markdown header format (`### path`)

## Detailed Results (Optimized Local)

| Task | Module | Risk | Local | Cloud |
|---|---|---|---|---|
| TASK-001 | Customers | R1 | REJECTED (timeout) | VERIFIED |
| TASK-002 | Customers | R0 | REJECTED (build) | VERIFIED |
| TASK-003 | Customers | R3 | REJECTED (scope) | REJECTED (scope) |
| TASK-004 | Customers | R1 | REJECTED (tests) | VERIFIED |
| TASK-005 | Customers | R2 | VERIFIED | VERIFIED |
| TASK-006 | Customers | R0 | REJECTED | VERIFIED |
| TASK-007 | Orders | R2 | VERIFIED | VERIFIED |
| TASK-008 | Orders | R2 | VERIFIED | VERIFIED |
| TASK-009 | Billing | R0 | VERIFIED | VERIFIED |
| TASK-010 | Billing | R3 | REJECTED | VERIFIED |
| TASK-011 | Customers | R0 | REJECTED | VERIFIED |

## Performance by Risk Level

| Risk | Local | Cloud |
|---|---|---|
| R0 | 1/4 (25%) | 4/4 (100%) |
| R1 | 0/2 (0%) | 2/2 (100%) |
| R2 | 3/3 (100%) | 3/3 (100%) |
| R3 | 0/2 (0%) | 1/2 (50%) |

## Key Findings

### Local (Ollama) Advantages
1. **40% cheaper**: CPVC $0.0043 vs $0.0072
2. **No rate limiting**: Can run unlimited experiments
3. **Data privacy**: Code never leaves local machine
4. **R2 tasks excel**: 100% success on medium-risk refactors

### Local (Ollama) Disadvantages
1. **Lower success rate**: 36% vs 82%
2. **Slower**: 1385s vs 880s (57% slower)
3. **Timeouts**: 2 tasks timed out
4. **R0/R1 struggle**: Low-risk tasks fail often
5. **Less consistent**: More variable results

### Cloud (Groq) Advantages
1. **Higher success rate**: 82% vs 36%
2. **Faster**: 880s vs 1385s
3. **No timeouts**: Reliable execution
4. **Better at simple tasks**: R0/R1 100% success

### Cloud (Groq) Disadvantages
1. **More expensive**: 70% higher CPVC
2. **Rate limiting**: Free tier limits consecutive runs
3. **Data privacy**: Code sent to external API
4. **Requires API key**: Needs Groq account

## Recommendations

### Use Local When:
- Working with sensitive/proprietary code
- Running many experiments (no rate limits)
- Budget is primary concern
- Tasks are medium complexity (R2)

### Use Cloud When:
- Need highest success rate
- Time is critical
- Tasks are simple (R0/R1) or complex (R3)
- Can afford API costs

### Hybrid Approach:
- Use local for initial development/testing
- Use cloud for final verification
- Use local for R2 tasks, cloud for R0/R1/R3

## Cost Analysis

For 100 verified code changes:
- **Local**: $0.43 (100 × $0.0043)
- **Cloud**: $0.72 (100 × $0.0072)
- **Savings**: $0.29 (40%) with local

But local needs 278 attempts (100/36%) vs cloud 122 attempts (100/82%):
- **Local actual cost**: 278 × $0.0015 avg = $0.42
- **Cloud actual cost**: 122 × $0.0059 avg = $0.72

**Conclusion**: Local is still cheaper overall, but requires more attempts.
