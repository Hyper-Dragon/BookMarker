#!/usr/bin/env bash
# BookMarker container healthcheck: GET /health over /dev/tcp (no curl/wget needed at runtime).
#
# Asserts a marker in the response BODY, not just the status line: a bare 200 would also be
# satisfied by some other process that happened to hold the port, which is exactly the kind
# of health check that teaches an operator to ignore health.
exec 3<>/dev/tcp/127.0.0.1/"${PORT:-8080}" || exit 1
printf 'GET /health HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3
grep -q '"status":"healthy"' <&3
