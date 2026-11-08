#!/usr/bin/env bash
# Headless in-game screenshot of a map: logs a test account in with xdotool clicks, waits, takes a screenshot.
#   tools/mapedit/maptest.sh <out.png> [load_wait_seconds=90]
#
# You need: a running server, a test account with one character whose DB row has MapId set to the map to test, and a
# client copy (never your original install) in a 1280x720 window on an X display. The click positions below are for
# that window size.
#
# Required env:
#   MAPTEST_USER, MAPTEST_PASS   the test account
#   MAPTEST_LAUNCH               your script that starts the client (run in the background; it sets its own WINEPREFIX)
# Optional env:
#   MAPTEST_DISPLAY              X display of the client (default :99)
#   MAPTEST_CONTAINER            run xdotool and import in this docker container instead of on the host
#   MAPTEST_WINEPREFIX           after the screenshot, stop only this prefix's wine processes (wineserver -k)
#   MS2_OUT                      where the client log goes (see tools/modtools_config.py)
#
# Timings: title ~60 s, login ~30 s, then <load_wait> after Start.
# Set WINEDEBUG=+debugstr in your launch script to capture the game's OutputDebugString in the client log.
set -u
R=$(cd "$(dirname "$0")/../.." && pwd)
OUT=${1:?out.png}; WAIT=${2:-90}
: "${MAPTEST_USER:?set MAPTEST_USER (test account)}" "${MAPTEST_PASS:?set MAPTEST_PASS}" "${MAPTEST_LAUNCH:?set MAPTEST_LAUNCH (your script that starts the client)}"
D=${MAPTEST_DISPLAY:-:99}
LOGDIR=$(python3 "$R/tools/modtools_config.py" MS2_OUT || echo "$R/out")
mkdir -p "$LOGDIR"
in_x() {
    if [ -n "${MAPTEST_CONTAINER:-}" ]; then docker exec "$MAPTEST_CONTAINER" env DISPLAY="$D" "$@"; else env DISPLAY="$D" "$@"; fi
}
X() { in_x xdotool "$@"; }
(nohup bash "$MAPTEST_LAUNCH" > "$LOGDIR/maptest_client.log" 2>&1 &)
sleep 60                                   # title screen
X mousemove 680 581 click 1; sleep 1; X type --delay 80 "$MAPTEST_USER"
X mousemove 680 609 click 1; sleep 1; X type --delay 80 "$MAPTEST_PASS"
X mousemove 638 675 click 1; sleep 30      # login -> character select
X mousemove 1120 644 click 1               # Start
sleep "$WAIT"
if [ -n "${MAPTEST_CONTAINER:-}" ]; then
    in_x import -window root /tmp/maptest.png && docker cp "$MAPTEST_CONTAINER:/tmp/maptest.png" "$OUT"
else
    in_x import -window root "$OUT"
fi
echo "screenshot: $OUT"
if [ -n "${MAPTEST_WINEPREFIX:-}" ]; then WINEPREFIX=$MAPTEST_WINEPREFIX wineserver -k; sleep 2; fi
