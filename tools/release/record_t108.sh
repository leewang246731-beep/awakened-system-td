#!/usr/bin/env bash
# record_t108.sh — T-108 商店物料录制+后期一键流水线（E 泳道）。
#
# 复现「真实录屏」全链（反造假口径：每一步产物可 ffprobe 实测）：
#   1) 建立 EraRecord harness 的资源 junction（指向真项目目录，绝不入库）；
#   2) dotnet build harness（=真项目 presentation 源码原样编译+EraCore 引用）；
#   3) Godot 4.7.2 内建 --write-movie 录 60 s 母片（窗口模式，含真游戏音频）；
#      录制前清 hint-progress（新玩家首见语义，运行时数据非仓库内容）；
#   4) python tools/release/store_materials.py 后期（GIF×3/MP4/胶囊/截图/角标）；
#   5) 母片+日志归档 E 盘（E:\game-videos\t108-store\），成品镜像 E:\game-assets\store\。
#
# 用法： bash tools/release/record_t108.sh [--skip-record]   # 已有母片时只做后期
# 环境纪律：NUGET_PACKAGES=D:\nuget、DOTNET_ROOT=D:\dotnet（09 号环境事实）。

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HARNESS="$REPO_ROOT/tools/release/record/EraRecord"
GODOT_CONSOLE="$REPO_ROOT/tools/godot_extract/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
MASTER="$HARNESS/out/master_final.avi"
E_MASTERS="/e/game-videos/t108-store"
E_MIRROR="/e/game-assets/store"

export NUGET_PACKAGES='D:\nuget'
export DOTNET_ROOT='D:\dotnet'
DOTNET_CLI="$(command -v dotnet || true)"
[ -n "$DOTNET_CLI" ] || [ -x /d/dotnet/dotnet.exe ] && DOTNET_CLI="${DOTNET_CLI:-/d/dotnet/dotnet.exe}"

for dir in textures shaders audio scenes scripts cinematics narrative expand; do
  if [ ! -e "$HARNESS/$dir" ]; then
    cmd //c "mklink /J $dir D:\\game\\game\\presentation\\EraTD\\$dir" >/dev/null
    echo "record_t108: junction $dir 建立"
  fi
done

echo "record_t108: 构建 harness（真项目源码原样编译）…"
"$DOTNET_CLI" build "$HARNESS" -v q --nologo

if [ "${1:-}" != "--skip-record" ]; then
  echo "record_t108: 录制母片 60 s（--write-movie，窗口模式）…"
  rm -f "$REPO_ROOT/game/data/settings/hint-progress.json"   # 新玩家首见语义
  "$GODOT_CONSOLE" --path "$HARNESS" \
    --write-movie "$(cygpath -w "$MASTER" 2>/dev/null || echo "D:/game/tools/release/record/EraRecord/out/master_final.avi")" \
    -- 2>&1 | tee "$HARNESS/out/master_final.log" | grep -E "RECORD_(WAVE_BEGIN|ORDER_CONFIRMED|CARRIER|DONE)|Done recording" || true
fi

[ -f "$MASTER" ] || { echo "record_t108: 母片缺失（先去掉 --skip-record 跑录制）" >&2; exit 1; }

echo "record_t108: 后期（GIF/MP4/胶囊/截图+版本角标）…"
python "$REPO_ROOT/tools/release/store_materials.py"

echo "record_t108: E 盘归档…"
mkdir -p "$E_MASTERS" "$E_MIRROR"
cp -f "$MASTER" "$HARNESS/out/master_final.log" "$E_MASTERS/"
cp -f "$REPO_ROOT"/game/assets/store/* "$E_MIRROR/"

echo "record_t108: 完成。成品=game/assets/store/（git add 该路径+tools/release+tools/ci+REV）。"
