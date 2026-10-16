#!/usr/bin/env bash
# BookMarker container healthcheck: GET /health over /dev/tcp (no curl/wget needed at runtime).
exec 3<>/dev/tcp/127.0.0.1/"${PORT:-8080}"
printf 'GET /health HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3
head -1 <&3 | grep -q ' 200 '
