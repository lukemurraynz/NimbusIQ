# NimbusIQ Architecture

This document provides an overview of the NimbusIQ architecture, its three services, and the ten-agent analysis pipeline.

## System Overview

NimbusIQ consists of three services deployed on Azure Container Apps:

```
┌──────────────────────────────────────────────────────────────────┐
│  Frontend (React + Fluent UI v9)                                  │
│  Service graph · Recommendations · Approval workflow · Timeline   │
└─────────────────────────┬────────────────────────────────────────┘
                          │ REST / JWT (Entra ID)
┌─────────────────────────▼────────────────────────────────────────┐
│  Control Plane API (.NET 10 / ASP.NET Core)                       │
│  Service groups · Analysis runs · Decisions · RFC 9457 errors     │
└──────────┬──────────────────────────────┬────────────────────────┘
           │ PostgreSQL (EF Core)          │ Agent messages
┌──────────▼──────────────────────────────▼────────────────────────┐
│  Agent Orchestrator (.NET 10 background worker)                   │
│  10 specialised agents via Microsoft Agent Framework              │
└──────────────────────────────────────────────────────────────────┘
```

### 1. Frontend

- **Stack**: React, TypeScript, Fluent UI v9
- **Purpose**: Dashboard showing service graph, drift trends, recommendations, and the dual-control approval workflow
- **Communication**: REST API calls to the Control Plane API

### 2. Control Plane API

- **Stack**: ASP.NET Core (.NET 10), Entity Framework Core, PostgreSQL
- **Purpose**: Manages service groups, triggers analysis runs, stores results, and handles the approval workflow
- **API Style**: REST with RFC 9457 Problem Details error responses, `api-version` query parameter versioning

### 3. Agent Orchestrator

- **Stack**: .NET 10 background worker, Microsoft Agent Framework (MAF)
- **Purpose**: Runs the multi-agent analysis pipeline, coordinating 10 specialised agents
- **AI Integration**: Azure AI Foundry (GPT-4) for reasoning agents, Azure MCP for grounding

## The Ten-Agent Pipeline

NimbusIQ uses a hybrid approach: four deterministic agents handle rule evaluation and scoring, while six AI-enhanced agents use Azure AI Foundry (GPT-4) for reasoning, narrative generation, and remediation planning.

### Deterministic Agents

| Agent                        | Responsibility                                                                   |
| ---------------------------- | -------------------------------------------------------------------------------- |
| **ServiceIntelligenceAgent** | Calculates service-group intelligence scores from discovered resources           |
| **BestPracticeEngine**       | Evaluates 700+ rules (WAF, PSRule, AZQR, Architecture Centre) against resources  |
| **DriftDetectionAgent**      | Compares current vs. previous snapshots, produces severity-weighted drift scores |
| **CloudNativeMaturityAgent** | Assesses cloud-native maturity level for the service group                       |

### AI-Enhanced Agents (Azure AI Foundry GPT-4)

| Agent                              | Responsibility                                                                    |
| ---------------------------------- | --------------------------------------------------------------------------------- |
| **WellArchitectedAssessmentAgent** | Azure Well-Architected Framework pillar assessment with narrative explanation     |
| **FinOpsOptimizerAgent**           | Cost optimisation analysis with savings estimates and remediation recommendations |
| **ArchitectureAgent**              | Architecture maturity evaluation and design pattern assessment                    |
| **ReliabilityAgent**               | System reliability, availability, and resilience pattern evaluation               |
| **SustainabilityAgent**            | Environmental impact and carbon efficiency assessment                             |
| **GovernanceNegotiationAgent**     | Mediates cross-agent conflicts and produces governance-approved outcomes          |

### Pipeline Flow

1. **Discovery** — Resource Graph, Cost Management, and Log Analytics queries gather the current estate state
2. **Grounding** — Azure MCP and Learn MCP build contextual knowledge for downstream agents
3. **Deterministic Evaluation** — BestPracticeEngine, ServiceIntelligence, DriftDetection, and CloudNativeMaturity run rule-based assessments
4. **AI Reasoning** — WAF, FinOps, Architecture, Reliability, and Sustainability agents reason over the rule results using GPT-4
5. **Governance Negotiation** — GovernanceNegotiationAgent mediates conflicts between agent recommendations
6. **Output** — Synthesised results with recommendations, evidence, and confidence scores

## Key Design Decisions

### Hybrid Agent Architecture

Not everything needs an LLM. Drift scoring, best-practice rule evaluation, and cloud-native maturity checks are deterministic operations where consistent, reproducible results matter. The AI agents handle subjective reasoning: explaining trade-offs, generating narratives, and producing remediation code.

### Dual-Control Approval Workflow

All remediation actions require human approval through an idempotent state machine. NimbusIQ generates IaC and presents it for review — it never applies changes automatically.

### A2A Lineage Tracing

Every agent-to-agent message carries `LineageMetadata` with decision path, contributing agents, and confidence score. This makes all decisions replayable and auditable.

### Managed Identity Everywhere

No connection strings or secrets in configuration. All service-to-service authentication uses `DefaultAzureCredential` with RBAC role assignments defined in Bicep.

### OpenTelemetry Instrumentation

The entire agent pipeline is instrumented with OpenTelemetry. Every agent step, every Foundry call, every MCP tool invocation gets a trace with correlation IDs.

## Infrastructure

Defined in Bicep using Azure Verified Modules (AVM):

- **Azure Container Apps** — all three services
- **Azure Container Registry** — private image registry
- **PostgreSQL Flexible Server** — persistent storage
- **Azure Key Vault** — secrets management
- **Log Analytics** — centralised logging
- **Azure AI Foundry** — GPT-4 model deployment (optional)
- **Managed Identities** — least-privilege RBAC
- **Virtual Network** — private networking (optional)

## Diagrams

- [atlas-architecture.drawio](diagrams/atlas-architecture.drawio) — Full system architecture
- [nimbusiq-hackathon-submission.drawio](diagrams/nimbusiq-hackathon-submission.drawio) — Hackathon submission overview
- [foundry-hosted-agents-architecture.drawio](diagrams/foundry-hosted-agents-architecture.drawio) — Agent hosting architecture
- [foundry-hosted-agents-runtime-sequence.drawio](diagrams/foundry-hosted-agents-runtime-sequence.drawio) — Agent runtime sequence flow
