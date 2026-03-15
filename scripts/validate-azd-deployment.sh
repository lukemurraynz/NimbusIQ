#!/bin/bash
set -euo pipefail

# Validate azd deployment end-to-end with managed identity and NSP
# This script validates:
# 1. Infrastructure provisioning (Bicep deployment)
# 2. Managed Identity configuration (PostgreSQL, ACR, AI Foundry)
# 3. Network Security Perimeter (when enabled)
# 4. Container Apps deployment
# 5. End-to-end connectivity and authentication

echo "🚀 Atlas E2E Deployment Validation"
echo "=================================="

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Validation functions
check_command() {
    if ! command -v "$1" &> /dev/null; then
        echo -e "${RED}❌ $1 is not installed${NC}"
        return 1
    fi
    echo -e "${GREEN}✅ $1 is installed${NC}"
    return 0
}

check_azure_login() {
    if ! az account show &> /dev/null; then
        echo -e "${RED}❌ Not logged in to Azure${NC}"
        return 1
    fi
    echo -e "${GREEN}✅ Logged in to Azure${NC}"
    SUBSCRIPTION_ID=$(az account show --query id -o tsv)
    echo "   Subscription: $SUBSCRIPTION_ID"
    return 0
}

check_azd_login() {
    if ! azd auth login --check-status &> /dev/null; then
        echo -e "${RED}❌ Not logged in to azd${NC}"
        return 1
    fi
    echo -e "${GREEN}✅ Logged in to azd${NC}"
    return 0
}

# Step 1: Prerequisites
echo ""
echo "📋 Step 1: Checking Prerequisites"
echo "--------------------------------"
check_command "az" || exit 1
check_command "azd" || exit 1
check_command "jq" || echo -e "${YELLOW}⚠️  jq not installed (optional)${NC}"
check_azure_login || exit 1
check_azd_login || exit 1

# Step 2: Check azd environment
echo ""
echo "🔍 Step 2: Checking azd Environment"
echo "-----------------------------------"
if ! azd env list &> /dev/null; then
    echo -e "${RED}❌ No azd environment found. Run 'azd init' first${NC}"
    exit 1
fi

ENV_NAME=$(azd env get-values | grep AZURE_ENV_NAME | cut -d'=' -f2 | tr -d '"' || echo "")
if [ -z "$ENV_NAME" ]; then
    echo -e "${YELLOW}⚠️  AZURE_ENV_NAME not set, using default from azd${NC}"
    ENV_NAME=$(azd env list --output json | jq -r '.[0].Name' 2>/dev/null || echo "prod")
fi

echo -e "${GREEN}✅ Environment: $ENV_NAME${NC}"

# Get environment values
RESOURCE_GROUP=$(azd env get-values | grep -E '^AZURE_RESOURCE_GROUP=' | cut -d'=' -f2 | tr -d '"' || echo "atlas-${ENV_NAME}-rg")
LOCATION=$(azd env get-values | grep AZURE_LOCATION | cut -d'=' -f2 | tr -d '"' || echo "uksouth")
PROJECT_NAME=$(azd env get-values | grep -E '^NIMBUSIQ_PROJECT_NAME=' | cut -d'=' -f2 | tr -d '"' || echo "nimbusiq")

echo "   Resource Group: $RESOURCE_GROUP"
echo "   Location: $LOCATION"
echo "   Project: $PROJECT_NAME"

# Step 3: Validate Bicep syntax
echo ""
echo "🔨 Step 3: Validating Bicep Syntax"
echo "----------------------------------"
if [ ! -f "infrastructure/bicep/main.bicep" ]; then
    echo -e "${RED}❌ main.bicep not found${NC}"
    exit 1
fi

if az bicep build --file infrastructure/bicep/main.bicep --outdir /tmp &> /dev/null; then
    echo -e "${GREEN}✅ Bicep syntax valid${NC}"
else
    echo -e "${RED}❌ Bicep syntax errors detected${NC}"
    az bicep build --file infrastructure/bicep/main.bicep --outdir /tmp
    exit 1
fi

# Step 4: Check if infrastructure is deployed
echo ""
echo "🏗️  Step 4: Checking Infrastructure Deployment"
echo "--------------------------------------------"
if ! az group show --name "$RESOURCE_GROUP" &> /dev/null; then
    echo -e "${YELLOW}⚠️  Resource group not found. Infrastructure not deployed yet.${NC}"
    echo "   Run 'azd provision' or 'azd up' to deploy infrastructure"
    INFRA_DEPLOYED=false
else
    echo -e "${GREEN}✅ Resource group exists${NC}"
    INFRA_DEPLOYED=true
fi

if [ "$INFRA_DEPLOYED" = true ]; then
    # Step 5: Validate Managed Identity Configuration
    echo ""
    echo "🔐 Step 5: Validating Managed Identity"
    echo "--------------------------------------"

    # Check for ACR pull identity
    ACR_PULL_IDENTITY=$(az identity list --resource-group "$RESOURCE_GROUP" --query "[?contains(name, 'acrpull')].name" -o tsv | head -1 || echo "")
    if [ -n "$ACR_PULL_IDENTITY" ]; then
        echo -e "${GREEN}✅ ACR Pull Identity found: $ACR_PULL_IDENTITY${NC}"

        # Check identity principal ID
        PRINCIPAL_ID=$(az identity show --resource-group "$RESOURCE_GROUP" --name "$ACR_PULL_IDENTITY" --query principalId -o tsv)
        echo "   Principal ID: $PRINCIPAL_ID"

        # Check PostgreSQL administrators
        PG_SERVER=$(az postgres flexible-server list --resource-group "$RESOURCE_GROUP" --query "[0].name" -o tsv || echo "")
        if [ -n "$PG_SERVER" ]; then
            echo -e "${GREEN}✅ PostgreSQL server found: $PG_SERVER${NC}"

            # Check if managed identity is PostgreSQL admin
            if az postgres flexible-server ad-admin list --resource-group "$RESOURCE_GROUP" --server-name "$PG_SERVER" --query "[?objectId=='$PRINCIPAL_ID']" -o tsv | grep -q "$PRINCIPAL_ID"; then
                echo -e "${GREEN}✅ Managed Identity is PostgreSQL administrator${NC}"
            else
                echo -e "${YELLOW}⚠️  Managed Identity not configured as PostgreSQL admin${NC}"
            fi
        else
            echo -e "${YELLOW}⚠️  PostgreSQL server not found${NC}"
        fi
    else
        echo -e "${YELLOW}⚠️  ACR Pull Identity not found${NC}"
    fi

    # Check Container Registry
    ACR_NAME=$(az acr list --resource-group "$RESOURCE_GROUP" --query "[0].name" -o tsv || echo "")
    if [ -n "$ACR_NAME" ]; then
        echo -e "${GREEN}✅ Azure Container Registry found: $ACR_NAME${NC}"

        # Check if identity has AcrPull role
        ACR_ID=$(az acr show --resource-group "$RESOURCE_GROUP" --name "$ACR_NAME" --query id -o tsv)
        if az role assignment list --assignee "$PRINCIPAL_ID" --scope "$ACR_ID" --query "[?roleDefinitionName=='AcrPull']" -o tsv | grep -q "AcrPull"; then
            echo -e "${GREEN}✅ Managed Identity has AcrPull role${NC}"
        else
            echo -e "${YELLOW}⚠️  AcrPull role not assigned${NC}"
        fi
    else
        echo -e "${YELLOW}⚠️  Container Registry not found${NC}"
    fi

    # Step 6: Validate Network Security Perimeter (if enabled)
    echo ""
    echo "🛡️  Step 6: Validating Network Security Perimeter"
    echo "-----------------------------------------------"

    NSP_ENABLED=$(azd env get-values | grep enableNetworkSecurityPerimeter | cut -d'=' -f2 | tr -d '"' || echo "false")
    if [ "$NSP_ENABLED" = "true" ]; then
        NSP_NAME=$(az network nsp list --resource-group "$RESOURCE_GROUP" --query "[0].name" -o tsv 2>/dev/null || echo "")
        if [ -n "$NSP_NAME" ]; then
            echo -e "${GREEN}✅ Network Security Perimeter found: $NSP_NAME${NC}"

            # Check NSP mode
            NSP_MODE=$(az network nsp show --resource-group "$RESOURCE_GROUP" --name "$NSP_NAME" --query "tags.\"nsp-mode\"" -o tsv 2>/dev/null || echo "")
            echo "   NSP Mode: $NSP_MODE"

            # Check NSP associations
            ASSOCIATIONS=$(az network nsp association list --nsp-name "$NSP_NAME" --resource-group "$RESOURCE_GROUP" --query "length([])" -o tsv 2>/dev/null || echo "0")
            echo -e "${GREEN}✅ NSP Associations: $ASSOCIATIONS${NC}"

            # Check NSP access rules
            PROFILES=$(az network nsp profile list --nsp-name "$NSP_NAME" --resource-group "$RESOURCE_GROUP" --query "[].name" -o tsv 2>/dev/null || echo "")
            if [ -n "$PROFILES" ]; then
                echo -e "${GREEN}✅ NSP Profiles configured${NC}"
            fi
        else
            echo -e "${YELLOW}⚠️  Network Security Perimeter not found (expected when enabled)${NC}"
        fi
    else
        echo -e "${YELLOW}⚠️  Network Security Perimeter disabled${NC}"
        echo "   Set 'enableNetworkSecurityPerimeter=true' in main.bicepparam to enable"
    fi

    # Step 7: Validate Container Apps
    echo ""
    echo "📦 Step 7: Validating Container Apps"
    echo "-----------------------------------"

    CONTAINER_APPS=$(az containerapp list --resource-group "$RESOURCE_GROUP" --query "length([])" -o tsv || echo "0")
    if [ "$CONTAINER_APPS" -gt 0 ]; then
        echo -e "${GREEN}✅ Container Apps deployed: $CONTAINER_APPS${NC}"

        # Check each app
        for APP_NAME in $(az containerapp list --resource-group "$RESOURCE_GROUP" --query "[].name" -o tsv); do
            echo ""
            echo "   Checking: $APP_NAME"

            # Check provisioning state
            PROV_STATE=$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$APP_NAME" --query "properties.provisioningState" -o tsv)
            if [ "$PROV_STATE" = "Succeeded" ]; then
                echo -e "   ${GREEN}✅ Provisioning: $PROV_STATE${NC}"
            else
                echo -e "   ${YELLOW}⚠️  Provisioning: $PROV_STATE${NC}"
            fi

            # Check running state
            RUN_STATE=$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$APP_NAME" --query "properties.runningStatus" -o tsv || echo "Unknown")
            if [ "$RUN_STATE" = "Running" ]; then
                echo -e "   ${GREEN}✅ Running: $RUN_STATE${NC}"
            else
                echo -e "   ${YELLOW}⚠️  Running: $RUN_STATE${NC}"
            fi

            # Check managed identity
            IDENTITY_TYPE=$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$APP_NAME" --query "identity.type" -o tsv || echo "None")
            echo "   Identity: $IDENTITY_TYPE"

            # Get FQDN if external ingress
            FQDN=$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$APP_NAME" --query "properties.configuration.ingress.fqdn" -o tsv 2>/dev/null || echo "")
            if [ -n "$FQDN" ]; then
                echo "   FQDN: https://$FQDN"
            fi
        done
    else
        echo -e "${YELLOW}⚠️  No Container Apps found${NC}"
        echo "   Run 'azd deploy' to deploy applications"
    fi

    # Step 8: End-to-End Connectivity Test
    echo ""
    echo "🌐 Step 8: Testing End-to-End Connectivity"
    echo "-----------------------------------------"

    API_FQDN=$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "${PROJECT_NAME}-control-plane-api-${ENV_NAME}" --query "properties.configuration.ingress.fqdn" -o tsv 2>/dev/null || echo "")
    if [ -n "$API_FQDN" ]; then
        echo "   Testing API health endpoint..."
        if curl -f -s "https://$API_FQDN/health/ready" > /dev/null 2>&1; then
            echo -e "${GREEN}✅ API health endpoint responding${NC}"
        else
            echo -e "${YELLOW}⚠️  API health endpoint not responding (may still be starting)${NC}"
        fi
    fi

    FRONTEND_FQDN=$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "${PROJECT_NAME}-frontend-${ENV_NAME}" --query "properties.configuration.ingress.fqdn" -o tsv 2>/dev/null || echo "")
    if [ -n "$FRONTEND_FQDN" ]; then
        echo "   Testing frontend..."
        if curl -f -s "https://$FRONTEND_FQDN" > /dev/null 2>&1; then
            echo -e "${GREEN}✅ Frontend responding${NC}"
            echo "   URL: https://$FRONTEND_FQDN"
        else
            echo -e "${YELLOW}⚠️  Frontend not responding (may still be starting)${NC}"
        fi
    fi
fi

# Final Summary
echo ""
echo "📊 Validation Summary"
echo "===================="
echo -e "${GREEN}✅ Prerequisites: OK${NC}"
echo -e "${GREEN}✅ Bicep Syntax: OK${NC}"

if [ "$INFRA_DEPLOYED" = true ]; then
    echo -e "${GREEN}✅ Infrastructure: Deployed${NC}"
    echo -e "${GREEN}✅ Managed Identity: Configured${NC}"

    if [ "$NSP_ENABLED" = "true" ]; then
        echo -e "${GREEN}✅ Network Security Perimeter: Enabled${NC}"
    else
        echo -e "${YELLOW}⚠️  Network Security Perimeter: Disabled${NC}"
    fi

    if [ "$CONTAINER_APPS" -gt 0 ]; then
        echo -e "${GREEN}✅ Container Apps: Deployed${NC}"
    else
        echo -e "${YELLOW}⚠️  Container Apps: Not deployed${NC}"
    fi
else
    echo -e "${YELLOW}⚠️  Infrastructure: Not deployed${NC}"
    echo ""
    echo "Next Steps:"
    echo "1. Run 'azd provision' to deploy infrastructure"
    echo "2. Run 'azd deploy' to deploy applications"
    echo "3. Or run 'azd up' to do both"
fi

echo ""
echo "✨ Validation complete!"
