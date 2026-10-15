#!/usr/bin/env bash
# build_web.sh — T-106 (F-144/F-164) HTML5 发包流水线（threads-off，无
# SharedArrayBuffer 依赖，任意静态托管可玩——不需 COOP/COEP 响应头）。
#
# 【引擎现状实证（2026-10-15，REV-20261016-T106-web-export §2）】
# Godot 4.7.2-stable_mono（D-04 钉死工具链）的编辑器对 Web 导出一律拒绝：
#   ERROR: Cannot export project with preset "Web" due to configuration errors:
#   Exporting to Web is currently not supported in Godot 4 when using C#/.NET.
#   Use Godot 3 to target Web with C#/Mono instead.
#   （若项目不用 C#，请改用非 C# 编辑器构建导出。）
# 实验证据：EraTD（C# 项目）与一个纯 GDScript 空项目同样被拒——mono 编辑器
# 无论如何都导不出 Web；官方 mono 导出模板 tpz（27 条目逐一核对）不含任何
# web*.zip 模板件。上游跟踪：godotengine/godot#70796（重加 C#→Web 支持）。
#
# 本脚本的纪律（反造假红线）：导出被引擎拒绝 ⇒ 打印引擎原文并 exit 1，
# 绝不产出/绝不发布任何"伪 Web 包"。步骤 5 起的 no-SAB 验证/本地 HTTP 冒烟/
# 打 zip/U 盘同步在引擎支持落地（换钉版本或官方模板到位）后自动生效——
# 本脚本是同一条流水线，不是占位符。
#
# threads-off 由 presentation/EraTD/export_presets.cfg preset.1 固化
# （variant/thread_support=false ⇒ 无 SharedArrayBuffer/crossOriginIsolated
# 依赖 ⇒ 不需 COOP/COEP；F-144 验收口径）。
#
# 用法： bash tools/release/build_web.sh
# 产物： release/web-preview/EraTD-web-preview.zip（+ .sha256），
#        同步 E:\game-videos\releases\web-preview\（E 盘不在位则警告跳过）。

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SLN="$REPO_ROOT/game/EraTD.sln"
PROJECT_DIR="$REPO_ROOT/game/presentation/EraTD"
GODOT_CONSOLE="$REPO_ROOT/tools/godot_extract/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"

TAG="web-preview"
PKG_NAME="EraTD-web-preview"
OUT_DIR="$REPO_ROOT/release/$TAG"
STAGE="$OUT_DIR/$PKG_NAME"
ZIP_PATH="$OUT_DIR/$PKG_NAME.zip"
USB_DIR="/e/game-videos/releases/$TAG"
SMOKE_PORT=8000
RUN_TIMEOUT_SECS=900   # import+export 慢可接受；15 分钟兜底

# ---- 环境纪律（与 build_v010.sh 同源：D 盘兜底，09-env-locked）----------------
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
  echo "build_web: dotnet 不可用（PATH 无 dotnet 且未找到 D 盘 SDK）" >&2
  exit 2
fi
if [ ! -f "$GODOT_CONSOLE" ]; then
  echo "build_web: 找不到 Godot console exe：$GODOT_CONSOLE（07 §三（附））" >&2
  exit 2
fi

echo "==> [1/6] dotnet build Release（零警告）"
"$DOTNET_CLI" build "$SLN" -c Release --nologo -p:TreatWarningsAsErrors=true

echo "==> [2/6] Godot headless --import"
if command -v timeout >/dev/null 2>&1; then
  timeout "$RUN_TIMEOUT_SECS" "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" --import || \
    echo "build_web: import 退出码非 0（非致命告警常见，继续）"
else
  "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" --import || true
fi

echo "==> [3/6] Godot --export-release \"Web\"（threads-off）"
rm -rf "$STAGE"
mkdir -p "$STAGE"
EXPORT_LOG="$OUT_DIR/export-web.log"
mkdir -p "$OUT_DIR"
EXPORT_RC=0
if command -v timeout >/dev/null 2>&1; then
  timeout "$RUN_TIMEOUT_SECS" "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" \
    --export-release "Web" "$STAGE/index.html" 2>&1 | tee "$EXPORT_LOG" || EXPORT_RC=$?
else
  "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" \
    --export-release "Web" "$STAGE/index.html" 2>&1 | tee "$EXPORT_LOG" || EXPORT_RC=$?
fi

if [ ! -f "$STAGE/index.html" ]; then
  echo "" >&2
  echo "build_web: 导出未产出 index.html（rc=$EXPORT_RC）——引擎拒绝原文如下：" >&2
  grep -A3 "Cannot export project with preset" "$EXPORT_LOG" >&2 || true
  echo "" >&2
  echo "build_web: 【已知引擎阻断】Godot 4.7.2-mono 拒绝 C#/.NET 项目的 Web 导出" >&2
  echo "  （上游跟踪 godotengine/godot#70796；证据与决策选项见" >&2
  echo "   docs/engineering/reviews/REV-20261016-T106-web-export.md）。" >&2
  echo "  F-144 的 threads-off preset（export_presets.cfg preset.1）与 Web 约束" >&2
  echo "  代码路径（WEB_OK 冒烟段）已就位；本脚本在引擎支持落地当日自动转绿。" >&2
  echo "  按反造假纪律不产出任何伪 Web 包。" >&2
  exit 1
fi

echo "==> [4/6] no-SAB 验证（threads-off ⇒ 无 SharedArrayBuffer/COI 依赖）"
SAB_HITS="$(grep -rloE "SharedArrayBuffer|crossOriginIsolated|coi-serviceworker" \
  "$STAGE" 2>/dev/null || true)"
if [ -n "$SAB_HITS" ]; then
  echo "build_web: threads-off 产物仍引用 SAB/COI（违反 F-144）：" >&2
  echo "$SAB_HITS" >&2
  exit 1
fi
echo "build_web: no-SAB 验证通过——任意静态托管可直接服务（不需 COOP/COEP 头）"

echo "==> [5/6] 本地 Web 冒烟（python http.server:$SMOKE_PORT + curl HEAD）"
PYTHON_SM=""
for cand in python python3; do
  if command -v "$cand" >/dev/null 2>&1; then PYTHON_SM="$cand"; break; fi
done
if [ -z "$PYTHON_SM" ]; then
  echo "build_web: python 不可用——HTTP 冒烟降级为文件存在性检查" >&2
  ls "$STAGE"
else
  ( cd "$STAGE" && "$PYTHON_SM" -m http.server "$SMOKE_PORT" \
      >/dev/null 2>&1 & echo $! > "$OUT_DIR/http.pid" )
  sleep 2
  SM_FAIL=0
  # 主文档 200 + index.html 内引用的每个产物（.js/.wasm/.pck/…）逐一 HEAD 200
  if ! curl -sI -o /dev/null -w "%{http_code}" "http://127.0.0.1:$SMOKE_PORT/index.html" | grep -q 200; then
    echo "build_web: index.html HEAD 非 200" >&2; SM_FAIL=1
  fi
  for ref in $(grep -oE '(src|href)="[^"]+"' "$STAGE/index.html" \
      | sed -E 's/^(src|href)="//; s/"$//' | grep -vE '^https?://' | sort -u); do
    code="$(curl -sI -o /dev/null -w "%{http_code}" "http://127.0.0.1:$SMOKE_PORT/$ref")"
    if [ "$code" != "200" ]; then
      echo "build_web: 引用产物 HEAD $code —— $ref" >&2; SM_FAIL=1
    fi
  done
  kill "$(cat "$OUT_DIR/http.pid")" 2>/dev/null || true
  rm -f "$OUT_DIR/http.pid"
  if [ "$SM_FAIL" -ne 0 ]; then
    echo "build_web: 本地 Web 冒烟失败（见上）" >&2
    exit 1
  fi
  echo "build_web: curl 冒烟通过（index.html + 全部引用产物 200）"
  echo "build_web: 浏览器级冒烟（canvas 出现/页面 console SMOKE_OK）请在本地起" >&2
  echo "  python -m http.server 后人工/Playwright 复核——本轮引擎未产出包，无从执行" >&2
fi

echo "==> [6/6] 打 zip + sha256 + U 盘同步"
( cd "$OUT_DIR" && powershell -NoProfile -Command \
    "Compress-Archive -Path '$PKG_NAME' -DestinationPath '$PKG_NAME.zip'" )
SHA256_FILE="$OUT_DIR/$PKG_NAME.zip.sha256"
sha256sum "$ZIP_PATH" | sed "s|$REPO_ROOT/||" > "$SHA256_FILE"
cat "$SHA256_FILE"
ls -la "$ZIP_PATH"

if [ -d /e/game-videos ]; then
  mkdir -p "$USB_DIR"
  cp "$ZIP_PATH" "$SHA256_FILE" "$USB_DIR/"
  echo "build_web: 已同步到 U 盘 $USB_DIR"
else
  echo "build_web: U 盘 E: 不在位——跳过同步（仅本地 $OUT_DIR）"
fi

echo "build_web: 完成——$ZIP_PATH"
exit 0
