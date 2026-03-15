# NimbusIQ - AI Cloud Architect

> **AI Dev Days Hackathon submission** | Category: Best Multi-Agent System + Best Enterprise Solution

NimbusIQ is an AI-powered cloud intelligence platform that continuously discovers, scores, and evolves Azure service estates using a ten-agent analysis pipeline powered by [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/overview/?pivots=programming-language-csharp&WT.mc_id=AZ-MVP-5004796) and [Microsoft Foundry](https://learn.microsoft.com/azure/ai-foundry/what-is-azure-ai-foundry?WT.mc_id=AZ-MVP-5004796). It detects configuration drift, surfaces governance-approved remediation plans, and generates deployable IaC — all requiring human approval before any change is applied.

---

## Architecture

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
│                                                                   │
│  DiscoveryWorkflow ──► MultiAgentOrchestrator (Microsoft MAF)    │
│    Resource Graph        │                                        │
│    Cost Management       ├─ ServiceIntelligenceAgent              │
│    Log Analytics         ├─ BestPracticeEngine (700+ rules)      │
│                          ├─ DriftDetectionAgent                   │
│                          ├─ WellArchitectedAssessmentAgent       │
│                          ├─ FinOpsOptimizerAgent                 │
│                          ├─ CloudNativeMaturityAgent             │
│                          ├─ ArchitectureAgent                    │
│                          ├─ ReliabilityAgent                     │
│                          ├─ SustainabilityAgent                  │
│                          └─ GovernanceNegotiationAgent           │
│                                                                   │
│  IacGenerationWorkflow (Foundry-powered Bicep/Terraform)         │
└──────────────────────────────────────────────────────────────────┘
           All on Azure Container Apps + PostgreSQL Flexible Server
           Managed Identity · OpenTelemetry · Key Vault
```

Full architecture diagrams: [docs/diagrams/](docs/diagrams/)

---

## How NimbusIQ Differs from Azure Advisor, PSRules, and Azure Quick Review

Azure Advisor, PSRules for Azure, and Azure Quick Review are discovery and scanning tools — they detect violations and report them. NimbusIQ is an orchestration and decision-support platform that sits above these tools.

| Capability                                | Azure Advisor | PSRule         | Azure Quick Review | NimbusIQ                 |
| ----------------------------------------- | ------------- | -------------- | ------------------ | ------------------------ |
| Detect configuration violations           | ✓             | ✓              | ✓                  | ✓                        |
| Continuous drift trending                 | ✗             | ✗              | ✗                  | ✓                        |
| AI-powered reasoning across signals       | ✗             | ✗              | ✗                  | ✓ (6 LLM agents)         |
| Workload-scoped analysis                  | ✗             | ✗              | ✗                  | ✓ (Azure Service Groups) |
| Generate deployable IaC (Bicep/Terraform) | ✗             | ✗              | ✗                  | ✓                        |
| Dual-control approval workflow            | ✗             | ✗              | ✗                  | ✓                        |
| Explain WHY issues exist                  | ~Basic        | ~Pattern-based | ~Checklist-based   | ✓ (AI narrative)         |
| Track value realisation                   | ✗             | ✗              | ✗                  | ✓                        |
| Auditable agent-to-agent lineage          | ✗             | ✗              | ✗                  | ✓ (A2A tracing)          |

---

## Key Capabilities

### Multi-Agent Analysis Pipeline

Ten specialised agents run in configurable Sequential, Parallel, or Leader protocols. Six agents use Azure AI Foundry GPT-4 for deep analysis; four use deterministic rule-based evaluation. The `GovernanceNegotiationAgent` mediates cross-agent conflicts before results are committed.

### Drift Detection

Weighted severity scoring (critical=10, high=5, medium=2, low=1) compares current vs. previous discovery snapshots. Drift trend status (`stable`, `degrading`, `improving`) is surfaced on the dashboard with full evidence lineage.

### AI-Powered IaC Generation

When a recommendation is approved, the `IacGenerationWorkflow` calls Azure AI Foundry to generate Bicep or Terraform code with a rollback plan. All generated code requires human approval before application.

### Dual-Control Approval Workflow

All remediation actions require human approval through an idempotent state machine. The `DecisionService` enforces policy constraints before any IaC is applied.

### A2A Lineage Tracing

Every agent-to-agent message carries `LineageMetadata` with decision path, contributing agents, and confidence score. All decisions are replayable and auditable.

---

## Hero Technologies

| Technology                                                                                                            | How it is used                                                                                                                                                                                         |
| --------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **[Microsoft Agent Framework (MAF)](https://learn.microsoft.com/agent-framework/overview/?pivots=programming-language-csharp&WT.mc_id=AZ-MVP-5004796)**  | `WorkflowBuilder` + `InProcessExecution` drives 10 specialised agents with `UseOpenTelemetry()` instrumentation. **BestPracticeEngine** (700+ rules) feeds normalised results into the agent pipeline. |
| **[Microsoft Foundry](https://learn.microsoft.com/azure/ai-foundry/what-is-azure-ai-foundry?WT.mc_id=AZ-MVP-5004796)** | GPT-4 model deployment used by 6 agents for LLM-enhanced analysis and IaC generation (Bicep/Terraform).                                                                                                |
| **[Azure MCP](https://learn.microsoft.com/azure/developer/azure-mcp-server?WT.mc_id=AZ-MVP-5004796)**                 | Agent-to-tool protocol for dynamic Azure capability discovery with bearer-token authentication.                                                                                                        |
| **[Azure Container Apps](https://learn.microsoft.com/azure/container-apps/overview?WT.mc_id=AZ-MVP-5004796)**         | Hosts all three services with managed identity, auto-scaling, and zero-downtime revision deployment.                                                                                                   |
| **GitHub Copilot**                                                                                                    | Used throughout development for agent scaffolding, Bicep module authoring, and test writing.                                                                                                           |

---

## Prerequisites

- [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd?WT.mc_id=AZ-MVP-5004796)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0?WT.mc_id=AZ-MVP-5004796)
- [Node.js 20+](https://nodejs.org/)
- Azure subscription with permissions to create resources

---

## Quick Start — Deploy with `azd up`

### 1. Clone and initialise

```bash
git clone https://github.com/lukemurraynz/NimbusIQ.git
cd NimbusIQ

azd init
```

When prompted:

- **Environment name**: Choose a name (e.g., `nimbusiq-dev`)
- **Location**: Select your Azure region (e.g., `australiaeast`)

### 2. Set required configuration

```bash
azd env set NIMBUSIQ_POSTGRES_ADMIN_PASSWORD "YourSecurePassword123!"
```

### 3. Provision and deploy

```bash
azd up
```

This single command will:

1. Create an Azure resource group
2. Deploy PostgreSQL Flexible Server with a database
3. Create Azure Container Registry (ACR)
4. Set up Azure Key Vault for secrets
5. Create Log Analytics workspace
6. Deploy Container App Environment
7. Deploy Azure AI Foundry Hub with GPT-4 models (optional)
8. Build and push Docker images to ACR
9. Deploy all three services to Container Apps

### 4. Access the application

```bash
azd env get-values
```

Open the **Frontend URL** in your browser to access the NimbusIQ dashboard.

---

## Common Commands

```bash
# Deploy code changes without re-provisioning infrastructure
azd deploy

# Deploy a specific service only
azd deploy control-plane-api
azd deploy agent-orchestrator
azd deploy frontend

# View logs from a service
azd logs control-plane-api

# View environment variables and outputs
azd env get-values

# Clean up all Azure resources
azd down
```

---

## Local Development

```bash
# Frontend
cd apps/frontend
npm install
npm run dev

# Control Plane API
cd apps/control-plane-api
dotnet restore
dotnet run --project src/Api/Atlas.ControlPlane.Api.csproj

# Agent Orchestrator
cd apps/agent-orchestrator
dotnet restore
dotnet run --project src/Orchestration/Atlas.AgentOrchestrator.Orchestration.csproj
```

---

## Infrastructure

The infrastructure is defined using [Azure Bicep](https://learn.microsoft.com/azure/azure-resource-manager/bicep/overview?WT.mc_id=AZ-MVP-5004796) in `infrastructure/bicep/main.bicep` and uses [Azure Verified Modules](https://aka.ms/AVM) where available.

Resources provisioned:

- **Azure Container Apps** — hosting for all three services
- **Azure Container Registry** — private Docker image registry
- **PostgreSQL Flexible Server** — database for service metadata and graph
- **Azure Key Vault** — secrets management
- **Log Analytics** — centralised logging and monitoring
- **Managed Identities** — secure authentication between services
- **Microsoft Foundry** — ML workspace for agent orchestration (optional)
- **Azure AI Services** — GPT-4 and embedding models (optional)
- **Virtual Network** — private networking with private endpoints (optional)

---

## Project Structure

```
├── apps/
│   ├── control-plane-api/    # ASP.NET Core API (.NET 10)
│   ├── agent-orchestrator/   # MAF agent pipeline (.NET 10)
│   └── frontend/             # React + Fluent UI v9
├── infrastructure/
│   └── bicep/                # Azure Bicep IaC (AVM-based)
├── packages/
│   └── contracts/            # Shared contracts
├── scripts/                  # Deployment and provisioning scripts
├── tests/                    # Unit, integration, contract, e2e tests
├── docs/
│   ├── architecture.md       # Detailed architecture documentation
│   └── diagrams/             # Architecture diagrams (.drawio)
└── azure.yaml                # Azure Developer CLI configuration
```

---

## Blog Articles

For detailed technical write-ups covering the individual components — drift detection, OpenTelemetry instrumentation, governance negotiation, container security, and more — see [luke.geek.nz](https://luke.geek.nz).

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines on how to contribute.

## License

This project is licensed under the MIT License — see [LICENSE](LICENSE) for details.
