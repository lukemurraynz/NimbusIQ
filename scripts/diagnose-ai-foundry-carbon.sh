#!/bin/bash
# Automated diagnostics for AI Foundry fallback and missing carbon data issues
# Usage: ./scripts/diagnose-ai-foundry-carbon.sh

set -e

RG_NAME="${AZURE_RESOURCE_GROUP}"
DEPLOYMENT_NAME="${AZURE_DEPLOYMENT_NAME:-main}"
CONTROL_PLANE_APP_NAME="${CONTROL_PLANE_APP_NAME:-}"
AGENT_ORCH_APP_NAME="${AGENT_ORCH_APP_NAME:-}"

if [ -z "$RG_NAME" ]; then
  echo "Error: AZURE_RESOURCE_GROUP environment variable not set"
  echo "Usage: export AZURE_RESOURCE_GROUP=your-rg-name && ./scripts/diagnose-ai-foundry-carbon.sh"
  exit 1
fi

# Resolve deployment name when the default does not exist in the target resource group.
if ! az deployment group show --resource-group "$RG_NAME" --name "$DEPLOYMENT_NAME" >/dev/null 2>&1; then
  DEPLOYMENT_NAME=$(az deployment group list \
    --resource-group "$RG_NAME" \
    --query "[?contains(string(properties.outputs), 'aiFoundryProjectEndpoint')]|[0].name" \
    -o tsv 2>/dev/null || echo "")
fi

if [ -z "$DEPLOYMENT_NAME" ] || ! az deployment group show --resource-group "$RG_NAME" --name "$DEPLOYMENT_NAME" >/dev/null 2>&1; then
  DEPLOYMENT_NAME=$(az deployment group list \
    --resource-group "$RG_NAME" \
    --query "[?starts_with(name,'container-apps-') || starts_with(name,'main')]|[0].name" \
    -o tsv 2>/dev/null || echo "")
fi

# Resolve app names from azd-service-name tags when not provided.
if [ -z "$CONTROL_PLANE_APP_NAME" ]; then
  CONTROL_PLANE_APP_NAME=$(az containerapp list \
    --resource-group "$RG_NAME" \
    --query "[?tags.'azd-service-name'=='control-plane-api']|[0].name" \
    -o tsv 2>/dev/null || echo "")
fi

if [ -z "$AGENT_ORCH_APP_NAME" ]; then
  AGENT_ORCH_APP_NAME=$(az containerapp list \
    --resource-group "$RG_NAME" \
    --query "[?tags.'azd-service-name'=='agent-orchestrator']|[0].name" \
    -o tsv 2>/dev/null || echo "")
fi

# Backstop discovery for environments where tags were not applied.
if [ -z "$CONTROL_PLANE_APP_NAME" ]; then
  CONTROL_PLANE_APP_NAME=$(az containerapp list \
    --resource-group "$RG_NAME" \
    --query "[?contains(name,'control-plane-api')]|[0].name" \
    -o tsv 2>/dev/null || echo "")
fi

if [ -z "$AGENT_ORCH_APP_NAME" ]; then
  AGENT_ORCH_APP_NAME=$(az containerapp list \
    --resource-group "$RG_NAME" \
    --query "[?contains(name,'agent-orchestrator')]|[0].name" \
    -o tsv 2>/dev/null || echo "")
fi

echo "=== AI Foundry & Carbon Data Diagnostics ==="
echo "Resource Group: $RG_NAME"
echo "Deployment: ${DEPLOYMENT_NAME:-NOT FOUND}"
echo "Control Plane App: ${CONTROL_PLANE_APP_NAME:-NOT FOUND}"
echo "Agent Orchestrator App: ${AGENT_ORCH_APP_NAME:-NOT FOUND}"
echo ""

echo "1. Checking AI Foundry enablement..."
ENABLED=$(az deployment group show \
  --resource-group "$RG_NAME" \
  --name "${DEPLOYMENT_NAME:-main}" \
  --query "properties.parameters.enableAIFoundry.value" -o tsv 2>/dev/null || echo "unknown")
echo "   enableAIFoundry: $ENABLED"

echo ""
echo "2. Checking AI Foundry endpoint..."
ENDPOINT=$(az deployment group show \
  --resource-group "$RG_NAME" \
  --name "${DEPLOYMENT_NAME:-main}" \
  --query "properties.outputs.aiFoundryProjectEndpoint.value" -o tsv 2>/dev/null || echo "")
echo "   aiFoundryProjectEndpoint: ${ENDPOINT:-NOT SET}"

echo ""
echo "3. Checking control-plane-api environment variables..."
CP_ENDPOINT=$(az containerapp show \
  --resource-group "$RG_NAME" \
  --name "$CONTROL_PLANE_APP_NAME" \
  --query "properties.template.containers[0].env[?name=='AzureAIFoundry__ProjectEndpoint'].value" -o tsv 2>/dev/null || echo "")
echo "   AzureAIFoundry__ProjectEndpoint: ${CP_ENDPOINT:-NOT SET}"

CP_MODEL=$(az containerapp show \
  --resource-group "$RG_NAME" \
  --name "$CONTROL_PLANE_APP_NAME" \
  --query "properties.template.containers[0].env[?name=='AzureAIFoundry__DefaultModelDeployment'].value" -o tsv 2>/dev/null || echo "")
echo "   AzureAIFoundry__DefaultModelDeployment: ${CP_MODEL:-NOT SET}"

echo ""
echo "4. Checking agent-orchestrator status..."
AO_STATUS=$(az containerapp show \
  --resource-group "$RG_NAME" \
  --name "$AGENT_ORCH_APP_NAME" \
  --query "properties.runningStatus" -o tsv 2>/dev/null || echo "unknown")
echo "   Running status: $AO_STATUS"

AO_ENDPOINT=$(az containerapp show \
  --resource-group "$RG_NAME" \
  --name "$AGENT_ORCH_APP_NAME" \
  --query "properties.template.containers[0].env[?name=='AzureAIFoundry__ProjectEndpoint'].value" -o tsv 2>/dev/null || echo "")
echo "   AzureAIFoundry__ProjectEndpoint: ${AO_ENDPOINT:-NOT SET}"

echo ""
echo "5. Checking latest logs for AI Foundry initialization..."
az containerapp logs show \
  --resource-group "$RG_NAME" \
  --name "$CONTROL_PLANE_APP_NAME" \
  --follow false \
  --tail 50 2>/dev/null | grep -i "AI Chat Service" || echo "   No AI Chat Service logs found"

echo ""
echo "6. Checking for AI Foundry errors..."
az containerapp logs show \
  --resource-group "$RG_NAME" \
  --name "$CONTROL_PLANE_APP_NAME" \
  --follow false \
  --tail 100 2>/dev/null | grep -i "Azure AI Foundry chat failed" | tail -3 || echo "   No AI Foundry error logs found (good)"

echo ""
echo "7. Checking for carbon data persistence..."
az containerapp logs show \
  --resource-group "$RG_NAME" \
  --name "$AGENT_ORCH_APP_NAME" \
  --follow false \
  --tail 100 2>/dev/null | grep -i "Persisted sustainability assessment" | tail -3 || echo "   No sustainability persistence logs found"

echo ""
echo "8. Checking for agent orchestrator errors..."
az containerapp logs show \
  --resource-group "$RG_NAME" \
  --name "$AGENT_ORCH_APP_NAME" \
  --follow false \
  --tail 100 2>/dev/null | grep -i "error\|exception\|failed" | head -5 || echo "   No recent error logs (good)"

echo ""
echo "=== Diagnostic complete ==="
echo ""
echo "Next steps:"
if [ -z "$CONTROL_PLANE_APP_NAME" ] || [ -z "$AGENT_ORCH_APP_NAME" ]; then
  echo "  ❌ Could not resolve container app names."
  echo "     Set CONTROL_PLANE_APP_NAME and AGENT_ORCH_APP_NAME, then rerun this script."
elif [ "$ENABLED" != "true" ]; then
  echo "  ❌ AI Foundry is disabled. Set enableAIFoundry=true in bicepparam and redeploy with 'azd provision'."
elif [ -z "$ENDPOINT" ] && [ -z "$CP_ENDPOINT" ]; then
  echo "  ❌ aiFoundryProjectEndpoint output is empty. Check if AI Foundry module deployed successfully."
  echo "     Run: az deployment group show --resource-group $RG_NAME --name $DEPLOYMENT_NAME --query properties.outputs"
elif [ -z "$CP_ENDPOINT" ]; then
  echo "  ❌ AzureAIFoundry__ProjectEndpoint not set in control-plane-api. Check container-apps.bicep module."
  echo "     Expected condition: enableAIFoundry && !empty(aiFoundryProjectEndpoint)"
elif [ -z "$CP_MODEL" ]; then
  echo "  ❌ AzureAIFoundry__DefaultModelDeployment not set. Should be 'gpt-4o'."
elif [ "$AO_STATUS" != "Running" ]; then
  echo "  ❌ Agent orchestrator not running. Check container app logs:"
  echo "     Run: az containerapp logs show --resource-group $RG_NAME --name $AGENT_ORCH_APP_NAME --follow true"
else
  echo "  ✅ Configuration looks correct. Check application logs for runtime errors:"
  echo ""
  echo "  For AI Foundry issues:"
  echo "    az containerapp logs show --resource-group $RG_NAME --name $CONTROL_PLANE_APP_NAME --follow true | grep -i foundry"
  echo ""
  echo "  For carbon data issues:"
  echo "    az containerapp logs show --resource-group $RG_NAME --name $AGENT_ORCH_APP_NAME --follow true | grep -i sustainability"
  echo ""
  echo "  Trigger a test analysis run:"
  echo "    curl -X POST https://<control-plane-url>/api/v1/analysis/trigger?api-version=2025-01-15 -H 'Content-Type: application/json' -d '{\"serviceGroupId\":\"<guid>\"}'"
fi
