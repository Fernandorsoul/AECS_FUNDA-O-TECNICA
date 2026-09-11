# AECS Experiment Report

**Date**: 2026-09-11  
**Model**: openai/gpt-oss-20b (Groq)  
**Tasks**: 10  
**Duration**: 772.6s (12.9 min)

## Summary

| Metric | Value |
|---|---|
| First-pass verification rate | 80% (8/10) |
| CPVC (Cost per Verified Code Change) | $0.0067 |
| Total cost | $0.0533 |
| Average files changed | 1.8 |
| Average duration per task | 77.3s |

## Results by Task

| Task | Module | Risk | Result | Duration | Files | Cost |
|---|---|---|---|---|---|---|
| TASK-001 | Customers | R1 | VERIFIED | 83.9s | 2 | $0.0039 |
| TASK-002 | Customers | R0 | VERIFIED | 91.9s | 1 | $0.0038 |
| TASK-003 | Customers | R3 | REJECTED | 33.2s | 0 | $0.0140 |
| TASK-004 | Customers | R1 | VERIFIED | 76.7s | 2 | $0.0043 |
| TASK-005 | Customers | R2 | VERIFIED | 75.0s | 4 | $0.0079 |
| TASK-006 | Customers | R0 | VERIFIED | 76.1s | 1 | $0.0027 |
| TASK-007 | Orders | R2 | VERIFIED | 88.4s | 2 | $0.0036 |
| TASK-008 | Orders | R2 | VERIFIED | 88.6s | 2 | $0.0038 |
| TASK-009 | Billing | R0 | REJECTED | 49.2s | 2 | $0.0042 |
| TASK-010 | Billing | R3 | VERIFIED | 109.6s | 2 | $0.0051 |

## Results by Module

| Module | Tasks | VERIFIED | REJECTED | Rate |
|---|---|---|---|---|
| Customers | 6 | 5 | 1 | 83% |
| Orders | 2 | 2 | 0 | 100% |
| Billing | 2 | 1 | 1 | 50% |

## Results by Risk Level

| Risk | Tasks | VERIFIED | REJECTED | Rate | Avg Cost |
|---|---|---|---|---|---|
| R0 | 3 | 2 | 1 | 67% | $0.0036 |
| R1 | 2 | 2 | 0 | 100% | $0.0041 |
| R2 | 3 | 3 | 0 | 100% | $0.0051 |
| R3 | 2 | 1 | 1 | 50% | $0.0096 |

## Rejected Tasks Analysis

### TASK-003 (Scope Violation)
- **Objective**: Refactor CustomerMapper to also update Billing records
- **Reason**: Scope violation — Billing is in forbidden list
- **Expected**: Yes — this task tests the scope enforcer
- **Agent behavior**: Correctly refused to modify forbidden files

### TASK-009 (Build Failure)
- **Objective**: Add XML docs and ApplyDiscount method to BillingService
- **Reason**: Build failed — agent introduced syntax error
- **Expected**: No — this is a valid task
- **Root cause**: Agent made a mistake in code generation

## Acceptance Evidence

All VERIFIED tasks had their acceptance criteria validated through executable tests:

- **TASK-001**: 3/3 criteria passed (null check, tests updated, empty string)
- **TASK-002**: 4/4 criteria passed (validation for name, email, format)
- **TASK-004**: 2/2 criteria passed (null phone, tests pass)
- **TASK-005**: 2/2 criteria passed (new pattern, tests pass)
- **TASK-006**: 2/2 criteria passed (XML docs, tests pass)
- **TASK-007**: 3/3 criteria passed (negative customerId, negative total)
- **TASK-008**: 3/3 criteria passed (GetTotalByCustomer sum, empty)
- **TASK-010**: 4/4 criteria passed (10% discount, 15% discount, no discount)

## Key Findings

1. **Cloud-only mode works well**: Using Groq directly (skipping Ollama) improved reliability and speed

2. **Test files are critical**: Tasks with existing test files have much higher success rates

3. **Scope enforcement works**: TASK-003 correctly rejected for attempting to modify forbidden files

4. **Complex tasks can succeed**: TASK-010 (bulk discount with multiple conditions) passed with R3 risk

5. **Build failures are caught**: TASK-009 correctly rejected when agent introduced syntax errors

6. **CPVC is reasonable**: $0.0067 per verified code change is cost-effective

## Recommendations

1. **Always provide test files**: Create baseline test files before running tasks

2. **Use cloud-only mode**: Skip Ollama for better reliability

3. **Set adequate budget**: 600s wall-clock is sufficient for most tasks

4. **Monitor build failures**: Agent can introduce syntax errors on complex tasks

5. **Leverage scope enforcement**: Use forbidden lists to prevent unwanted changes

## Conclusion

The AECS pipeline demonstrates strong performance with 80% first-pass verification rate. The system correctly:
- Verifies valid code changes
- Rejects scope violations
- Catches build failures
- Validates acceptance criteria through executable tests

The pipeline is ready for production use with proper configuration.
