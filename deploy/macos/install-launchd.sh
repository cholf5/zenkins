#!/usr/bin/env bash
#
# I-003: install/uninstall the published Zenkins Server as a launchd agent (macOS).
#
#   install-launchd.sh install   <zenkins-home> [port]
#   install-launchd.sh uninstall
#   install-launchd.sh status
#
# <zenkins-home> is the deployment directory (the publish output copied there); the job keeps its
# data (App_Data: database, key ring, artifact store) inside it, and uninstalling the service never
# deletes the data. The agent runs in the user's login session (gui domain), so no root is needed;
# a system daemon (LaunchDaemon in /Library/LaunchDaemons) is the same plist with RunAtLoad already
# satisfied at boot - see the card notes for what was verified on the machine at hand.
set -euo pipefail

LABEL="com.zenkins.server"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE="$SCRIPT_DIR/com.zenkins.server.plist"
PLIST_DIR="$HOME/Library/LaunchAgents"
PLIST="$PLIST_DIR/$LABEL.plist"

uid="$(id -u)"

case "${1:-}" in
  install)
    home="${2:?usage: install-launchd.sh install <zenkins-home> [port]}"
    port="${3:-5000}"
    home="$(cd "$home" && pwd)"

    [ -x "$home/Zenkins.Server" ] || {
      echo "error: $home/Zenkins.Server is not executable - publish first (scripts/publish.sh)" >&2
      exit 1
    }

    mkdir -p "$home/logs" "$PLIST_DIR"
    sed -e "s|@ZENKINS_HOME@|$home|g" -e "s|@ZENKINS_PORT@|$port|g" "$TEMPLATE" > "$PLIST"

    # Replace a possibly-loaded older definition, then load the new one.
    launchctl bootout "gui/$uid/$LABEL" 2>/dev/null || true
    launchctl bootstrap "gui/$uid" "$PLIST"
    echo "installed: $LABEL ($home, port $port) - data lives in $home/App_Data"
    ;;

  uninstall)
    launchctl bootout "gui/$uid/$LABEL" 2>/dev/null || true
    rm -f "$PLIST"
    echo "uninstalled: $LABEL (the deployment directory and its App_Data were left in place)"
    ;;

  status)
    launchctl print "gui/$uid/$LABEL" | grep -E "state|pid|program" || true
    ;;

  *)
    echo "usage: install-launchd.sh {install <zenkins-home> [port] | uninstall | status}" >&2
    exit 1
    ;;
esac
