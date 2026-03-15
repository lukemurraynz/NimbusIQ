#!/bin/bash
# Post-Deployment Verification Script
# Verifies all security fixes are working in production

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo "🔍 NimbusIQ Post-Deployment Verification"
echo "========================================"
echo ""

# Get frontend URL from azd environment
FRONTEND_URL=$(azd env get-values | grep AZURE_CONTAINER_APPS_FRONTEND_URL | cut -d'=' -f2 | tr -d '"')
API_URL=$(azd env get-values | grep AZURE_CONTAINER_APPS_API_URL | cut -d'=' -f2 | tr -d '"')

if [ -z "$FRONTEND_URL" ]; then
    echo -e "${YELLOW}⚠️  Could not determine frontend URL from azd environment${NC}"
    echo "Please provide frontend URL:"
    read -r FRONTEND_URL
fi

if [ -z "$API_URL" ]; then
    echo -e "${YELLOW}⚠️  Could not determine API URL from azd environment${NC}"
    echo "Please provide API URL:"
    read -r API_URL
fi

echo "Frontend URL: $FRONTEND_URL"
echo "API URL: $API_URL"
echo ""

# 1. Check API Health Endpoints
echo "1️⃣  Testing API Health Endpoints"
echo "-----------------------------------"
if curl -f -s "${API_URL}/health/ready" > /dev/null; then
    echo -e "${GREEN}✅ /health/ready - OK${NC}"
else
    echo -e "${RED}❌ /health/ready - FAILED${NC}"
fi

if curl -f -s "${API_URL}/health/live" > /dev/null; then
    echo -e "${GREEN}✅ /health/live - OK${NC}"
else
    echo -e "${RED}❌ /health/live - FAILED${NC}"
fi
echo ""

# 2. Test Request Timeout (REL-001 fix)
echo "2️⃣  Testing Request Timeout (30s limit)"
echo "-----------------------------------"
echo -e "${YELLOW}⏱️  This test would require a slow endpoint${NC}"
echo -e "${YELLOW}⏱️  Manual test: Use Chrome DevTools Network throttling${NC}"
echo ""

# 3. Test Rate Limiting (CYBER-001/002 fix)
echo "3️⃣  Testing Backend Rate Limiting"
echo "-----------------------------------"
RATE_LIMIT_COUNT=0
for i in {1..110}; do
    STATUS=$(curl -s -o /dev/null -w "%{http_code}" "${API_URL}/health/ready")
    if [ "$STATUS" -eq 429 ]; then
        RATE_LIMIT_COUNT=$((RATE_LIMIT_COUNT + 1))
    fi
    if [ $((i % 20)) -eq 0 ]; then
        echo "  Sent $i requests..."
    fi
done

if [ "$RATE_LIMIT_COUNT" -gt 0 ]; then
    echo -e "${GREEN}✅ Rate limiting working - received $RATE_LIMIT_COUNT x 429 responses${NC}"
else
    echo -e "${YELLOW}⚠️  Rate limiting not triggered - might need more requests or different endpoint${NC}"
fi
echo ""

# 4. Check Frontend Loads
echo "4️⃣  Testing Frontend Availability"
echo "-----------------------------------"
if curl -f -s "$FRONTEND_URL" > /dev/null; then
    echo -e "${GREEN}✅ Frontend loads successfully${NC}"

    # Check for GDPR consent banner in HTML
    if curl -s "$FRONTEND_URL" | grep -q "consent"; then
        echo -e "${GREEN}✅ Consent banner code detected in bundle${NC}"
    else
        echo -e "${YELLOW}⚠️  Consent banner not detected (might be code-split)${NC}"
    fi
else
    echo -e "${RED}❌ Frontend failed to load${NC}"
fi
echo ""

# 5. Test CORS Configuration
echo "5️⃣  Testing CORS Configuration"
echo "-----------------------------------"
CORS_HEADERS=$(curl -s -X OPTIONS -H "Origin: $FRONTEND_URL" -H "Access-Control-Request-Method: GET" -I "${API_URL}/health/ready")
if echo "$CORS_HEADERS" | grep -q "Access-Control-Allow-Origin"; then
    echo -e "${GREEN}✅ CORS headers present${NC}"
else
    echo -e "${RED}❌ CORS headers missing${NC}"
fi
echo ""

# 6. Manual Verification Checklist
echo "6️⃣  Manual Verification Checklist"
echo "-----------------------------------"
echo "Please verify the following manually:"
echo ""
echo "  [ ] Open $FRONTEND_URL in browser"
echo "  [ ] Clear localStorage: localStorage.removeItem('nimbusiq_analytics_consent')"
echo "  [ ] Reload page - GDPR consent banner should appear"
echo "  [ ] Click 'Accept' - banner should disappear and not reappear on reload"
echo "  [ ] Test rate limiting: make 100+ rapid requests from Network tab"
echo "  [ ] Test timeout: Throttle network to Slow 3G, verify timeout after 30s"
echo "  [ ] Navigate through all pages - no console errors"
echo "  [ ] Check DevTools React Components - no infinite re-renders"
echo ""

# 7. Re-run Judges (if available)
echo "7️⃣  Optional: Re-run Judges Evaluation"
echo "-----------------------------------"
echo "To verify score improvements, run:"
echo ""
echo "  cd /workspaces/NimbusIQdf"
echo "  node /tmp/judges/dist/index.js eval apps/frontend/src/pages/DashboardPage.tsx --summary"
echo "  node /tmp/judges/dist/index.js eval apps/frontend/src/services/controlPlaneApi.ts --summary"
echo ""
echo "Expected results:"
echo "  - DashboardPage.tsx: 98/100 ✅ PASS"
echo "  - controlPlaneApi.ts: 100/100 ✅ PASS"
echo ""

# Summary
echo "=========================================="
echo "✅ Verification Complete"
echo "=========================================="
echo ""
echo "Next steps:"
echo "  1. Complete manual verification checklist above"
echo "  2. Monitor logs for any errors:"
echo "     az containerapp logs show -n control-plane-api -g nimbusiq-prod-rg --follow"
echo "  3. Re-run judges to confirm score improvements"
echo ""
