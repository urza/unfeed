#!/usr/bin/env bash
set -euo pipefail
FEED_INSTANCE="${FEED_DATA:-$(pwd)/data}"
FEED_XDISPLAY="${FEED_DISPLAY:-:99}"
FEED_VNC_PORT="${NOVNC_PORT:-6901}"
FEED_RFB_PORT="${VNC_RFB_PORT:-5900}"
mkdir -p "$FEED_INSTANCE/logs" "$FEED_INSTANCE/locks"
if [ "${1:-}" = stop ]; then
  for service in novnc x11vnc xvfb; do
    file="$FEED_INSTANCE/locks/$service.pid"
    if [ -f "$file" ]; then
      read -r process_id < "$file"
      if [ -r "/proc/$process_id/cmdline" ] && tr '\0' ' ' < "/proc/$process_id/cmdline" | grep -Eq '(websockify|x11vnc|Xvfb)'; then kill "$process_id"; fi
      rm -f "$file"
    fi
  done
  exit 0
fi
start() {
  local service="$1"; shift
  local file="$FEED_INSTANCE/locks/$service.pid"
  if [ -f "$file" ] && kill -0 "$(cat "$file")" 2>/dev/null; then return; fi
  setsid nohup "$@" > "$FEED_INSTANCE/logs/$service.log" 2>&1 &
  echo "$!" > "$file"
}
start xvfb Xvfb "$FEED_XDISPLAY" -screen 0 1600x1000x24 -nolisten tcp
sleep 1
start x11vnc env -u WAYLAND_DISPLAY x11vnc -display "$FEED_XDISPLAY" -forever -shared -nopw -localhost -rfbport "$FEED_RFB_PORT"
start novnc websockify --web /usr/share/novnc "0.0.0.0:$FEED_VNC_PORT" "localhost:$FEED_RFB_PORT"
sleep 1
for service in xvfb x11vnc novnc; do
  if ! kill -0 "$(cat "$FEED_INSTANCE/locks/$service.pid")" 2>/dev/null; then
    cat "$FEED_INSTANCE/logs/$service.log" >&2
    exit 1
  fi
done
printf 'noVNC ready: http://127.0.0.1:%s/vnc.html?autoconnect=1&resize=scale\n' "$FEED_VNC_PORT"
