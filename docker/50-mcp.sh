#!/bin/sh
# Starts the password gate (127.0.0.1:5179) and the MCP server (127.0.0.1:5178)
# beside nginx, which proxies /auth/ and /mcp to them. The MCP server starts the
# headless host (/opt/openwebtau/headless) on /data when no editor tab is open.
set -e
su -s /bin/sh nginx -c 'node /opt/openwebtau/auth.mjs' &
su -s /bin/sh nginx -c 'OPENWEBTAU_DATA_DIR=/data node /opt/openwebtau/server.mjs --http' &
