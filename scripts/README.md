# Scripts Directory

This directory contains operational scripts for managing the NimbusIQ Azure deployment.

## Available Scripts

### validate-nsp-learning-mode.sh

Validates Network Security Perimeter (NSP) access patterns before promoting from Learning mode to Enforced mode.

**Purpose**: Analyzes NSP access logs to ensure all legitimate traffic patterns are captured before enabling enforcement.

**Usage**:
```bash
./scripts/validate-nsp-learning-mode.sh \
  --resource-group atlas-prod-rg \
  --nsp-name atlas-nsp-prod \
  --workspace atlas-prod-logs \
  --days 14
```

**Parameters**:
- `--resource-group`: Azure resource group name
- `--nsp-name`: Network Security Perimeter name
- `--workspace`: Log Analytics workspace name
- `--days`: Number of days to analyze (default: 14)

**Prerequisites**:
- Azure CLI installed and authenticated
- `jq` command-line JSON processor
- Reader access to Log Analytics workspace
- Contributor access to NSP resource

**Output**:
- Total access attempts summary
- Access patterns by direction (inbound/outbound)
- Top source IPs
- Top destination FQDNs
- Denied access attempts (if any)
- Access pattern timeline
- Readiness assessment for Enforced mode

**Validation Checklist**:
- ✓ AI Foundry Hub access detected
- ✓ AI Services access detected
- ✓ MCP Server access patterns validated
- ✓ No unexpected outbound destinations
- ✓ No denied access attempts

**Example Output**:
```
========================================
NSP Learning Mode Validation Report
========================================
Resource Group: atlas-prod-rg
NSP Name: atlas-nsp-prod
Analysis Period: Last 14 days
========================================

✓ NSP Status: Succeeded
Current NSP Mode: Learning

1. Total Access Attempts:
   Total: 15234

2. Access by Direction:
   Direction  Count
   ---------  -----
   Outbound   12456
   Inbound    2778

...

========================================
✓ READY FOR ENFORCED MODE
========================================

To promote NSP to Enforced mode, run:
  azd up --parameter nspMode=Enforced
```

**Troubleshooting**:

If validation fails:

1. **No access logs found**:
   - Verify NSP diagnostic settings are enabled
   - Check Log Analytics workspace connection
   - Wait 24 hours for initial logs to appear

2. **AI Foundry Hub not accessed**:
   - Verify agent-orchestrator is running
   - Check managed identity RBAC roles
   - Review agent-orchestrator logs

3. **AI Services not accessed**:
   - Verify model deployments exist
   - Check AI Services endpoint configuration
   - Review agent-orchestrator environment variables

4. **Denied access attempts**:
   - Review denied requests in NSP logs
   - Update NSP inbound/outbound rules if legitimate
   - Investigate unauthorized access attempts

## Adding New Scripts

When adding new scripts:

1. Use descriptive names following kebab-case convention
2. Include proper error handling (`set -e`, `set -u`)
3. Add usage documentation in this README
4. Make scripts executable: `chmod +x scripts/<script>.sh`
5. Test scripts in dev/staging before production use
6. Document required Azure CLI extensions
7. Include validation checks for required tools (jq, az, etc.)

## Script Development Guidelines

- Use Bash for Azure CLI automation
- Use PowerShell for Windows-specific automation
- Add colored output for readability (GREEN/YELLOW/RED)
- Implement dry-run mode for destructive operations
- Log script execution with timestamps
- Return non-zero exit codes on failure
- Document all prerequisites and dependencies
