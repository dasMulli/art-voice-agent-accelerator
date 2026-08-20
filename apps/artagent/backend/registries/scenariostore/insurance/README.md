# Insurance Scenario - Multi-Agent Voice System

## Business Overview

This scenario demonstrates a **multi-agent insurance voice system** that handles both **B2B** and **B2C** callers through intelligent routing and specialized agents.

### Business Value

| Capability | Business Impact |
|------------|-----------------|
| **24/7 Automated Service** | Reduce call center costs, handle overflow |
| **B2B Subrogation Hotline** | Faster inter-company claim resolution |
| **Policy Self-Service** | Customers get instant answers without hold times |
| **FNOL Intake** | Structured claim collection, fewer errors |
| **Intelligent Routing** | Right agent for the right caller type |

## Agent Architecture

```
                    ┌───────────────┐
                    │   AuthAgent   │  ← Entry Point (Authentication Gate)
                    └───────┬───────┘
                            │
         ┌──────────────────┼──────────────────┐
         │                  │                  │
         ▼                  ▼                  ▼
   ┌───────────┐      ┌───────────┐      ┌───────────┐
   │  Policy   │ ◄──► │   FNOL    │      │   Subro   │
   │  Advisor  │      │   Agent   │      │   Agent   │
   └───────────┘      └───────────┘      └───────────┘
        │                  │                  │
        └────── B2C ───────┘                  │
         (Policyholders)               B2B (CC Reps)
```

### Agent Roles

| Agent | Caller Type | Purpose |
|-------|-------------|---------|
| **AuthAgent** | All | Greet, identify caller type, authenticate, route |
| **PolicyAdvisor** | B2C | Answer policy questions, coverage inquiries |
| **FNOLAgent** | B2C | File new insurance claims (accidents, losses) |
| **SubroAgent** | B2B | Handle inter-company subrogation inquiries |

## 🎯 Test Scenarios

### ⭐ Scenario: Golden Path B2B Workflow (RECOMMENDED)

> **Persona**: Jennifer Martinez from Contoso Insurance calling about a subrogation demand. This scenario tests the **complete B2B workflow** with all 6 key inquiries.

#### Setup
1. Create demo profile: `scenario=insurance`, `role=cc_rep`, `test_scenario=golden_path`
2. Claim number: `CLM-2024-1234`
3. Caller: Jennifer Martinez, Contoso Insurance

#### Complete Workflow Script

| # | User Question | Expected Response | Tool |
|---|---------------|-------------------|------|
| 1 | "I'm calling about claim CLM-2024-1234" | Asks for company + name | — |
| 2 | "Jennifer Martinez, Contoso Insurance" | Verifies CC access, hands off | `verify_cc_caller` → `handoff_subro_agent` |
| **3** | **"Is coverage confirmed for this claim?"** | "Coverage is confirmed on this claim." | `get_coverage_status` |
| **4** | **"Has liability been accepted? What's the range?"** | "Liability has been accepted at 80%." | `get_liability_decision` |
| **5** | **"Does the demand exceed policy limits?"** | "No limits issue. Your demand ($45,000) is within the $100,000 PD limit." | `get_pd_policy_limits` |
| **6** | **"Have any payments been made on the PD feature?"** | "1 payment totaling $15,000.00." | `get_pd_payments` |
| **7** | **"Has my subrogation demand been received? When will it be assigned?"** | "Received on Oct 20th for $45,000. Currently under review by Sarah Johnson." | `get_subro_demand_status` |
| **8** | **"Can this be rushed due to attorney involvement or statute concerns?"** | Agent MUST ask about each criterion before evaluating | See Rush Flow below |

#### Rush Criteria Flow (Step 8)

**BUSINESS RULE: At least TWO criteria must be met to qualify for ISRUSH.**

Rush Criteria:
1. Out-of-pocket expenses (rental, deductible) involved
2. Third call for same demand
3. Attorney involvement or suit filed
4. DOI complaint filed
5. Statute of limitations within 60 days

The agent **MUST ask about criteria** before calling `evaluate_rush_criteria`:

```
Agent: "I can check if this qualifies for rush handling. A few quick questions:
        Is there attorney involvement or has a suit been filed?"
User:  "Yes, there's an attorney involved."
Agent: "Is the statute of limitations coming up within 60 days?"
User:  "Yes, about 45 days left."
Agent: "Are there out-of-pocket expenses, like rental or deductible? 
        Has a DOI complaint been filed? Is this your third call on this demand?"
User:  "No to those."
→ Agent calls: evaluate_rush_criteria(attorney_represented=true, statute_near=true, 
                                       oop_expenses=false, doi_complaint=false, 
                                       prior_demands_unanswered=false)
→ Result: 2 criteria met (attorney + statute) = QUALIFIES
→ Agent calls: create_isrush_diary(...)
Agent: "I've flagged this for rush handling. Two criteria met: attorney involvement and 
        statute near. You'll see assignment within 2 business days."
```

#### Expected Tool Outputs (CLM-2024-1234)

| Tool | Key Output Values |
|------|-------------------|
| `get_coverage_status` | `coverage_status: "confirmed"`, `has_cvq: false` |
| `get_liability_decision` | `liability_decision: "accepted"`, `liability_percentage: 80` |
| `get_pd_policy_limits` | `pd_limits: 100000`, `demand_amount: 43847.52`, `demand_exceeds_limits: false` |
| `get_pd_payments` | `payment_count: 1`, `total_paid: 14832.00` |
| `get_subro_demand_status` | `demand_received: true`, `amount: 43847.52`, `assigned_to: "Sarah Johnson"`, `status: "under_review"` |
| `evaluate_rush_criteria` | `qualifies_for_rush: true` (if attorney OR statute criteria met), auto-validates call history (3 prior calls = 4th call qualifies) |

#### Business Rules Verified
- ✅ CC company must match claim's claimant_carrier
- ✅ Coverage can be disclosed immediately
- ✅ Liability percentage disclosed (lower end only: "80%", not "80-100%")
- ✅ Policy limits only disclosed AFTER liability accepted
- ✅ Demand amount auto-fetched from claim record (no need to ask caller)
- ✅ Rush criteria: **at least 2 criteria required** to qualify for ISRUSH
- ✅ Rush criteria: agent MUST gather criteria before calling tool
- ✅ `escalation_request` alone does NOT count toward the 2-criteria minimum
- ✅ Call history auto-validated: 3+ prior calls auto-qualifies for "third call" criterion

---

### Scenario A: B2B Subrogation Demand Status

> **Persona**: Sarah from Progressive Insurance calling about a claim where her customer was hit by our insured.

#### Setup
1. Create demo profile: `scenario=insurance`, `role=cc_rep`, `test_scenario=demand_under_review`
2. Note the claim number (e.g., `CLM-2024-001234`)

#### Script

| Turn | Caller Says | Agent Does | Tool Triggered |
|------|-------------|------------|----------------|
| 1 | "Hi, I'm calling about claim CLM-2024-001234" | Asks for company and name | — |
| 2 | "Contoso Insurance, Sarah Johnson" | Verifies CC access | `verify_cc_caller` ✓ |
| 3 | — | Hands off to SubroAgent | `handoff_subro_agent` |
| 4 | "What's the status of our demand?" | Retrieves demand info | `get_subro_demand_status` ✓ |
| 5 | "Has liability been determined?" | Checks liability | `get_liability_decision` ✓ |
| 6 | "What are the policy limits?" | Checks if can disclose | `get_pd_policy_limits` ✓ |
| 7 | "Any payments made?" | Checks payments | `get_pd_payments` ✓ |
| 8 | "Thanks, that's all" | Offers email summary | — |
| 9 | "Yes, sarah@contoso.com" | Closes & sends email | `close_and_document_call` ✓ |

#### Expected Responses (test_scenario: demand_under_review)
- **Demand**: Received 11/20 for $12,500, under review by Sarah Johnson
- **Liability**: Pending - still under investigation
- **Limits**: "Can't disclose until liability is accepted"
- **Payments**: None

#### Business Rules Tested
- ✅ CC company must match claim's claimant_carrier
- ✅ Policy limits only disclosed after liability acceptance
- ✅ All interactions documented with request/response details
- ✅ Optional email confirmation sent to CC rep

### Scenario B: B2B Rush Escalation Request

> **Persona**: Mike from Fabrikam Insurance calling about an attorney-represented claim that needs expediting.

#### Setup
1. Create demo profile: `scenario=insurance`, `role=cc_rep`, `test_scenario=demand_paid`
2. Claim `CLM-2024-005678` has liability accepted at 80%

#### Script

| Turn | Caller Says | Agent Does | Tool Triggered |
|------|-------------|------------|----------------|
| 1 | "Claim CLM-2024-005678, Fabrikam, Mike" | Verifies | `verify_cc_caller` ✓ |
| 2 | "I need this expedited - claimant has an attorney" | Asks about other criteria | — |
| 3 | "Is the statute coming up?" | Agent gathers all criteria | — |
| 4 | "Yes, within 60 days" | Evaluates rush | `evaluate_rush_criteria` ✓ |
| 5 | — | Creates rush diary | `create_isrush_diary` ✓ |
| 6 | "That's all, thanks" | Offers email summary | — |
| 7 | "No email needed" | Closes & documents | `close_and_document_call` ✓ |

#### Rush Criteria (MUST gather ALL before evaluating)
- 🔴 Attorney represented / suit filed?
- 🔴 Statute of limitations within 60 days?
- 🔴 Out-of-pocket expenses (rental, deductible)?
- 🔴 DOI complaint filed?
- 🔴 Prior demands unanswered?

#### Business Rules Tested
- ✅ Agent gathers ALL rush criteria before calling evaluate_rush_criteria
- ✅ ISRUSH diary created when criteria met
- ✅ Call documented with rush_status in key_responses

### Scenario C: B2C Policy Coverage Inquiry

> **Persona**: Alice, a policyholder, calling to check if she has roadside assistance.

#### Setup
1. Create demo profile: `scenario=insurance`, `role=policyholder`
2. Note the SSN4 (e.g., `1234`)

#### Script

| Turn | Caller Says | Agent Does | Tool Triggered |
|------|-------------|------------|----------------|
| 1 | "Hi, I need to check my coverage" | Asks for name + SSN4 | — |
| 2 | "Alice Brown, last four 1234" | Verifies identity | `verify_client_identity` ✓ |
| 3 | "Do I have roadside assistance?" | Searches policy | `search_policy_info` ✓ |
| 4 | "What's my deductible for collision?" | Queries deductible | `search_policy_info` ✓ |
| 5 | "What cars are on my policy?" | Lists vehicles | `search_policy_info` ✓ |

#### Business Rules Tested
- ✅ Must authenticate before accessing policy data
- ✅ Natural language policy queries supported
- ✅ Returns data specific to caller's policies

### Scenario D: B2C First Notice of Loss (FNOL)

> **Persona**: Bob, a policyholder, calling to report a car accident.

#### Script

| Turn | Caller Says | Agent Does | Tool Triggered |
|------|-------------|------------|----------------|
| 1 | "I was in an accident and need to file a claim" | Verifies identity first | `verify_client_identity` ✓ |
| 2 | — | Routes to FNOLAgent | Handoff |
| 3-12 | [Collects all 10 FNOL fields] | Guides through intake | — |
| 13 | [Confirms all details] | Records the claim | `record_fnol` ✓ |

#### FNOL Fields Collected
1. Driver identification
2. Vehicle details (year, make, model)
3. Number of vehicles involved
4. Incident description
5. Loss date/time
6. Loss location
7. Vehicle drivable status
8. Passengers
9. Injury assessment
10. Trip purpose

### Scenario E: Multi-Claim Inquiry (B2B)

> **Persona**: CC rep from Contoso checking on multiple claims in one call.

#### Setup
1. Create demo profile with `test_scenario=demand_under_review` (CLM-2024-001234, Contoso)
2. Also test with CLM-2024-007890 (Woodgrove - different CC, should fail switch)

#### Script

| Turn | Caller Says | Agent Does | Tool Triggered |
|------|-------------|------------|----------------|
| 1 | "Claim CLM-2024-001234, Contoso, John" | Verifies first claim | `verify_cc_caller` ✓ |
| 2 | "What's the demand status?" | Retrieves data | `get_subro_demand_status` ✓ |
| 3 | "I have another claim: CLM-2024-005678" | Checks if same CC | `switch_claim` ✓ |
| 4 | — | (If same CC) Switches seamlessly | — |
| 5 | "What's the status on this one?" | Gets second claim info | `get_subro_demand_status` ✓ |
| 6 | "One more: CLM-2024-007890" | Tries to switch | `switch_claim` ✗ |
| 7 | — | Different CC - denied | "Call back to verify separately" |

#### Business Rules Tested
- ✅ `switch_claim` allows switching within same CC company
- ✅ Different CC company requires separate call/verification
- ✅ Final `close_and_document_call` captures all claims discussed

## 🔧 Tools Reference

### Authentication Tools (auth.py)

| Tool | Scenario | Purpose |
|------|----------|---------|
| `verify_cc_caller` | B2B | Verify CC rep by claim + company match |
| `verify_client_identity` | B2C | Verify policyholder by name + SSN4 |

### Subrogation Tools (subro.py)

| Tool | Returns |
|------|---------|
| `get_claim_summary` | Parties, loss date, status |
| `get_subro_demand_status` | Demand amount, handler, status |
| `get_liability_decision` | Accepted/denied, percentage |
| `get_coverage_status` | Confirmed, pending, CVQ |
| `get_pd_policy_limits` | PD limits (if liability > 0) |
| `get_pd_payments` | Payment history, totals |
| `evaluate_rush_criteria` | Qualifies for ISRUSH? |
| `create_isrush_diary` | Rush diary entry |
| `append_claim_note` | Simple call note (legacy) |
| `close_and_document_call` | **Close call + summary + optional email** |
| `switch_claim` | Switch to different claim (same CC) |
| `resolve_feature_owner` | Handler for PD/BI/SUBRO |
| `get_subro_contact_info` | Fax/phone numbers |

### Policy Tools (policy.py)

| Tool | Returns |
|------|---------|
| `search_policy_info` | Natural language query results |
| `get_policy_limits` | Coverage limits by type |
| `get_policy_deductibles` | Deductible amounts |
| `list_user_policies` | All policies for user |
| `list_user_claims` | All claims for user |

### FNOL Tools (fnol.py)

| Tool | Returns |
|------|---------|
| `record_fnol` | Claim ID, confirmation |
| `handoff_to_general_info_agent` | Route non-claim inquiries |


## 📊 System Capabilities Summary

| Capability | How It's Demonstrated |
|------------|----------------------|
| **Multi-Agent Orchestration** | AuthAgent → SubroAgent/PolicyAdvisor/FNOLAgent |
| **B2B Authentication** | Claim ownership + company verification |
| **B2C Authentication** | Name + SSN4 verification |
| **Real-Time Data Access** | Live Cosmos DB queries during calls |
| **Business Rule Enforcement** | Liability required before limits disclosure |
| **Escalation Workflows** | ISRUSH criteria evaluation + diary creation |
| **Audit Trail** | Detailed call documentation with request/response details |
| **Email Confirmation** | Optional summary email to CC reps via `close_and_document_call` |
| **Multi-Claim Support** | `switch_claim` for same-CC claim switching |
| **Natural Language Queries** | Policy questions without structured input |
| **Structured Data Collection** | FNOL 10-field intake process |

## 🧪 Test Scenarios (MOCK_CLAIMS)

| `test_scenario` | Claim | CC Company | Edge Case |
|-----------------|-------|------------|------------|
| ⭐ `golden_path` | CLM-2024-1234 | Contoso | **Full B2B workflow**: coverage ✓, liability 80%, limits $100k, payment $14,832, demand $43,847.52 |
| `demand_under_review` | CLM-2024-001234 | Contoso | Liability pending, demand under review |
| `demand_paid` | CLM-2024-005678 | Fabrikam | 80% liability, demand paid |
| `no_demand` | CLM-2024-009012 | Northwind | No demand received, coverage pending |
| `coverage_denied` | CLM-2024-003456 | Tailspin | Policy lapsed, coverage denied |
| `pending_assignment` | CLM-2024-007890 | Woodgrove | Demand in queue, not assigned |
| `liability_denied` | CLM-2024-002468 | Litware | 0% liability, demand denied |
| `cvq_open` | CLM-2024-013579 | Proseware | Coverage question open |
| `demand_exceeds_limits` | CLM-2024-024680 | Lucerne | $85k demand vs $25k limits |
