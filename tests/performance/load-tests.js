// k6 Load Test Suite for NimbusIQ Platform
// Run: k6 run tests/performance/load-tests.js

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

// Custom metrics
const errorRate = new Rate('errors');
const analysisLatency = new Trend('analysis_latency');
const recommendationLatency = new Trend('recommendation_latency');
const iacLatency = new Trend('iac_latency');
const timelineLatency = new Trend('timeline_latency');

// Configuration from environment
const API_URL = __ENV.API_URL || 'https://control-plane-api.azurecontainerapps.io';
const TOKEN = __ENV.TOKEN || 'test-token';
const API_VERSION = '2026-02-16';

// Test scenarios
export const options = {
  scenarios: {
    // Scenario 1: Service Group Analysis Load Test
    analysis: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '2m', target: 10 },   // Ramp up to 10
        { duration: '5m', target: 100 },  // Sustain 100 concurrent analyses
        { duration: '2m', target: 0 },    // Ramp down
      ],
      gracefulRampDown: '30s',
      exec: 'testAnalysis',
    },
    
    // Scenario 2: Recommendation Query Load Test
    recommendations: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '1m', target: 20 },
        { duration: '5m', target: 50 },
        { duration: '1m', target: 0 },
      ],
      gracefulRampDown: '30s',
      exec: 'testRecommendations',
    },
    
    // Scenario 3: IaC Generation Load Test
    iac: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '2m', target: 10 },
        { duration: '4m', target: 50 },
        { duration: '2m', target: 0 },
      ],
      gracefulRampDown: '30s',
      exec: 'testIacGeneration',
    },
    
    // Scenario 4: Timeline Query Load Test
    timeline: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '1m', target: 50 },
        { duration: '3m', target: 200 },
        { duration: '1m', target: 0 },
      ],
      gracefulRampDown: '30s',
      exec: 'testTimeline',
    },
  },
  
  thresholds: {
    // Global thresholds
    'errors': ['rate<0.01'],  // < 1% errors
    'http_req_duration': ['p(95)<2000'],  // 95% of requests under 2s
    
    // Scenario-specific thresholds
    'analysis_latency': ['p(50)<200', 'p(95)<500', 'p(99)<1000'],
    'recommendation_latency': ['p(50)<150', 'p(95)<300'],
    'iac_latency': ['p(50)<500', 'p(95)<2000'],
    'timeline_latency': ['p(50)<100', 'p(95)<200'],
  },
};

// Helper function for authenticated requests
function makeRequest(method, url, body = null) {
  const params = {
    headers: {
      'Authorization': `Bearer ${TOKEN}`,
      'Content-Type': 'application/json',
    },
    tags: { name: url },
  };
  
  if (body) {
    return http.request(method, url, JSON.stringify(body), params);
  }
  return http.request(method, url, null, params);
}

// Scenario 1: Service Group Analysis
export function testAnalysis() {
  const serviceGroupId = `test-group-${__VU}`;
  
  // Step 1: Create service group
  let res = makeRequest(
    'POST',
    `${API_URL}/api/v1/service-groups?api-version=${API_VERSION}`,
    {
      id: serviceGroupId,
      name: `Load Test Group ${__VU}`,
      subscriptionId: '00000000-0000-0000-0000-000000000000',
      resourceGroupPattern: 'rg-test-*',
    }
  );
  
  check(res, {
    'create group status 201': (r) => r.status === 201,
  }) || errorRate.add(1);
  
  sleep(1);
  
  // Step 2: Start analysis
  const startTime = Date.now();
  res = makeRequest(
    'POST',
    `${API_URL}/api/v1/service-groups/${serviceGroupId}/analysis?api-version=${API_VERSION}`
  );
  
  const duration = Date.now() - startTime;
  analysisLatency.add(duration);
  
  const success = check(res, {
    'analysis status 202': (r) => r.status === 202,
    'has operation-location': (r) => r.headers['Operation-Location'] !== undefined,
    'has Retry-After': (r) => r.headers['Retry-After'] !== undefined,
  });
  
  if (!success) {
    errorRate.add(1);
  }
  
  // Step 3: Poll for completion (simplified for load test)
  if (res.status === 202 && res.headers['Operation-Location']) {
    sleep(2);
    const operationUrl = res.headers['Operation-Location'];
    res = makeRequest('GET', operationUrl);
    
    check(res, {
      'operation status 200 or 202': (r) => r.status === 200 || r.status === 202,
    }) || errorRate.add(1);
  }
  
  sleep(1);
}

// Scenario 2: Recommendation Queries
export function testRecommendations() {
  const serviceGroupId = `test-group-${__VU % 20}`;  // Reuse groups
  
  const startTime = Date.now();
  const res = makeRequest(
    'GET',
    `${API_URL}/api/v1/recommendations?serviceGroupId=${serviceGroupId}&api-version=${API_VERSION}`
  );
  
  const duration = Date.now() - startTime;
  recommendationLatency.add(duration);
  
  const success = check(res, {
    'recommendations status 200': (r) => r.status === 200,
    'has value array': (r) => {
      try {
        const body = JSON.parse(r.body);
        return Array.isArray(body.value);
      } catch {
        return false;
      }
    },
  });
  
  if (!success) {
    errorRate.add(1);
  }
  
  sleep(0.5);
}

// Scenario 3: IaC Generation
export function testIacGeneration() {
  const recommendationId = `rec-${__VU}`;
  
  const startTime = Date.now();
  const res = makeRequest(
    'POST',
    `${API_URL}/api/v1/iac/generate-preview?api-version=${API_VERSION}`,
    {
      recommendationId: recommendationId,
      targetFormat: 'bicep',
    }
  );
  
  const duration = Date.now() - startTime;
  iacLatency.add(duration);
  
  const success = check(res, {
    'iac preview status 202': (r) => r.status === 202,
    'has operation-location': (r) => r.headers['Operation-Location'] !== undefined,
  });
  
  if (!success) {
    errorRate.add(1);
  }
  
  sleep(2);
}

// Scenario 4: Timeline Queries
export function testTimeline() {
  const serviceGroupId = `test-group-${__VU % 50}`;  // Reuse groups
  
  const startTime = Date.now();
  const res = makeRequest(
    'GET',
    `${API_URL}/api/v1/audit/timeline/${serviceGroupId}?api-version=${API_VERSION}`
  );
  
  const duration = Date.now() - startTime;
  timelineLatency.add(duration);
  
  const success = check(res, {
    'timeline status 200': (r) => r.status === 200,
    'has serviceGroupId': (r) => {
      try {
        const body = JSON.parse(r.body);
        return body.serviceGroupId !== undefined;
      } catch {
        return false;
      }
    },
    'has events': (r) => {
      try {
        const body = JSON.parse(r.body);
        return body.totalEvents !== undefined;
      } catch {
        return false;
      }
    },
  });
  
  if (!success) {
    errorRate.add(1);
  }
  
  sleep(0.3);
}

// Summary handler
export function handleSummary(data) {
  return {
    'stdout': textSummary(data, { indent: ' ', enableColors: true }),
    'performance-results.json': JSON.stringify(data),
  };
}

function textSummary(data, options) {
  const indent = options.indent || '';
  const colors = options.enableColors ? {
    reset: '\x1b[0m',
    green: '\x1b[32m',
    red: '\x1b[31m',
    yellow: '\x1b[33m',
    cyan: '\x1b[36m',
  } : {
    reset: '', green: '', red: '', yellow: '', cyan: '',
  };
  
  let output = `\n${colors.cyan}═══════════════════════════════════════════════════════════${colors.reset}\n`;
  output += `${colors.cyan}  NimbusIQ Platform - Performance Test Results${colors.reset}\n`;
  output += `${colors.cyan}═══════════════════════════════════════════════════════════${colors.reset}\n\n`;
  
  // Scenario summaries
  for (const [name, metrics] of Object.entries(data.metrics)) {
    if (name.endsWith('_latency')) {
      const scenario = name.replace('_latency', '');
      output += `${indent}${colors.yellow}${scenario.toUpperCase()} Scenario:${colors.reset}\n`;
      output += `${indent}  P50: ${metrics.values.p50.toFixed(2)}ms\n`;
      output += `${indent}  P95: ${metrics.values.p95.toFixed(2)}ms\n`;
      output += `${indent}  P99: ${metrics.values.p99.toFixed(2)}ms\n`;
      output += `${indent}  Avg: ${metrics.values.avg.toFixed(2)}ms\n\n`;
    }
  }
  
  // Overall stats
  const httpReqs = data.metrics.http_reqs;
  const httpDuration = data.metrics.http_req_duration;
  const errors = data.metrics.errors;
  
  output += `${indent}${colors.yellow}Overall Statistics:${colors.reset}\n`;
  output += `${indent}  Total Requests: ${httpReqs.values.count}\n`;
  output += `${indent}  Error Rate: ${(errors.values.rate * 100).toFixed(2)}%\n`;
  output += `${indent}  Avg Duration: ${httpDuration.values.avg.toFixed(2)}ms\n`;
  output += `${indent}  P95 Duration: ${httpDuration.values.p95.toFixed(2)}ms\n\n`;
  
  // Threshold results
  output += `${indent}${colors.yellow}Threshold Results:${colors.reset}\n`;
  for (const [name, threshold] of Object.entries(data.thresholds)) {
    const status = threshold.ok ? colors.green + '✓' : colors.red + '✗';
    output += `${indent}  ${status} ${name}${colors.reset}\n`;
  }
  
  output += `\n${colors.cyan}═══════════════════════════════════════════════════════════${colors.reset}\n`;
  
  return output;
}
