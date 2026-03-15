#!/bin/bash
# Network Security Perimeter (NSP) Learning Mode Validation Script
# This script helps validate NSP access patterns before promoting to Enforced mode

set -e
set -u

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Configuration
RESOURCE_GROUP=""
NSP_NAME=""
LOG_ANALYTICS_WORKSPACE=""
DAYS_TO_ANALYZE=14

# Parse arguments
while [[ $# -gt 0 ]]; do
  case $1 in
    --resource-group)
      RESOURCE_GROUP="$2"
      shift 2
      ;;
    --nsp-name)
      NSP_NAME="$2"
      shift 2
      ;;
    --workspace)
      LOG_ANALYTICS_WORKSPACE="$2"
      shift 2
      ;;
    --days)
      DAYS_TO_ANALYZE="$2"
      shift 2
      ;;
    *)
      echo "Unknown option: $1"
      echo "Usage: $0 --resource-group <rg> --nsp-name <nsp> --workspace <workspace> [--days <days>]"
      exit 1
      ;;
  esac
done

# Validate required parameters
if [[ -z "$RESOURCE_GROUP" ]] || [[ -z "$NSP_NAME" ]] || [[ -z "$LOG_ANALYTICS_WORKSPACE" ]]; then
  echo -e "${RED}Error: Missing required parameters${NC}"
  echo "Usage: $0 --resource-group <rg> --nsp-name <nsp> --workspace <workspace> [--days <days>]"
  exit 1
fi

echo "========================================"
echo "NSP Learning Mode Validation Report"
echo "========================================"
echo "Resource Group: $RESOURCE_GROUP"
echo "NSP Name: $NSP_NAME"
echo "Analysis Period: Last $DAYS_TO_ANALYZE days"
echo "========================================"
echo ""

# Check if NSP exists
echo -e "${YELLOW}Checking NSP status...${NC}"
NSP_STATUS=$(az network nsp show \
  --name "$NSP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "provisioningState" \
  --output tsv 2>/dev/null || echo "NotFound")

if [ "$NSP_STATUS" == "NotFound" ]; then
  echo -e "${RED}ERROR: NSP not found${NC}"
  exit 1
fi

echo -e "${GREEN}✓ NSP Status: $NSP_STATUS${NC}"
echo ""

# Get NSP mode
NSP_MODE=$(az network nsp show \
  --name "$NSP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "tags.\"nsp-mode\"" \
  --output tsv)

echo "Current NSP Mode: $NSP_MODE"
echo ""

if [ "$NSP_MODE" != "Learning" ]; then
  echo -e "${YELLOW}WARNING: NSP is not in Learning mode. This script is designed for Learning mode validation.${NC}"
  echo ""
fi

# Query NSP access logs
echo -e "${YELLOW}Analyzing NSP access logs...${NC}"

# 1. Total access attempts
echo "1. Total Access Attempts:"
TOTAL_ATTEMPTS=$(az monitor log-analytics query \
  --workspace "$LOG_ANALYTICS_WORKSPACE" \
  --analytics-query "NSPAccessLogs | where TimeGenerated > ago(${DAYS_TO_ANALYZE}d) | count" \
  --output json | jq -r '.[0].Count' 2>/dev/null || echo "0")

echo "   Total: $TOTAL_ATTEMPTS"
echo ""

if [ "$TOTAL_ATTEMPTS" == "0" ]; then
  echo -e "${RED}WARNING: No access logs found. NSP may not be configured correctly or no traffic has occurred.${NC}"
  echo ""
fi

# 2. Access by direction
echo "2. Access by Direction:"
az monitor log-analytics query \
  --workspace "$LOG_ANALYTICS_WORKSPACE" \
  --analytics-query "NSPAccessLogs | where TimeGenerated > ago(${DAYS_TO_ANALYZE}d) | summarize Count=count() by Direction | order by Count desc" \
  --output table

echo ""

# 3. Top source IPs
echo "3. Top Source IPs (Inbound):"
az monitor log-analytics query \
  --workspace "$LOG_ANALYTICS_WORKSPACE" \
  --analytics-query "NSPAccessLogs | where TimeGenerated > ago(${DAYS_TO_ANALYZE}d) and Direction == 'Inbound' | summarize Count=count() by SourceIP | top 10 by Count desc" \
  --output table

echo ""

# 4. Top destination FQDNs (Outbound)
echo "4. Top Destination FQDNs (Outbound):"
az monitor log-analytics query \
  --workspace "$LOG_ANALYTICS_WORKSPACE" \
  --analytics-query "NSPAccessLogs | where TimeGenerated > ago(${DAYS_TO_ANALYZE}d) and Direction == 'Outbound' | summarize Count=count() by DestinationFQDN | top 10 by Count desc" \
  --output table

echo ""

# 5. Denied access attempts (if any)
echo "5. Denied Access Attempts:"
DENIED_COUNT=$(az monitor log-analytics query \
  --workspace "$LOG_ANALYTICS_WORKSPACE" \
  --analytics-query "NSPAccessLogs | where TimeGenerated > ago(${DAYS_TO_ANALYZE}d) and Action == 'Deny' | count" \
  --output json | jq -r '.[0].Count' 2>/dev/null || echo "0")

echo "   Total Denied: $DENIED_COUNT"

if [ "$DENIED_COUNT" != "0" ]; then
  echo ""
  echo "   Recent Denied Attempts:"
  az monitor log-analytics query \
    --workspace "$LOG_ANALYTICS_WORKSPACE" \
    --analytics-query "NSPAccessLogs | where TimeGenerated > ago(${DAYS_TO_ANALYZE}d) and Action == 'Deny' | project TimeGenerated, Direction, SourceIP, DestinationFQDN, Protocol | top 20 by TimeGenerated desc" \
    --output table
fi

echo ""

# 6. Access patterns by hour
echo "6. Access Pattern by Hour (Last 24 hours):"
az monitor log-analytics query \
  --workspace "$LOG_ANALYTICS_WORKSPACE" \
  --analytics-query "NSPAccessLogs | where TimeGenerated > ago(1d) | summarize Count=count() by bin(TimeGenerated, 1h) | order by TimeGenerated desc" \
  --output table

echo ""

# 7. Validation checklist
echo "========================================"
echo "Validation Checklist for Enforced Mode"
echo "========================================"
echo ""

# Check if agent-orchestrator has accessed AI Foundry
AI_FOUNDRY_ACCESS=$(az monitor log-analytics query \
  --workspace "$LOG_ANALYTICS_WORKSPACE" \
  --analytics-query "NSPAccessLogs | where TimeGenerated > ago(${DAYS_TO_ANALYZE}d) and DestinationFQDN contains 'azureml' | count" \
  --output json | jq -r '.[0].Count' 2>/dev/null || echo "0")

if [ "$AI_FOUNDRY_ACCESS" != "0" ]; then
  echo -e "${GREEN}✓ AI Foundry Hub access detected ($AI_FOUNDRY_ACCESS requests)${NC}"
else
  echo -e "${RED}✗ No AI Foundry Hub access detected - verify agent-orchestrator is running${NC}"
fi

# Check if AI Services accessed
AI_SERVICES_ACCESS=$(az monitor log-analytics query \
  --workspace "$LOG_ANALYTICS_WORKSPACE" \
  --analytics-query "NSPAccessLogs | where TimeGenerated > ago(${DAYS_TO_ANALYZE}d) and DestinationFQDN contains 'cognitiveservices' | count" \
  --output json | jq -r '.[0].Count' 2>/dev/null || echo "0")

if [ "$AI_SERVICES_ACCESS" != "0" ]; then
  echo -e "${GREEN}✓ AI Services access detected ($AI_SERVICES_ACCESS requests)${NC}"
else
  echo -e "${RED}✗ No AI Services access detected - verify model deployments${NC}"
fi

# Check for MCP Server access
MCP_ACCESS=$(az monitor log-analytics query \
  --workspace "$LOG_ANALYTICS_WORKSPACE" \
  --analytics-query "NSPAccessLogs | where TimeGenerated > ago(${DAYS_TO_ANALYZE}d) and (DestinationFQDN contains 'azure.com' or DestinationFQDN contains 'microsoft.com') | count" \
  --output json | jq -r '.[0].Count' 2>/dev/null || echo "0")

if [ "$MCP_ACCESS" != "0" ]; then
  echo -e "${GREEN}✓ MCP Server access detected ($MCP_ACCESS requests)${NC}"
else
  echo -e "${YELLOW}! No MCP Server access detected - may not be in use yet${NC}"
fi

# Check for unexpected access patterns
UNEXPECTED_ACCESS=$(az monitor log-analytics query \
  --workspace "$LOG_ANALYTICS_WORKSPACE" \
  --analytics-query "NSPAccessLogs | where TimeGenerated > ago(${DAYS_TO_ANALYZE}d) and DestinationFQDN !contains 'azure' and DestinationFQDN !contains 'microsoft' | count" \
  --output json | jq -r '.[0].Count' 2>/dev/null || echo "0")

if [ "$UNEXPECTED_ACCESS" != "0" ]; then
  echo -e "${YELLOW}! Unexpected destination FQDNs detected ($UNEXPECTED_ACCESS requests)${NC}"
  echo "  Review: az monitor log-analytics query --workspace $LOG_ANALYTICS_WORKSPACE --analytics-query \"NSPAccessLogs | where TimeGenerated > ago(${DAYS_TO_ANALYZE}d) and DestinationFQDN !contains 'azure' and DestinationFQDN !contains 'microsoft' | project DestinationFQDN | distinct DestinationFQDN\""
else
  echo -e "${GREEN}✓ All access patterns within expected domains${NC}"
fi

echo ""

# Readiness assessment
echo "========================================"
echo "Readiness Assessment"
echo "========================================"
echo ""

READY=true

if [ "$TOTAL_ATTEMPTS" == "0" ]; then
  echo -e "${RED}✗ FAIL: No access logs found${NC}"
  READY=false
fi

if [ "$AI_FOUNDRY_ACCESS" == "0" ]; then
  echo -e "${RED}✗ FAIL: AI Foundry Hub not accessed${NC}"
  READY=false
fi

if [ "$AI_SERVICES_ACCESS" == "0" ]; then
  echo -e "${RED}✗ FAIL: AI Services not accessed${NC}"
  READY=false
fi

if [ "$DENIED_COUNT" != "0" ]; then
  echo -e "${YELLOW}⚠ WARNING: Denied access attempts detected${NC}"
  echo "  Review denied requests before promoting to Enforced mode"
  READY=false
fi

if [ "$UNEXPECTED_ACCESS" != "0" ]; then
  echo -e "${YELLOW}⚠ WARNING: Unexpected destination FQDNs detected${NC}"
  echo "  Review and approve before promoting to Enforced mode"
  READY=false
fi

echo ""

if [ "$READY" = true ]; then
  echo -e "${GREEN}========================================${NC}"
  echo -e "${GREEN}✓ READY FOR ENFORCED MODE${NC}"
  echo -e "${GREEN}========================================${NC}"
  echo ""
  echo "To promote NSP to Enforced mode, run:"
  echo "  azd up --parameter nspMode=Enforced"
  echo ""
else
  echo -e "${RED}========================================${NC}"
  echo -e "${RED}✗ NOT READY FOR ENFORCED MODE${NC}"
  echo -e "${RED}========================================${NC}"
  echo ""
  echo "Address the issues above before promoting to Enforced mode."
  echo "Continue monitoring for at least $DAYS_TO_ANALYZE days."
  echo ""
fi
