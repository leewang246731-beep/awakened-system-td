#!/usr/bin/env bash
# build_v120_dev.sh — T-162（08 v1.31 W10 B 批）裸包验证发包流水线
# （build_v111.sh 家族的 Windows 单平台精简形态）。
#
# 用途：产出本地裸导出包（release/v1.2.0-dev/T-162-bare-win64/），供本批
# 「导出包实测复验」门禁在裸包目录里跑 --shot 截图钩子（三证=六时代色按钮
# 卡有色差+A-01 视频背景活着+选关卡片网格有 banner 缩略图）。
#
# 与 build_v111.sh 的差量：
#   - 版本面复用 v1.1.1 参数（product_version 不动——升版面归主管发版链，
#     任务卡允许「复用既有脚本参数」）；
#   - 仅 Windows 通道（裸包截图门禁单平台足够；Linux/zip/USB 同步裁掉）；
#   - 发布素材预检按 73e1219 后的实际资产面（banner 31 张 [29 关+2 模式卡]
#     +expand-v1 聚合图 97 张 P1/P3/P4）。
#
# 用法： bash tools/release/build_v120_dev.sh
# 前置： Godot 4.7.2 mono + Windows 导出模板就位。

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SLN="$REPO_ROOT/game/EraTD.sln"
PROJECT_DIR="$REPO_ROOT/game/presentation/EraTD"
GODOT_CONSOLE="$REPO_ROOT/tools/godot_extract/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"

TAG="v1.2.0-dev"            # 开发周期标记（目录名）；版本面=export_presets v1.1.1 复用
STAGE_NAME="T-162-bare-win64"
OUT_DIR="$REPO_ROOT/release/$TAG"
STAGE="$OUT_DIR/$STAGE_NAME"

RUN_TIMEOUT_SECS=900

# ---- 环境纪律（与 build_v111.sh 同源：D 盘兜底，09-env-locked）----------------
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
  echo "build_v120_dev: dotnet 不可用（PATH 无 dotnet 且未找到 D 盘 SDK）" >&2
  exit 2
fi
if [ ! -f "$GODOT_CONSOLE" ]; then
  echo "build_v120_dev: 找不到 Godot console exe：$GODOT_CONSOLE（07 §三（附））" >&2
  exit 2
fi
TPL_DIR="$APPDATA/Godot/export_templates/4.7.2.stable.mono"
if [ ! -f "$TPL_DIR/windows_release_x86_64.exe" ]; then
  echo "build_v120_dev: 导出模板缺失：$TPL_DIR/windows_release_x86_64.exe" >&2
  exit 2
fi

# ---- 发布素材就位预检（73e1219 后资产面）---------------------------------------
BANNER_COUNT=$(find "$REPO_ROOT/game/assets/expand-v1/P1/banners" -name "*.png" | wc -l)
if [ "$BANNER_COUNT" -ne 31 ]; then
  echo "build_v120_dev: 选关 banner 应为 31（29 关+2 模式卡），实得 $BANNER_COUNT" >&2
  exit 2
fi
AGG_PNG_COUNT=$(find "$REPO_ROOT/game/assets/expand-v1/P1" \
    "$REPO_ROOT/game/assets/expand-v1/P3" \
    "$REPO_ROOT/game/assets/expand-v1/P4" -name "*.png" 2>/dev/null | wc -l)
if [ "$AGG_PNG_COUNT" -ne 97 ]; then
  echo "build_v120_dev: expand-v1 聚合图应为 97（51 P1+6 P3+40 P4），实得 $AGG_PNG_COUNT" >&2
  exit 2
fi
OGV_COUNT=$(find "$REPO_ROOT/game/assets/cinematics" -name "*.ogv" | wc -l)
if [ "$OGV_COUNT" -ne 52 ]; then
  echo "build_v120_dev: cinematics ogv 应为 52（51 条+《零的真相》），实得 $OGV_COUNT" >&2
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

echo "==> [1/4] dotnet build Release（零警告）"
"$DOTNET_CLI" build "$SLN" -c Release --nologo -p:TreatWarningsAsErrors=true

echo "==> [2/4] Godot headless --import"
godot_headless "$RUN_TIMEOUT_SECS" --import || \
  echo "build_v120_dev: import 退出码非 0（非致命告警常见，继续）"

echo "==> [3/4] Windows 导出+装配（res:// 外内容树随包 exe 旁）"
rm -rf "$STAGE"
mkdir -p "$STAGE"
godot_headless "$RUN_TIMEOUT_SECS" --export-release "Windows Desktop" "$STAGE/EraTD.exe"
if [ ! -f "$STAGE/EraTD.exe" ]; then
  echo "build_v120_dev: Windows 导出失败——未产出 EraTD.exe" >&2
  exit 1
fi
mkdir -p "$STAGE/game/assets"
cp -r "$REPO_ROOT/game/content" "$STAGE/game/content"
cp -r "$REPO_ROOT/game/assets/cinematics" "$STAGE/game/assets/cinematics"
cp -r "$REPO_ROOT/game/assets/expand-v1" "$STAGE/game/assets/expand-v1"
cp "$REPO_ROOT/LICENSE" "$STAGE/LICENSE"
cp "$REPO_ROOT/ASSETS-LICENSE" "$STAGE/ASSETS-LICENSE"
cp "$REPO_ROOT/tools/release/templates/快速开始.txt" "$STAGE/快速开始.txt"
cp "$REPO_ROOT/tools/release/templates/start-game.bat" "$STAGE/start-game.bat"

echo "==> [4/4] 裸包冒烟（SMOKE_OK 门禁，launcher 形态 ERA_DATA_ROOT）"
SMOKE_DATA="$(mktemp -d -t era_t162_smoke_XXXXXX)"
SMOKE_LOG="$SMOKE_DATA/run.log"
if ( cd "$STAGE" && ERA_DATA_ROOT="$SMOKE_DATA" ./EraTD.exe --headless --quit \
       --log-file "$(cygpath -w "$SMOKE_LOG" 2>/dev/null || echo "$SMOKE_LOG")" ); then
  SMOKE_RC=0
else
  SMOKE_RC=$?
fi
if ! grep -q "SMOKE_OK" "$SMOKE_LOG" 2>/dev/null; then
  echo "build_v120_dev: 裸包冒烟未捕获 SMOKE_OK（rc=$SMOKE_RC，日志 $SMOKE_LOG）" >&2
  tail -n 20 "$SMOKE_LOG" 2>/dev/null || true
  exit 1
fi
grep -E "SMOKE_OK|CAMPAIGN_OK|EXPAND_OK" "$SMOKE_LOG" || true
rm -rf "$SMOKE_DATA"

echo "build_v120_dev: 完成——裸包目录 $STAGE（版本面=v1.1.1 参数复用；T-162 导出复验承载件）"
exit 0
