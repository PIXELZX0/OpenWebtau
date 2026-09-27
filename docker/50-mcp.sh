#!/bin/sh
# Starts the MCP server beside nginx, which proxies /mcp to it on 127.0.0.1:5178.
set -e
su -s /bin/sh nginx -c 'node /opt/openwebtau/mcp/server.mjs --http' &
