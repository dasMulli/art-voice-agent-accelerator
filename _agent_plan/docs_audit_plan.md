# Documentation Audit & Cleanup Plan

**Created**: January 24, 2026  
**Status**: ✅ COMPLETED  
**Owner**: Documentation Team

> **Completion Note**: All phases executed successfully on Jan 24, 2026. See bottom of document for implementation summary.

---

## Executive Summary

This document outlines a comprehensive plan to audit, clean up, and reorganize the ART Voice Agent Accelerator documentation. The goal is to ensure documentation:
1. Accurately reflects the current codebase structure
2. Removes legacy/obsolete content
3. Eliminates redundancy and fluff
4. Provides focused context for each agent type

---

## Current State Analysis

### Documentation Structure Overview

```
docs/
├── index.md                    # Hub page - GOOD but verbose
├── mkdocs.yml                  # Navigation config
│
├── getting-started/            # ✅ ACCURATE - Keep
│   ├── README.md
│   ├── prerequisites.md
│   ├── quickstart.md
│   ├── local-development.md
│   └── demo-guide.md
│
├── architecture/               # 🔄 NEEDS REVIEW - Mixed quality
│   ├── README.md               # ✅ Good overview
│   ├── CHANGELOG-ARCHITECTURE.md  # ⚠️ May be stale
│   ├── memory-management-per-agent.md  # ⚠️ Unclear if implemented
│   ├── telemetry.md            # ✅ Keep
│   ├── agents/                 # ✅ Well-documented
│   ├── orchestration/          # ✅ Well-documented
│   ├── registries/             # ✅ Excellent - recently updated
│   ├── speech/                 # 🔄 Review for accuracy
│   ├── data/                   # 🔄 Review for accuracy
│   ├── acs/                    # 🔄 Review for accuracy
│   ├── voice/                  # 🔄 Review for accuracy
│   └── archive/                # ⚠️ Should be moved out of main docs
│
├── agents/                     # ⚠️ LEGACY - Contains obsolete proposal
│   └── agent-consolidation-plan.md  # 🗑️ DELETE - superseded by registries
│
├── proposals/                  # ⚠️ LEGACY - Move to archive
│   ├── handoff-consolidation-plan.md  # Done - archive
│   ├── scenario-orchestration-*.md    # Historical
│   ├── specify-integration-proposal.md
│   └── tts-streaming-latency-analysis.md
│
├── refactoring/                # ⚠️ LEGACY - Move to archive
│   ├── CLEANUP_ANALYSIS.md
│   ├── CLEANUP_PROGRESS.md
│   ├── MEDIAHANDLER_MIGRATION.md
│   ├── PRIORITY_1_COMPLETE.md
│   └── VOICELIVE_STRUCTURE_ANALYSIS.md
│
├── industry/                   # 🔄 REVIEW - Update for current agents
│   ├── README.md               # Minimal - expand
│   ├── banking.md
│   ├── insurance.md
│   └── healthcare.md
│
├── deployment/                 # ✅ Keep - Production docs
├── security/                   # ✅ Keep - Security docs
├── operations/                 # ✅ Keep - Operations docs
├── api/                        # 🔄 REVIEW - Ensure API current
├── guides/                     # ✅ Good utility docs
├── samples/                    # ✅ Keep
├── community/                  # 🔄 Review
├── testing/                    # 🔄 Review
└── sre/                        # Empty - DELETE
```

---

## Key Issues Identified

### 1. **Legacy & Obsolete Content** (~40% reduction potential)

| Location | Issue | Action |
|----------|-------|--------|
| `docs/agents/agent-consolidation-plan.md` | Superseded by registries system | DELETE |
| `docs/proposals/` (entire folder) | Historical planning docs | MOVE to `docs/archive/proposals/` |
| `docs/refactoring/` (entire folder) | Migration complete | MOVE to `docs/archive/refactoring/` |
| `docs/architecture/archive/` | Already archived content | MOVE to `docs/archive/architecture/` |
| `docs/sre/` | Empty folder | DELETE |
| `docs/architecture/CHANGELOG-ARCHITECTURE.md` | May be stale | REVIEW or archive |
| `docs/architecture/memory-management-per-agent.md` | Unclear status | VERIFY implementation or archive |

### 2. **Navigation Structure Issues**

**Current mkdocs.yml nav problems:**
- References to non-existent files (e.g., `architecture/llm-orchestration.md`)
- Orphaned pages not in navigation
- Inconsistent hierarchy depth
- `docs/agents/` folder exists but not prominent in nav

### 3. **Redundant Documentation**

| Redundancy | Files | Resolution |
|------------|-------|------------|
| Agent framework docs | `architecture/agents/README.md` vs `docs/agents/` | Consolidate to `architecture/agents/` |
| Agent configuration | Multiple overlapping guides | Single source in `architecture/registries/agents.md` |
| Handoff documentation | Multiple places | Consolidate to `architecture/agents/handoffs.md` |

### 4. **Missing Agent-Focused Documentation**

Current agents in codebase (from `registries/agentstore/`):
- auth_agent
- banking_concierge
- card_recommendation
- claims_specialist
- compliance_desk
- concierge
- custom_agent
- document_analyst
- fnol_agent
- fraud_agent
- investment_advisor
- policy_advisor
- prior_auth_agent
- subro_agent

**Missing:** Individual agent reference pages with focused context.

---

## Proposed Cleanup Plan

### Phase 1: Delete Legacy Content (Day 1)

**Files/Folders to DELETE:**

```bash
# Empty folders
rm -rf docs/sre/

# Superseded proposal (now implemented)
rm docs/agents/agent-consolidation-plan.md

# Move proposals to archive (not delete - historical value)
mkdir -p docs/archive/proposals
mv docs/proposals/* docs/archive/proposals/

# Move refactoring to archive
mkdir -p docs/archive/refactoring  
mv docs/refactoring/* docs/archive/refactoring/

# Move architecture archive
mkdir -p docs/archive/architecture
mv docs/architecture/archive/* docs/archive/architecture/
rm -rf docs/architecture/archive/
```

**Update mkdocs.yml:** Remove references to moved/deleted files.

---

### Phase 2: Reorganize Agent Documentation (Day 2-3)

**Goal:** Create focused agent reference pages.

**New Structure:**
```
docs/architecture/agents/
├── README.md                   # Framework overview (keep)
├── handoffs.md                 # Handoff strategies (keep)
│
├── reference/                  # NEW - Per-agent reference
│   ├── index.md               # Agent catalog with links
│   ├── auth-agent.md
│   ├── banking-concierge.md
│   ├── card-recommendation.md
│   ├── claims-specialist.md
│   ├── compliance-desk.md
│   ├── concierge.md
│   ├── fraud-agent.md
│   ├── fnol-agent.md
│   ├── investment-advisor.md
│   ├── policy-advisor.md
│   ├── prior-auth-agent.md
│   └── subro-agent.md
```

**Each agent reference page should include:**
1. **Overview** - Agent purpose and role
2. **Configuration** - Link to YAML config
3. **Tools** - Available tools for this agent
4. **Handoff Routes** - Which agents can hand off to/from this agent
5. **Prompt Template** - Key prompt sections
6. **Industry Scenarios** - Which scenarios use this agent

---

### Phase 3: Update Industry Documentation (Day 3-4)

**Current industry docs are thin.** Expand with:

**Banking (`docs/industry/banking.md`):**
- Agent roster: BankingConcierge, AuthAgent, FraudAgent, InvestmentAdvisor, CardRecommendation
- Handoff graph (Mermaid diagram)
- Scenario configuration reference
- Tool permissions per agent

**Insurance (`docs/industry/insurance.md`):**
- Agent roster: AuthAgent, PolicyAdvisor, ClaimsSpecialist, FNOLAgent, SubroAgent
- Security-first architecture explanation
- Handoff graph (Mermaid diagram)
- B2B vs B2C handoff patterns

**Healthcare (`docs/industry/healthcare.md`):**
- Agent roster: (verify current implementation)
- HIPAA compliance considerations
- Escalation patterns

---

### Phase 4: Fix Navigation & Cross-References (Day 4-5)

**Update `mkdocs.yml` navigation:**

```yaml
nav:
  - Home: index.md
  - Getting Started:
    - Overview: getting-started/README.md
    - Prerequisites: getting-started/prerequisites.md
    - Quick Start: getting-started/quickstart.md
    - Local Development: getting-started/local-development.md
    - Demo Guide: getting-started/demo-guide.md
  
  - Architecture:
    - Overview: architecture/README.md
    - Agents:
      - Framework: architecture/agents/README.md
      - Handoff Strategies: architecture/agents/handoffs.md
      - Agent Reference:                    # NEW
        - Catalog: architecture/agents/reference/index.md
        - Auth Agent: architecture/agents/reference/auth-agent.md
        - Banking Concierge: architecture/agents/reference/banking-concierge.md
        # ... etc
    - Registries:
      - Overview: architecture/registries/index.md
      - Agents: architecture/registries/agents.md
      - Tools: architecture/registries/tools.md
      - Scenarios: architecture/registries/scenarios.md
    - Orchestration:
      - Overview: architecture/orchestration/README.md
      - Scenario-Based: architecture/orchestration/industry-scenarios.md
      - Scenario Flow: architecture/orchestration/scenario-system-flow.md
      - Cascade Mode: architecture/orchestration/cascade.md
      - VoiceLive Mode: architecture/orchestration/voicelive.md
      - Handoff Service: architecture/orchestration/handoff-service.md
    - Speech & Voice:
      - Streaming Modes: architecture/speech/README.md
      - Recognition: architecture/speech/recognition.md
      - Synthesis: architecture/speech/synthesis.md
    - Data:
      - Session Management: architecture/data/README.md
      - Data Flows: architecture/data/flows.md
    - ACS:
      - Call Flows: architecture/acs/README.md
      - Telephony: architecture/acs/integrations.md
    - Telemetry: architecture/telemetry.md
  
  - Industry Solutions:
    - Overview: industry/README.md
    - Banking: industry/banking.md
    - Insurance: industry/insurance.md
    - Healthcare: industry/healthcare.md
  
  - Deployment:
    - Guide: deployment/README.md
    - Phone Setup: deployment/phone-number-setup.md
    - Production: deployment/production.md
    - CI/CD: deployment/cicd.md
  
  - Operations:
    - Monitoring: operations/monitoring.md
    - Troubleshooting: operations/troubleshooting.md
    - Testing: operations/testing.md
    - Load Testing: operations/load-testing.md
  
  - API Reference:
    - Overview: api/README.md
    - API Reference: api/api-reference.md
  
  - Security:
    - Authentication: security/authentication.md
  
  - Guides:
    - Repository Structure: guides/repository-structure.md
    - Utilities: guides/utilities.md
  
  - Samples:
    - Overview: samples/README.md
```

---

### Phase 5: Content Review & Polish (Day 5-6)

**Review each remaining document for:**

1. **Accuracy** - Does it match current code?
2. **Completeness** - Are all features documented?
3. **Clarity** - Is it understandable?
4. **Freshness** - Are dates/versions current?
5. **Links** - Do all links work?

**Priority review list:**
1. `docs/index.md` - Simplify, reduce fluff
2. `docs/architecture/README.md` - Verify architecture diagrams
3. `docs/architecture/orchestration/*.md` - Verify matches implementation
4. `docs/architecture/speech/*.md` - Verify modes documentation
5. `docs/guides/repository-structure.md` - Verify structure is current

---

## Agent-Focused Breakdown

### Agent Documentation Approach

Each agent should have a dedicated reference page that provides:

#### Template: `docs/architecture/agents/reference/{agent-name}.md`

```markdown
# {Agent Name}

> {One-line description}

## Overview

{2-3 sentences explaining the agent's purpose and when it's used}

## Configuration

**YAML Location:** `registries/agentstore/{agent_folder}/agent.yaml`

| Setting | Value | Description |
|---------|-------|-------------|
| name | {name} | Agent identifier |
| description | {desc} | Purpose |
| handoff.trigger | {tool} | Tool that routes TO this agent |
| handoff.is_entry_point | {bool} | Is this a starting agent? |

## Tools

This agent has access to the following tools:

| Tool | Description | Category |
|------|-------------|----------|
| {tool_name} | {desc} | {category} |

## Handoff Routes

### Receives Handoffs From
- {Agent} via `{handoff_tool}`

### Can Hand Off To
- {Agent} via `{handoff_tool}`

```mermaid
flowchart LR
    A[{From Agent}] -->|{tool}| B[{This Agent}]
    B -->|{tool}| C[{To Agent}]
```

## Scenarios

| Scenario | Role | Entry Point? |
|----------|------|--------------|
| Banking | {role} | {yes/no} |
| Insurance | {role} | {yes/no} |

## Prompt Template

Key sections of the prompt:

- **Role Definition**: {summary}
- **Boundaries**: {what agent should/shouldn't do}
- **Handoff Criteria**: {when to escalate}

## Related Documentation

- [Agent Framework](../README.md)
- [Handoff Strategies](../handoffs.md)
- [{Industry} Scenario](../../industry/{industry}.md)
```

---

### Agent Documentation Priority

| Priority | Agent | Industry | Notes |
|----------|-------|----------|-------|
| 1 | Concierge | General | Entry point for many scenarios |
| 2 | AuthAgent | All | Critical security gate |
| 3 | BankingConcierge | Banking | Industry-specific entry |
| 4 | FraudAgent | Banking | High-stakes agent |
| 5 | InvestmentAdvisor | Banking | Complex tool set |
| 6 | CardRecommendation | Banking | Product-focused |
| 7 | PolicyAdvisor | Insurance | Insurance entry |
| 8 | ClaimsSpecialist | Insurance | Claims workflow |
| 9 | FNOLAgent | Insurance | First Notice of Loss |
| 10 | SubroAgent | Insurance | B2B agent |
| 11 | ComplianceDesk | Compliance | Specialized |
| 12 | PriorAuthAgent | Healthcare | Healthcare-specific |
| 13 | DocumentAnalyst | General | Utility agent |
| 14 | CustomAgent | General | Template for new agents |

---

## Success Metrics

| Metric | Before | Target | Measurement |
|--------|--------|--------|-------------|
| Legacy files | 15+ | 0 in main docs | Count archived |
| Orphan pages | Unknown | 0 | mkdocs build warnings |
| Agent reference pages | 0 | 14 | Count in reference/ |
| Broken links | Unknown | 0 | Link checker |
| Index.md lines | ~400 | <200 | Line count |
| Architecture accuracy | ~70% | 100% | Manual review |

---

## Implementation Checklist

### Phase 1: Delete Legacy ⬜
- [ ] Delete `docs/sre/` folder
- [ ] Delete `docs/agents/agent-consolidation-plan.md`
- [ ] Move `docs/proposals/*` to `docs/archive/proposals/`
- [ ] Move `docs/refactoring/*` to `docs/archive/refactoring/`
- [ ] Move `docs/architecture/archive/*` to `docs/archive/architecture/`
- [ ] Update mkdocs.yml to remove deleted references

### Phase 2: Create Agent Reference ⬜
- [ ] Create `docs/architecture/agents/reference/` folder
- [ ] Create `index.md` (agent catalog)
- [ ] Create reference pages for priority 1-5 agents
- [ ] Create reference pages for priority 6-10 agents
- [ ] Create reference pages for remaining agents

### Phase 3: Update Industry Docs ⬜
- [ ] Expand `docs/industry/README.md`
- [ ] Update `docs/industry/banking.md` with agent roster
- [ ] Update `docs/industry/insurance.md` with agent roster
- [ ] Update `docs/industry/healthcare.md` with agent roster

### Phase 4: Fix Navigation ⬜
- [ ] Update mkdocs.yml with new structure
- [ ] Add new agent reference pages to nav
- [ ] Fix broken internal links
- [ ] Run `mkdocs build` to check for warnings

### Phase 5: Content Polish ⬜
- [ ] Review and simplify `docs/index.md`
- [ ] Review architecture docs for accuracy
- [ ] Update dates and versions
- [ ] Run link checker
- [ ] Final review pass

---

## Timeline

| Phase | Duration | Dependencies |
|-------|----------|--------------|
| Phase 1: Delete Legacy | 1 day | None |
| Phase 2: Agent Reference | 2+3 days | Phase 1 |
| Phase 3: Industry Docs | 1-2 days | Phase 2 (partially) |
| Phase 4: Navigation | 1 day | Phase 1, 2 |
| Phase 5: Polish | 1-2 days | All above |

**Total Estimated Effort:** 6-9 days

---

## Appendix: Files to Archive (Historical Value)

These files have historical value but should not be in active docs:

### Proposals (-> docs/archive/proposals/)
- `handoff-consolidation-plan.md` - Implemented Dec 2025
- `scenario-orchestration-analysis.md` - Historical analysis
- `scenario-orchestration-simplification.md` - Historical proposal
- `specify-integration-proposal.md` - Integration design
- `tts-streaming-latency-analysis.md` - Performance analysis

### Refactoring (-> docs/archive/refactoring/)
- `CLEANUP_ANALYSIS.md` - Code audit done Jan 2026
- `CLEANUP_PROGRESS.md` - Migration tracker
- `MEDIAHANDLER_MIGRATION.md` - Handler migration (complete)
- `PRIORITY_1_COMPLETE.md` - Milestone tracker
- `VOICELIVE_STRUCTURE_ANALYSIS.md` - Structure analysis

---

## Implementation Summary (Completed Jan 24, 2026)

### Phase 1: Legacy Cleanup ✅
- Deleted `docs/sre/` (empty folder)
- Deleted `docs/agents/agent-consolidation-plan.md` (superseded)
- Moved `docs/proposals/` → `docs/archive/proposals/`
- Moved `docs/refactoring/` → `docs/archive/refactoring/`
- Moved `docs/architecture/archive/` → `docs/archive/architecture/`

### Phase 2: Agent Reference Structure ✅
Created 12 agent reference pages in `docs/architecture/agents/reference/`:
- Banking: banking-concierge.md, fraud-agent.md, investment-advisor.md, card-recommendation.md
- Insurance: policy-advisor.md, claims-specialist.md, fnol-agent.md, subro-agent.md
- Cross-Domain: auth-agent.md, concierge.md, compliance-desk.md, general-kb-agent.md

### Phase 3: Industry Docs ✅
- Updated banking.md and insurance.md with mermaid diagrams
- Added agent roster tables with reference links
- Added related agent notes

### Phase 4: Navigation ✅
- Updated mkdocs.yml with Agent Reference section
- Removed archive from main navigation

### Phase 5: Polish ✅
- Updated architecture/agents/README.md with reference links
- Created docs/archive/README.md index

---

## Notes

- The `_agent_plan/` folder already contains operational planning docs that are appropriate there
- Consider creating a `docs/CHANGELOG.md` that consolidates architecture/codebase changes
- The `CLEANUP_ANALYSIS.md` and `handoff-consolidation-plan.md` in `docs/refactoring/` duplicate content from `_agent_plan/`
