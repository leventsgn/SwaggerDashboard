#!/bin/sh
set -e

APP_UID=10001

# A mounted volume arrives owned by root, so the unprivileged application could not create
# its SQLite database or its data protection keys inside it. Ownership is corrected here and
# privileges are then dropped: the application process itself never runs as root.
if [ "$(id -u)" = "0" ]; then
    if [ -n "${DATA_DIR:-}" ]; then
        mkdir -p "$DATA_DIR"
        chown -R "$APP_UID:$APP_UID" "$DATA_DIR"
    fi

    if command -v setpriv > /dev/null 2>&1; then
        exec setpriv --reuid="$APP_UID" --regid="$APP_UID" --clear-groups "$@"
    fi

    # Refusing to start would be worse than running with the privileges the platform gave
    # us, but the operator needs to know it happened.
    echo "UYARI: setpriv bulunamadi, uygulama root olarak calisiyor." >&2
fi

exec "$@"
