#!/bin/sh
# nginx's entrypoint runs this as root before starting. A fresh volume is owned
# by root, and the nginx workers that handle PUT run as the nginx user.
set -e
mkdir -p /data/projects /data/singers /data/.tmp
chown nginx:nginx /data /data/projects /data/singers /data/.tmp
