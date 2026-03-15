#!/usr/bin/env bash
set -euo pipefail

user_data_dir="${HOME}/.vscode-remote/data/User"
workspace_storage_dir="${user_data_dir}/workspaceStorage"
logs_dir="${HOME}/.vscode-remote/data/logs"
mcp_config_path="${user_data_dir}/mcp.json"

echo "== MCP configuration =="
if [[ -f "${mcp_config_path}" ]]; then
  node -e '
    const fs = require("fs");
    const path = process.argv[1];
    const data = JSON.parse(fs.readFileSync(path, "utf8"));
    const servers = Object.keys(data.servers || {});
    if (servers.length === 0) {
      console.log("No MCP servers configured.");
    } else {
      for (const name of servers) {
        const server = data.servers[name] || {};
        const target = server.url || [server.command, ...(server.args || [])].join(" ");
        console.log(`- ${name}: ${target}`);
      }
    }
  ' "${mcp_config_path}"
else
  echo "No MCP config found at ${mcp_config_path}."
fi

latest_debug_log=""
if [[ -d "${workspace_storage_dir}" ]]; then
  latest_debug_log=$(find "${workspace_storage_dir}" -path '*GitHub.copilot-chat/debug-logs/*/main.jsonl' -type f -print 2>/dev/null | xargs -r ls -t 2>/dev/null | head -n 1)
fi

echo
echo "== Latest Copilot session =="
if [[ -n "${latest_debug_log}" && -f "${latest_debug_log}" ]]; then
  echo "Debug log: ${latest_debug_log}"
  echo
  echo "Tool calls:"
  grep -oE '"type":"tool_call","name":"[^"]+"' "${latest_debug_log}" \
    | sed -E 's/.*"name":"([^"]+)"/\1/' \
    | sort \
    | uniq -c \
    | sort -nr

  azure_count=$(grep -c '"name":"mcp_azure_' "${latest_debug_log}" || true)
  context7_count=$(grep -c '"name":"mcp_context7_' "${latest_debug_log}" || true)
  search_subagent_count=$(grep -c '"name":"search_subagent"' "${latest_debug_log}" || true)
  run_subagent_count=$(grep -c '"name":"runSubagent"' "${latest_debug_log}" || true)

  echo
  echo "Summary:"
  echo "- Azure MCP tool calls: ${azure_count}"
  echo "- Context7 MCP tool calls: ${context7_count}"
  echo "- search_subagent calls: ${search_subagent_count}"
  echo "- named runSubagent calls: ${run_subagent_count}"
else
  echo "No Copilot debug log found yet. Run a chat request first."
fi

latest_chat_log=""
if [[ -d "${logs_dir}" ]]; then
  latest_chat_log=$(find "${logs_dir}" -path '*GitHub Copilot Chat.log' -type f -print 2>/dev/null | xargs -r ls -t 2>/dev/null | head -n 1)
fi

echo
echo "== Latest chat log markers =="
if [[ -n "${latest_chat_log}" && -f "${latest_chat_log}" ]]; then
  echo "Chat log: ${latest_chat_log}"
  grep -E '\[searchSubagentTool\]|MCP server started' "${latest_chat_log}" | tail -n 20 || true
else
  echo "No GitHub Copilot Chat.log found."
fi
