#!/bin/bash
# Get the Aspire MCP API key from the running Dashboard process
# Usage: ./scripts/get-aspire-mcp-key.sh

KEY=$(ps eww $(pgrep -f 'Aspire.Dashboard.dll') 2>/dev/null | tr ' ' '\n' | grep DASHBOARD__MCP__PRIMARYAPIKEY | cut -d= -f2)

if [ -z "$KEY" ]; then
    echo "ERROR: Aspire Dashboard not running. Start with: cd src/SentenceStudio.AppHost && aspire run" >&2
    exit 1
fi

echo "$KEY"
