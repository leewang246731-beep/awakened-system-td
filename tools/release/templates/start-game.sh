#!/bin/sh
# EraTD launcher (T-107, F-145) — Linux twin of start-game.bat: the shipped
# binary must NOT write to the pinned development data root, so point the
# D-03 injected root at the exe-adjacent "data" directory first.
DIR="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$DIR/data"
export ERA_DATA_ROOT="$DIR/data"
exec "$DIR/EraTD.x86_64" "$@"
