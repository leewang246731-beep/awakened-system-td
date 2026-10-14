#!/usr/bin/env bash
# build_v010.sh — T-096 v0.1.0-alpha Windows 发包流水线（F-141）。
#
# 产物：EraTD-v0.1.0-alpha-win64.zip（Windows x64 免安装包），落
#   ① 本地 release/（gitignored）
#   ② U 盘 E:\game-videos\releases\v0.1.0-alpha\（存在时）
#
# 步骤（与 run_all.sh 同环境纪律：NUGET_PACKAGES/DOTNET_ROOT D 盘兜底）：
#   1) dotnet build 全解决方案 Release（零警告门禁同 G3）
#   2) Godot headless --import（刷新 .godot 导入缓存——冒烟前置于此）
#   3) Godot headless --export-release "Windows Desktop"（模板=junction 到
#      D:\game\tools\godot-templates\4.7.2.stable，见 09-env-locked 环境事实）
#   4) 装配 zip 暂存树：exe + EraTD_Data + game/content（res:// 外内容，
#      SimHost.FindContentDir 向上探测）+ start-game.bat（ERA_DATA_ROOT 注入，
#      D-03 发布=exe 旁 data\）+ 快速开始.txt + LICENSE + ASSETS-LICENSE
#   5) 导出件冒烟：EraTD.exe --headless --quit（release 模板支持，R 标参数）
#      判据=日志含 SMOKE_OK（与 tools/ci/smoke_godot.sh 同判据）
#   6) 打 zip + sha256 清单
#
# 用法： bash tools/release/build_v010.sh
# 前置： Godot 4.7.2 mono + 导出模板就位（07 §三（附）"导出模板"行）。

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SLN="$REPO_ROOT/game/EraTD.sln"
PROJECT_DIR="$REPO_ROOT/game/presentation/EraTD"
GODOT_CONSOLE="$REPO_ROOT/tools/godot_extract/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"

TAG="v0.1.0-alpha"
PKG_NAME="EraTD-v0.1.0-alpha-win64"
OUT_DIR="$REPO_ROOT/release/$TAG"
STAGE="$OUT_DIR/$PKG_NAME"
ZIP_PATH="$OUT_DIR/$PKG_NAME.zip"
USB_DIR="/e/game-videos/releases/$TAG"

RUN_TIMEOUT_SECS=600   # 首次 import+export 慢可接受；10 分钟兜底

# ---- 环境纪律（与 smoke_godot.sh 同源：D 盘兜底，09-env-locked）------------
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
  echo "build_v010: dotnet 不可用（PATH 无 dotnet 且未找到 D 盘 SDK）" >&2
  exit 2
fi
# GodotTools 的 dotnet publish 步骤按 PATH 搜 dotnet 可执行文件（仅
# DOTNET_ROOT 不够——GodotTools.dll 字符串/实测：PATH 无 dotnet 时报
# "Cannot find the dotnet executable"），故 PATH 必须含 D 盘 SDK。
if [ ! -f "$GODOT_CONSOLE" ]; then
  echo "build_v010: 找不到 Godot console exe：$GODOT_CONSOLE（07 §三（附））" >&2
  exit 2
fi
# 导出模板就位检查（junction 已把默认查找路径指到 D 盘登记位置）
TPL_DIR="$APPDATA/Godot/export_templates/4.7.2.stable.mono"
if [ ! -f "$TPL_DIR/windows_release_x86_64.exe" ]; then
  echo "build_v010: 导出模板缺失：$TPL_DIR/windows_release_x86_64.exe" >&2
  echo "  按 07 §三（附）登记位置装模板到 D:\\game\\tools\\godot-templates\\4.7.2.stable" >&2
  exit 2
fi

echo "==> [1/6] dotnet build Release（零警告）"
"$DOTNET_CLI" build "$SLN" -c Release --nologo -p:TreatWarningsAsErrors=true

echo "==> [2/6] Godot headless --import"
if command -v timeout >/dev/null 2>&1; then
  timeout "$RUN_TIMEOUT_SECS" "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" --import || \
    echo "build_v010: import 退出码非 0（非致命告警常见，继续）"
else
  "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" --import || true
fi

echo "==> [3/6] Godot --export-release \"Windows Desktop\""
rm -rf "$STAGE"
mkdir -p "$STAGE"
if command -v timeout >/dev/null 2>&1; then
  timeout "$RUN_TIMEOUT_SECS" "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" \
    --export-release "Windows Desktop" "$STAGE/EraTD.exe"
else
  "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" \
    --export-release "Windows Desktop" "$STAGE/EraTD.exe"
fi
if [ ! -f "$STAGE/EraTD.exe" ]; then
  echo "build_v010: 导出失败——未产出 EraTD.exe" >&2
  exit 1
fi

echo "==> [4/6] 装配发布树（content/启动器/许可/说明）"
mkdir -p "$STAGE/game"
cp -r "$REPO_ROOT/game/content" "$STAGE/game/content"
cp "$REPO_ROOT/tools/release/templates/start-game.bat" "$STAGE/start-game.bat"
cp "$REPO_ROOT/tools/release/templates/快速开始.txt" "$STAGE/快速开始.txt"
cp "$REPO_ROOT/LICENSE" "$STAGE/LICENSE"
cp "$REPO_ROOT/ASSETS-LICENSE" "$STAGE/ASSETS-LICENSE"

echo "==> [5/6] 导出件冒烟（headless --quit，判据=SMOKE_OK）"
SMOKE_DATA="$(mktemp -d -t era_v010_smoke_XXXXXX)"
SMOKE_LOG="$SMOKE_DATA/run.log"
if ( cd "$STAGE" && ERA_DATA_ROOT="$SMOKE_DATA" ./EraTD.exe --headless --quit \
       --log-file "$(cygpath -w "$SMOKE_LOG" 2>/dev/null || echo "$SMOKE_LOG")" ); then
  SMOKE_RC=0
else
  SMOKE_RC=$?
fi
if ! grep -q "SMOKE_OK" "$SMOKE_LOG" 2>/dev/null; then
  echo "build_v010: 导出件冒烟未捕获 SMOKE_OK（rc=$SMOKE_RC，日志 $SMOKE_LOG）" >&2
  tail -n 20 "$SMOKE_LOG" 2>/dev/null || true
  exit 1
fi
grep "SMOKE_OK" "$SMOKE_LOG"
rm -rf "$SMOKE_DATA"

echo "==> [6/6] 打 zip + sha256"
rm -f "$ZIP_PATH"
( cd "$OUT_DIR" && powershell -NoProfile -Command \
    "Compress-Archive -Path '$PKG_NAME' -DestinationPath '$PKG_NAME.zip'" )
SHA256_FILE="$OUT_DIR/$PKG_NAME.zip.sha256"
sha256sum "$ZIP_PATH" | sed "s|$REPO_ROOT/||" > "$SHA256_FILE"
cat "$SHA256_FILE"
ls -la "$ZIP_PATH"

# U 盘同步（业主交付物约定：成片/发布物落 E:\game-videos）
if [ -d /e/game-videos ]; then
  mkdir -p "$USB_DIR"
  cp "$ZIP_PATH" "$SHA256_FILE" "$USB_DIR/"
  echo "build_v010: 已同步到 U 盘 $USB_DIR"
else
  echo "build_v010: U 盘 E: 不在位——跳过同步（仅本地 $ZIP_PATH）"
fi

echo "build_v010: 完成——$ZIP_PATH"
