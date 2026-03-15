FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore apps/agent-orchestrator/src/Orchestration/Atlas.AgentOrchestrator.Orchestration.csproj
RUN dotnet publish apps/agent-orchestrator/src/Orchestration/Atlas.AgentOrchestrator.Orchestration.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM node:24-bookworm-slim AS azure_mcp
RUN npm install -g @azure/mcp@latest

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# Azure MCP stdio mode requires Node.js 20+ and the global MCP package layout
# (including symlinks and wrapper-relative files), so copy /usr/local wholesale.
COPY --from=azure_mcp /usr/local /usr/local

ENV HOME=/tmp
ENV NPM_CONFIG_CACHE=/tmp/.npm

COPY --from=build /app/publish .

USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "Atlas.AgentOrchestrator.Orchestration.dll", "--health"] || exit 1

ENTRYPOINT ["dotnet", "Atlas.AgentOrchestrator.Orchestration.dll"]
