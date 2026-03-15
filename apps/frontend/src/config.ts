export interface FrontendConfig {
  apiBaseUrl: string;
  gitOps: {
    repositoryUrl?: string;
    targetBranch: string;
    componentName: string;
    componentVersion: string;
  };
  auth: {
    enabled: boolean;
    tenantId?: string;
    clientId?: string;
    apiScope?: string;
  };
}

function toBool(value: unknown, defaultValue: boolean): boolean {
  if (value === undefined || value === null) return defaultValue;
  if (typeof value === 'boolean') return value;
  if (typeof value !== 'string') return defaultValue;
  return value.toLowerCase() === 'true';
}

export const config: FrontendConfig = {
  apiBaseUrl: import.meta.env.VITE_CONTROL_PLANE_API_BASE_URL ?? '/api/v1',
  gitOps: {
    repositoryUrl: import.meta.env.VITE_GITOPS_REPOSITORY_URL,
    targetBranch: import.meta.env.VITE_GITOPS_TARGET_BRANCH ?? "main",
    componentName: import.meta.env.VITE_GITOPS_COMPONENT_NAME ?? "nimbusiq-control-plane",
    componentVersion: import.meta.env.VITE_GITOPS_COMPONENT_VERSION ?? "v1",
  },
  auth: {
    // Dev mode: disable auth if VITE_AUTH_ENABLED is explicitly false
    // (allows testing API without Azure AD configured)
    enabled: toBool(import.meta.env.VITE_AUTH_ENABLED, false),
    tenantId: import.meta.env.VITE_AZURE_AD_TENANT_ID,
    clientId: import.meta.env.VITE_AZURE_AD_CLIENT_ID,
    apiScope: import.meta.env.VITE_CONTROL_PLANE_API_SCOPE,
  },
};
