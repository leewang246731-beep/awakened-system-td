#!/usr/bin/env bash
# setup_local_nuget.sh — 从钉死的 Godot 4.7.2 mono 编辑器发行包镜像本地 NuGet feed（任务 T-028/T-030；D-04）。
#
# 为什么需要这个脚本：
#   NuGet.org 不托管 Godot.NET.Sdk 4.7.x。全解决方案钉死 Godot.NET.Sdk /
#   GodotSharp / Godot.SourceGenerators 恰好 4.7.2（与编辑器/导出模板/gdUnit4Net
#   四对齐，07-decided-plan D-04）。编辑器发行包自带官方构建的 nupkg，
#   本脚本把它们复制进本地 feed 目录 tools/nuget-local/，game/nuget.config
#   已把源 `godot-local-4.7.2` 指向该目录（nuget.org 仍保留给 xUnit 等常规包）。
#
# 纪律（07-decided-plan §三（附）环境事实登记）：
#   - 一切载荷只落 D 盘（禁 C 盘缓存；restore 前必须 NUGET_PACKAGES=D:\nuget）；
#   - 脚本按仓库相对路径解析，不改机器；C 盘检出直接拒绝执行。
#
# 用法： bash tools/ci/setup_local_nuget.sh

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
EDITOR_DIST="$REPO_ROOT/tools/godot_extract/Godot_v4.7.2-stable_mono_win64"
NUPKG_SRC="$EDITOR_DIST/GodotSharp/Tools/nupkgs"
FEED_DIR="$REPO_ROOT/tools/nuget-local"
PINNED_VERSION="4.7.2"

EXPECTED_NUPKGS=(
  "Godot.NET.Sdk.$PINNED_VERSION.nupkg"
  "Godot.SourceGenerators.$PINNED_VERSION.nupkg"
  "GodotSharp.$PINNED_VERSION.nupkg"
  "GodotSharpEditor.$PINNED_VERSION.nupkg"
)

# —— D 盘纪律：拒绝 C 盘检出（user:// 之外的另一条 C 盘违约路径是包缓存）——
repo_win="$(cd "$REPO_ROOT" && pwd -W 2>/dev/null || pwd)"
case "$repo_win" in
  [Cc]:/*|[Cc]:\\*)
    echo "setup_local_nuget: 拒绝在 C 盘运行（仓库位于 $repo_win；D 盘纪律见 07-decided-plan §三（附））" >&2
    exit 1
    ;;
esac

if [ ! -d "$NUPKG_SRC" ]; then
  echo "setup_local_nuget: 找不到编辑器自带 nupkg 目录：$NUPKG_SRC" >&2
  echo "  请先按 07-decided-plan §三（附）环境事实表解压 Godot ${PINNED_VERSION}-stable mono 到 tools/godot_extract/。" >&2
  exit 1
fi

mkdir -p "$FEED_DIR"
copied=0
for nupkg in "${EXPECTED_NUPKGS[@]}"; do
  src="$NUPKG_SRC/$nupkg"
  if [ ! -f "$src" ]; then
    echo "setup_local_nuget: 缺少 $nupkg（编辑器发行包不完整）" >&2
    exit 1
  fi
  cp -f "$src" "$FEED_DIR/"
  copied=$((copied + 1))
  echo "  已镜像 $nupkg"
done

echo "setup_local_nuget: $copied 个 nupkg 已就位 → $FEED_DIR"
echo "  下一步：export NUGET_PACKAGES='D:\nuget' 后 dotnet restore game/EraTD.sln"
