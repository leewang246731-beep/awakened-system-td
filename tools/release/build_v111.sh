#!/usr/bin/env bash
# build_v111.sh — T-156 v1.1.1 完全体双平台发包流水线（build_v100 同链）。
#
# build_v050.sh（T-107 预览版）的同链升级：同环境纪律/同导出链/同冒烟判据，
# 差量=①版本面升 v1.1.1（W9 完全体：T-154~T-160 家族（战役宿主脸/六时代/联机/程序关/无尽定版）
# ；②版本面预检（export_presets.cfg product_version
# 必须=v1.1.1，防旧版本面出包）；③冒烟显示加战役/演出/ART 标记（门禁判据仍 SMOKE_OK）。
#
# 产物（本地 release/（gitignored）+ U 盘 E:\game-videos\releases\v1.1.1\）：
#   ① EraTD-v1.1.1-win64.zip（Windows 正片）
#   ② EraTD-v1.1.1-linux64.zip（Linux 正片，带执行位）
#
# 用法： bash tools/release/build_v111.sh
# 前置： Godot 4.7.2 mono + 导出模板就位（含 linux_release.x86_64）。

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SLN="$REPO_ROOT/game/EraTD.sln"
PROJECT_DIR="$REPO_ROOT/game/presentation/EraTD"
GODOT_CONSOLE="$REPO_ROOT/tools/godot_extract/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"

TAG="v1.1.1"
WIN_NAME="EraTD-v1.1.1-win64"
LINUX_NAME="EraTD-v1.1.1-linux64"
OUT_DIR="$REPO_ROOT/release/$TAG"
WIN_STAGE="$OUT_DIR/$WIN_NAME"
LINUX_STAGE="$OUT_DIR/$LINUX_NAME"
WIN_ZIP="$OUT_DIR/$WIN_NAME.zip"
LINUX_ZIP="$OUT_DIR/$LINUX_NAME.zip"
USB_DIR="/e/game-videos/releases/$TAG"

RUN_TIMEOUT_SECS=900   # import/export 慢可接受；15 分钟兜底

# ---- 环境纪律（与 build_v050.sh 同源：D 盘兜底，09-env-locked）----------------
if [ -z "${DOTNET_ROOT:-}" ] && [ -d /d/dotnet ]; then
  export DOTNET_ROOT='D:\dotnet'
fi
if [ -z "${NUGET_PACKAGES:-}" ] && [ -d /d/nuget ]; then
  export NUGET_PACKAGES='D:\nuget'
fi
if command -v dotnet >/dev/null 2>&1; then
  DOTNET_CLI="dotnet"
elif [ -x /d/dotnet/dotnet.exe ]; then
  DOTNET_CLI=/d/dotnet/dotnet.exe
  export PATH="/d/dotnet:$PATH"
else
  echo "build_v111: dotnet 不可用（PATH 无 dotnet 且未找到 D 盘 SDK）" >&2
  exit 2
fi
if [ ! -f "$GODOT_CONSOLE" ]; then
  echo "build_v111: 找不到 Godot console exe：$GODOT_CONSOLE（07 §三（附））" >&2
  exit 2
fi
TPL_DIR="$APPDATA/Godot/export_templates/4.7.2.stable.mono"
for tpl in windows_release_x86_64.exe linux_release.x86_64; do
  if [ ! -f "$TPL_DIR/$tpl" ]; then
    echo "build_v111: 导出模板缺失：$TPL_DIR/$tpl" >&2
    exit 2
  fi
done

# ---- 版本面预检（v1.0 专属：防旧版本面出包）-----------------------------------
if ! grep -q 'application/product_version="v1.1.1"' "$PROJECT_DIR/export_presets.cfg"; then
  echo "build_v111: export_presets.cfg product_version 不是 v1.1.1——先升版本面" >&2
  exit 2
fi

# ---- 发布素材就位预检（同 v0.5 硬前置）---------------------------------------
AGG_PNG_COUNT=$(find "$REPO_ROOT/game/assets/expand-v1/P1" \
    "$REPO_ROOT/game/assets/expand-v1/P3" \
    "$REPO_ROOT/game/assets/expand-v1/P4" -name "*.png" 2>/dev/null | wc -l)
if [ "$AGG_PNG_COUNT" -ne 73 ]; then
  echo "build_v111: expand-v1 聚合图应为 73（49 插画+24 图标），实得 $AGG_PNG_COUNT" >&2
  exit 2
fi
OGV_COUNT=$(find "$REPO_ROOT/game/assets/cinematics" -name "*.ogv" | wc -l)
if [ "$OGV_COUNT" -ne 52 ]; then
  echo "build_v111: cinematics ogv 应为 52（51 条+《零的真相》），实得 $OGV_COUNT" >&2
  exit 2
fi

PY_BIN=""
for cand in python python3; do
  if command -v "$cand" >/dev/null 2>&1; then PY_BIN="$cand"; break; fi
done
if [ -z "$PY_BIN" ]; then
  echo "build_v111: python 不可用（Linux zip 打包依赖 zipfile）" >&2
  exit 2
fi

godot_headless() {  # godot_headless <timeout-secs> <args...>
  local secs="$1"; shift
  if command -v timeout >/dev/null 2>&1; then
    timeout "$secs" "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" "$@"
  else
    "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" "$@"
  fi
}

echo "==> [1/5] dotnet build Release（零警告）"
"$DOTNET_CLI" build "$SLN" -c Release --nologo -p:TreatWarningsAsErrors=true

echo "==> [2/5] Godot headless --import"
godot_headless "$RUN_TIMEOUT_SECS" --import || \
  echo "build_v111: import 退出码非 0（非致命告警常见，继续）"

stage_common() {  # stage_common <stage-dir>：res:// 外内容树（两平台同构）
  local stage="$1"
  mkdir -p "$stage/game/assets"
  cp -r "$REPO_ROOT/game/content" "$stage/game/content"
  cp -r "$REPO_ROOT/game/assets/cinematics" "$stage/game/assets/cinematics"
  cp -r "$REPO_ROOT/game/assets/expand-v1" "$stage/game/assets/expand-v1"
  cp "$REPO_ROOT/LICENSE" "$stage/LICENSE"
  cp "$REPO_ROOT/ASSETS-LICENSE" "$stage/ASSETS-LICENSE"
  cp "$REPO_ROOT/tools/release/templates/快速开始.txt" "$stage/快速开始.txt"
}

echo "==> [3/5] Windows 导出+装配+冒烟+zip"
rm -rf "$WIN_STAGE"
mkdir -p "$WIN_STAGE"
godot_headless "$RUN_TIMEOUT_SECS" --export-release "Windows Desktop" "$WIN_STAGE/EraTD.exe"
if [ ! -f "$WIN_STAGE/EraTD.exe" ]; then
  echo "build_v111: Windows 导出失败——未产出 EraTD.exe" >&2
  exit 1
fi
stage_common "$WIN_STAGE"
cp "$REPO_ROOT/tools/release/templates/start-game.bat" "$WIN_STAGE/start-game.bat"

SMOKE_DATA="$(mktemp -d -t era_v100_smoke_XXXXXX)"
SMOKE_LOG="$SMOKE_DATA/run.log"
if ( cd "$WIN_STAGE" && ERA_DATA_ROOT="$SMOKE_DATA" ./EraTD.exe --headless --quit \
       --log-file "$(cygpath -w "$SMOKE_LOG" 2>/dev/null || echo "$SMOKE_LOG")" ); then
  SMOKE_RC=0
else
  SMOKE_RC=$?
fi
if ! grep -q "SMOKE_OK" "$SMOKE_LOG" 2>/dev/null; then
  echo "build_v111: Windows 导出件冒烟未捕获 SMOKE_OK（rc=$SMOKE_RC，日志 $SMOKE_LOG）" >&2
  tail -n 20 "$SMOKE_LOG" 2>/dev/null || true
  exit 1
fi
grep -E "SMOKE_OK|EXPAND_OK|ART4|BELT4|CAMPAIGN_OK|CINE_OK" "$SMOKE_LOG" || true
rm -rf "$SMOKE_DATA"

zip_channel() {  # zip_channel <stage> <zip>：裸根 zip（butler 通道惯例）
  local stage="$1" zip="$2"
  rm -f "$zip"
  "$PY_BIN" - "$stage" "$zip" <<'PYEOF'
import os, sys, zipfile

stage, dest = sys.argv[1], sys.argv[2]

def unix_mode(rel):
    if rel.endswith(".x86_64") or rel.endswith(".sh"):
        return 0o755
    return 0o644

count = 0
with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for root, dirs, files in os.walk(stage):
        dirs.sort()
        for name in sorted(files):
            full = os.path.join(root, name)
            rel = os.path.relpath(full, stage).replace(os.sep, "/")
            zi = zipfile.ZipInfo.from_file(full, rel)
            zi.external_attr = unix_mode(rel) << 16
            zi.compress_type = zipfile.ZIP_DEFLATED
            with open(full, "rb") as fh:
                z.writestr(zi, fh.read())
            count += 1
print(f"channel-zip: {count} files -> {dest}")
PYEOF
}

zip_channel "$WIN_STAGE" "$WIN_ZIP"
sha256sum "$WIN_ZIP" | sed "s|$REPO_ROOT/||" > "$WIN_ZIP.sha256"
cat "$WIN_ZIP.sha256"
ls -la "$WIN_ZIP"

echo "==> [4/5] Linux 导出+装配+结构校验+zip（带执行位）"
rm -rf "$LINUX_STAGE"
mkdir -p "$LINUX_STAGE"
godot_headless "$RUN_TIMEOUT_SECS" --export-release "Linux" "$LINUX_STAGE/EraTD.x86_64"
if [ ! -f "$LINUX_STAGE/EraTD.x86_64" ]; then
  echo "build_v111: Linux 导出失败——未产出 EraTD.x86_64" >&2
  exit 1
fi
stage_common "$LINUX_STAGE"
tr -d '\r' < "$REPO_ROOT/tools/release/templates/start-game.sh" > "$LINUX_STAGE/start-game.sh"

"$PY_BIN" - "$LINUX_STAGE" "$LINUX_ZIP" <<'PYEOF'
import os, sys, zipfile

stage, dest = sys.argv[1], sys.argv[2]

with open(os.path.join(stage, "EraTD.x86_64"), "rb") as f:
    magic = f.read(4)
assert magic == b"\x7fELF", f"EraTD.x86_64 非 ELF（magic={magic!r}）"
data_dir = os.path.join(stage, "data_EraTD_linuxbsd_x86_64")  # Godot 平台内部名=linuxbsd
assert os.path.isdir(data_dir), f"缺 mono 数据目录 {data_dir}"
assert os.path.isfile(os.path.join(data_dir, "EraTD.dll")), "data 目录缺 EraTD.dll"
assert os.path.isfile(os.path.join(data_dir, "libcoreclr.so")), "data 目录缺 libcoreclr.so（linux 自包含运行时）"
for probe in ("game/content", "game/assets/cinematics", "game/assets/expand-v1",
              "start-game.sh", "LICENSE", "ASSETS-LICENSE"):
    assert os.path.exists(os.path.join(stage, probe)), f"发布树缺 {probe}"
PYEOF

zip_channel "$LINUX_STAGE" "$LINUX_ZIP"
sha256sum "$LINUX_ZIP" | sed "s|$REPO_ROOT/||" > "$LINUX_ZIP.sha256"
cat "$LINUX_ZIP.sha256"
ls -la "$LINUX_ZIP"

echo "==> [5/5] U 盘同步"
if [ -d /e/game-videos ]; then
  mkdir -p "$USB_DIR"
  cp "$WIN_ZIP" "$WIN_ZIP.sha256" "$LINUX_ZIP" "$LINUX_ZIP.sha256" "$USB_DIR/"
  echo "build_v111: 已同步到 U 盘 $USB_DIR"
else
  echo "build_v111: U 盘 E: 不在位——跳过同步（仅本地 $OUT_DIR）"
fi

echo "build_v111: 完成——$WIN_ZIP + $LINUX_ZIP"
exit 0
