# Contributing to NimbusIQ

Thanks for your interest in contributing to NimbusIQ! This document outlines how to get started.

## Getting Started

1. **Fork** this repository
2. **Clone** your fork locally
3. **Create a branch** for your change (`git checkout -b feature/my-change`)
4. **Make your changes** and test them
5. **Commit** with a clear message following [Conventional Commits](https://www.conventionalcommits.org/)
6. **Push** your branch and open a **Pull Request**

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js 20+](https://nodejs.org/)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)

### Running Locally

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

### Running Tests

```bash
# Backend tests
cd apps/control-plane-api
dotnet test

cd apps/agent-orchestrator
dotnet test

# Frontend tests
cd apps/frontend
npm test
```

## Code Style

- **C#**: Follow standard .NET conventions. Use meaningful names, keep methods focused.
- **TypeScript/React**: Follow the ESLint configuration in the project. Use Fluent UI v9 components.
- **Bicep**: Follow Azure Verified Module patterns. Use parameterised naming.

## Commit Messages

Follow [Conventional Commits](https://www.conventionalcommits.org/):

```
feat(api): add service group filtering endpoint
fix(drift): correct severity weight calculation
docs(readme): update deployment instructions
test(agents): add WAF agent assessment tests
```

## Pull Requests

- Keep PRs small and focused (< 400 lines when possible)
- Include a clear description of what changed and why
- Add or update tests for new functionality
- Ensure all existing tests pass

## Reporting Issues

- Use GitHub Issues to report bugs or request features
- Include steps to reproduce for bugs
- Include expected vs. actual behaviour

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
