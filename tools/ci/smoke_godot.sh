#!/usr/bin/env bash
# smoke_godot.sh — T-029 headless 冒烟（Main.cs 注释引用的本脚本，round 1 补齐）。
#
# 三合一验证（game/presentation/EraTD/scripts/Main.cs）：
#   1) 钉死的 Godot 4.7.2 mono 运行时能无头启动；
#   2) 托管程序集（EraTD.dll，含 EraCore 项目引用）真实加载；
#   3) EraCore 链路活着——版本号由 EraCore.VersionInfo 打出。
# 成功判据：输出含 SMOKE_OK（Main._Ready 打印，随后 Quit(0)）。
#
# 纪律：Godot console exe 与项目均按仓库相对路径解析；缓存/日志走临时目录；
#   NUGET_PACKAGES 未设时提醒（D 盘纪律，07-decided-plan §三（附））。
#
# 用法： bash tools/ci/smoke_godot.sh

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GODOT_CONSOLE="$REPO_ROOT/tools/godot_extract/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
PROJECT_DIR="$REPO_ROOT/game/presentation/EraTD"
RUN_TIMEOUT_SECS=300   # 首次 import 慢可接受；5 分钟兜底

if [ ! -f "$GODOT_CONSOLE" ]; then
  echo "smoke_godot: 找不到 Godot console 可执行文件：$GODOT_CONSOLE" >&2
  echo "  请按 07-decided-plan §三（附）环境事实表放置钉死的 4.7.2-stable mono 编辑器。" >&2
  exit 1
fi

# Godot(mono) 启动托管程序集时须能定位 .NET 运行时，否则报
# "Unable to load .NET runtime/hostfxr"。环境事实（09-env-locked.md）：
# DOTNET_ROOT=D:\dotnet 已设用户级环境变量（2026-10-13），但先于该设置
# 启动的 shell 看不到——脚本内兜底再导出一次（Windows 形式，原生 exe 直接可用）。
if [ -z "${DOTNET_ROOT:-}" ] && [ -d /d/dotnet ]; then
  export DOTNET_ROOT='D:\dotnet'
  echo "smoke_godot: 已兜底导出 DOTNET_ROOT=$DOTNET_ROOT"
fi

# dotnet：PATH 优先，回退到环境事实登记的 D:\dotnet\dotnet.exe
if command -v dotnet >/dev/null 2>&1; then
  DOTNET_CLI="dotnet"
elif [ -x /d/dotnet/dotnet.exe ]; then
  DOTNET_CLI=/d/dotnet/dotnet.exe
else
  echo "smoke_godot: dotnet 不可用（PATH 无 dotnet 且未找到 /d/dotnet/dotnet.exe）" >&2
  exit 1
fi

if [ -z "${NUGET_PACKAGES:-}" ]; then
  echo "smoke_godot: 提示：NUGET_PACKAGES 未设置，按环境事实应设为 D:\\nuget（C 盘缓存违纪）"
fi

echo "smoke_godot: 构建 presentation（EraTD，含 EraCore 引用）…"
"$DOTNET_CLI" build "$PROJECT_DIR" -v q --nologo

LOG="$(mktemp -t era_smoke_XXXXXX.log)"
trap 'rm -f "$LOG"' EXIT

run_godot() { # $1 = 额外参数（--import 或 --quit）
  if command -v timeout >/dev/null 2>&1; then
    timeout "$RUN_TIMEOUT_SECS" "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" "$1" >"$LOG" 2>&1
  else
    "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" "$1" >"$LOG" 2>&1
  fi
}

echo "smoke_godot: 首次资源 import（可重复执行）…"
if ! run_godot --import; then
  echo "smoke_godot: import 阶段退出码非 0（首次 import 的非致命告警常见，继续跑主场景）"
fi

echo "smoke_godot: 无头运行主场景（main.tscn → Main._Ready）…"
run_godot --quit

echo "---- Godot 输出（末 30 行）----"
tail -n 30 "$LOG"
echo "------------------------------"

if grep -q "SMOKE_OK" "$LOG"; then
  echo "smoke_godot: SMOKE_OK 已捕获——运行时/程序集/EraCore 链路三合一通过"
  grep "SMOKE_OK" "$LOG"
  exit 0
fi

echo "smoke_godot: 未捕获 SMOKE_OK（见上方完整输出）" >&2
exit 1
