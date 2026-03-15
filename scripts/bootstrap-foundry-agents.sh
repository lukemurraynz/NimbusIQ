#!/usr/bin/env sh
set -eu

echo "Bootstrapping Azure AI Foundry hosted agents..."

# Load azd environment values into current shell when available
if command -v azd >/dev/null 2>&1; then
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    export "$line"
  done <<EOF
$(azd env get-values || true)
EOF
fi

PROJECT_ENDPOINT="${AzureAIFoundry__ProjectEndpoint:-${AZURE_AI_PROJECT_CONNECTION_STRING:-${AZURE_AI_PROJECT_ENDPOINT:-}}}"

if [ -z "$PROJECT_ENDPOINT" ]; then
  echo "⚠️  Azure AI Foundry endpoint/connection string not found. Skipping hosted agent bootstrap."
  exit 0
fi

export AzureAIFoundry__ProjectEndpoint="${AzureAIFoundry__ProjectEndpoint:-$PROJECT_ENDPOINT}"
export AzureAIFoundry__DefaultModelDeployment="${AzureAIFoundry__DefaultModelDeployment:-gpt-4o}"

if [ -z "${AzureAIFoundry__CapabilityHostName:-${aiFoundryCapabilityHostName:-}}" ]; then
  echo "⚠️  Azure AI Foundry capability host name not found. Skipping hosted agent bootstrap."
  exit 0
fi

dotnet run --project ./apps/agent-orchestrator/src/Bootstrap/Atlas.AgentOrchestrator.Bootstrap.csproj -c Release

echo "✅ Foundry hosted agent bootstrap finished"
