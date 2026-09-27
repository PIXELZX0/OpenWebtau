#!/bin/sh
# Starts the password gate (127.0.0.1:5179) and the MCP server (127.0.0.1:5178)
# beside nginx, which proxies /auth/ and /mcp to them.
set -e
su -s /bin/sh nginx -c 'node /opt/openwebtau/auth.mjs' &
su -s /bin/sh nginx -c 'node /opt/openwebtau/server.mjs --http' &
