#!/usr/bin/env bash
# build_v120.sh — v1.2.0 完全体双平台发包流水线（build_v111 同链升级）。
#
# 差量=①版本面 v1.2.0（W10 界面美术大修：T-161~T-164 四批——主菜单/选关卡片/
#   战斗 HUD 图标化/皮肤手感；美术资产 135 件）；②发布素材预检按 73e1219 后
#   实际资产面（expand-v1 聚合图 97=73+24 新 banner；ogv 52 不变）；③新增
#   三道发版门禁（W10 起）：窗口存活（v1.1.1 教训）+玩家视角截图三段（菜单/
#   选关/战斗）+出厂裸包三证（仓库树外裸包再截——防美术降级面回归）。
#
# 产物（本地 release/（gitignored）+ U 盘 E:\game-videos\releases\v1.2.0\）：
#   ① EraTD-v1.2.0-win64.zip   ② EraTD-v1.2.0-linux64.zip
#   ③ docs/screenshots/v120-release/{win,bare}/ 发版截图（门禁证据）
#
# 用法： bash tools/release/build_v120.sh
# 前置： Godot 4.7.2 mono + 双平台导出模板就位。

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SLN="$REPO_ROOT/game/EraTD.sln"
PROJECT_DIR="$REPO_ROOT/game/presentation/EraTD"
GODOT_CONSOLE="$REPO_ROOT/tools/godot_extract/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"

TAG="v1.2.0"
WIN_NAME="EraTD-v1.2.0-win64"
LINUX_NAME="EraTD-v1.2.0-linux64"
OUT_DIR="$REPO_ROOT/release/$TAG"
WIN_STAGE="$OUT_DIR/$WIN_NAME"
LINUX_STAGE="$OUT_DIR/$LINUX_NAME"
WIN_ZIP="$OUT_DIR/$WIN_NAME.zip"
LINUX_ZIP="$OUT_DIR/$LINUX_NAME.zip"
USB_DIR="/e/game-videos/releases/$TAG"
SHOT_DIR="$REPO_ROOT/docs/screenshots/v120-release"

RUN_TIMEOUT_SECS=900   # import/export 慢可接受；15 分钟兜底

# ---- 环境纪律（D 盘兜底，09-env-locked）--------------------------------------
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
  echo "build_v120: dotnet 不可用（PATH 无 dotnet 且未找到 D 盘 SDK）" >&2
  exit 2
fi
if [ ! -f "$GODOT_CONSOLE" ]; then
  echo "build_v120: 找不到 Godot console exe：$GODOT_CONSOLE（07 §三（附））" >&2
  exit 2
fi
TPL_DIR="$APPDATA/Godot/export_templates/4.7.2.stable.mono"
for tpl in windows_release_x86_64.exe linux_release.x86_64; do
  if [ ! -f "$TPL_DIR/$tpl" ]; then
    echo "build_v120: 导出模板缺失：$TPL_DIR/$tpl" >&2
    exit 2
  fi
done

# ---- 版本面预检（防旧版本面出包）---------------------------------------------
if ! grep -q 'application/product_version="v1.2.0"' "$PROJECT_DIR/export_presets.cfg"; then
  echo "build_v120: export_presets.cfg product_version 不是 v1.2.0——先升版本面" >&2
  exit 2
fi

# ---- 发布素材就位预检（73e1219 后实际资产面）---------------------------------
AGG_PNG_COUNT=$(find "$REPO_ROOT/game/assets/expand-v1/P1" \
    "$REPO_ROOT/game/assets/expand-v1/P3" \
    "$REPO_ROOT/game/assets/expand-v1/P4" -name "*.png" 2>/dev/null | wc -l)
if [ "$AGG_PNG_COUNT" -ne 97 ]; then
  echo "build_v120: expand-v1 聚合图应为 97（v1.1.1 的 73 + W10 新增 24 banner），实得 $AGG_PNG_COUNT" >&2
  exit 2
fi
BANNER_COUNT=$(find "$REPO_ROOT/game/assets/expand-v1/P1/banners" -name "*.png" | wc -l)
if [ "$BANNER_COUNT" -ne 31 ]; then
  echo "build_v120: banner 应为 31（29 关全覆盖+2 模式卡），实得 $BANNER_COUNT" >&2
  exit 2
fi
OGV_COUNT=$(find "$REPO_ROOT/game/assets/cinematics" -name "*.ogv" | wc -l)
if [ "$OGV_COUNT" -ne 52 ]; then
  echo "build_v120: cinematics ogv 应为 52（51 条+《零的真相》），实得 $OGV_COUNT" >&2
  exit 2
fi

PY_BIN=""
for cand in python python3; do
  if command -v "$cand" >/dev/null 2>&1 && "$cand" --version >/dev/null 2>&1; then
    PY_BIN="$cand"; break
  fi
done
if [ -z "$PY_BIN" ]; then
  echo "build_v120: python 不可用（Linux zip 打包依赖 zipfile；注意 WindowsApps stub 不算）" >&2
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

echo "==> [1/7] dotnet build Release（零警告）"
"$DOTNET_CLI" build "$SLN" -c Release --nologo -p:TreatWarningsAsErrors=true

echo "==> [2/7] Godot headless --import"
godot_headless "$RUN_TIMEOUT_SECS" --import || \
  echo "build_v120: import 退出码非 0（非致命告警常见，继续）"

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

echo "==> [3/7] Windows 导出+装配+冒烟+zip"
rm -rf "$WIN_STAGE"
mkdir -p "$WIN_STAGE"
godot_headless "$RUN_TIMEOUT_SECS" --export-release "Windows Desktop" "$WIN_STAGE/EraTD.exe"
if [ ! -f "$WIN_STAGE/EraTD.exe" ]; then
  echo "build_v120: Windows 导出失败——未产出 EraTD.exe" >&2
  exit 1
fi
stage_common "$WIN_STAGE"
cp "$REPO_ROOT/tools/release/templates/start-game.bat" "$WIN_STAGE/start-game.bat"

SMOKE_DATA="$(mktemp -d -t era_v120_smoke_XXXXXX)"
SMOKE_LOG="$SMOKE_DATA/run.log"
if ( cd "$WIN_STAGE" && ERA_DATA_ROOT="$SMOKE_DATA" ./EraTD.exe --headless --quit \
       --log-file "$(cygpath -w "$SMOKE_LOG" 2>/dev/null || echo "$SMOKE_LOG")" ); then
  SMOKE_RC=0
else
  SMOKE_RC=$?
fi
if ! grep -q "SMOKE_OK" "$SMOKE_LOG" 2>/dev/null; then
  echo "build_v120: Windows 导出件冒烟未捕获 SMOKE_OK（rc=$SMOKE_RC，日志 $SMOKE_LOG）" >&2
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

echo "==> [4/7] 发版门禁 A：窗口存活（v1.1.1 教训——无参玩家车道 25s 不自退）"
ALIVE_DATA="$(mktemp -d -t era_v120_alive_XXXXXX)"
set +e
# 判据锚=stdout 捕获（Godot --log-file 在 TERM 强杀下不落盘——实测 0 字节；
# 门禁行 MENU_BOOT/RENDER_PROBE 走 stdout 稳定可达）
( cd "$WIN_STAGE" && ERA_DATA_ROOT="$ALIVE_DATA" timeout 25 ./EraTD.exe ) \
    > "$ALIVE_DATA/stdout.log" 2>&1
ALIVE_RC=$?
set -e
if [ "$ALIVE_RC" -ne 124 ]; then
  echo "build_v120: 窗口存活门禁红——25s 内自行退出（rc=$ALIVE_RC，日志 $ALIVE_DATA/stdout.log）" >&2
  tail -n 20 "$ALIVE_DATA/stdout.log" 2>/dev/null || true
  exit 1
fi
if grep -E "L2_FAIL" "$ALIVE_DATA/stdout.log" >/dev/null 2>&1; then
  echo "build_v120: 窗口存活日志含 L2_FAIL 异常痕迹" >&2
  exit 1
fi
if ! grep -qE "MENU_BOOT|RENDER_PROBE" "$ALIVE_DATA/stdout.log"; then
  echo "build_v120: 窗口存活 stdout 未捕获 MENU_BOOT/RENDER_PROBE 探针行" >&2
  exit 1
fi
grep -E "MENU_BOOT|RENDER_PROBE|MENU background" "$ALIVE_DATA/stdout.log" | head -3
echo "build_v120: 窗口存活 PASS（rc=124=活到被杀）"
rm -rf "$ALIVE_DATA"

echo "==> [5/7] 发版门禁 B：玩家视角截图三段（菜单/选关/战斗）"
mkdir -p "$SHOT_DIR/win-menu" "$SHOT_DIR/win-levels" "$SHOT_DIR/win-battle"
set +e
( cd "$WIN_STAGE" && ERA_DATA_ROOT="$WIN_STAGE/_v120_shotdata" ./EraTD.exe \
    -- --shot="$(cygpath -m "$SHOT_DIR/win-menu" 2>/dev/null || echo "$SHOT_DIR/win-menu")" ) \
    > "$SHOT_DIR/win-menu.log" 2>&1
MENU_RC=$?
( cd "$WIN_STAGE" && ERA_DATA_ROOT="$WIN_STAGE/_v120_shotdata" ./EraTD.exe \
    -- --shot="$(cygpath -m "$SHOT_DIR/win-levels" 2>/dev/null || echo "$SHOT_DIR/win-levels")" --shot-plan=level-cards ) \
    > "$SHOT_DIR/win-levels.log" 2>&1
LEVELS_RC=$?
( cd "$WIN_STAGE" && ERA_DATA_ROOT="$WIN_STAGE/_v120_shotdata" ./EraTD.exe \
    -- --shot="$(cygpath -m "$SHOT_DIR/win-battle" 2>/dev/null || echo "$SHOT_DIR/win-battle")" --shot-plan=battle-hud ) \
    > "$SHOT_DIR/win-battle.log" 2>&1
BATTLE_RC=$?
set -e
for lane in menu levels battle; do
  LOG="$SHOT_DIR/win-$lane.log"
  if ! grep -q "SHOT_OK" "$LOG"; then
    echo "build_v120: 截图门禁红（win-$lane 无 SHOT_OK；rc 见上，日志 $LOG）" >&2
    tail -n 15 "$LOG" 2>/dev/null || true
    exit 1
  fi
  grep -E "SHOT_OK|CAMPAIGN_CARDS|BATTLE_HUD" "$LOG" | head -3
done
rm -rf "$WIN_STAGE/_v120_shotdata"
echo "build_v120: 玩家视角截图三段 PASS"

echo "==> [6/7] 发版门禁 C：出厂裸包三证（仓库树外复制再截——防美术降级回归）"
BARE_ROOT="$(mktemp -d -t era_v120_bare_XXXXXX)"
cp -r "$WIN_STAGE" "$BARE_ROOT/pkg"
mkdir -p "$SHOT_DIR/bare-menu" "$SHOT_DIR/bare-levels"
set +e
( cd "$BARE_ROOT/pkg" && ./EraTD.exe \
    -- --shot="$(cygpath -m "$SHOT_DIR/bare-menu" 2>/dev/null || echo "$SHOT_DIR/bare-menu")" ) \
    > "$SHOT_DIR/bare-menu.log" 2>&1
BARE_MENU_RC=$?
( cd "$BARE_ROOT/pkg" && ./EraTD.exe \
    -- --shot="$(cygpath -m "$SHOT_DIR/bare-levels" 2>/dev/null || echo "$SHOT_DIR/bare-levels")" --shot-plan=level-cards ) \
    > "$SHOT_DIR/bare-levels.log" 2>&1
BARE_LEVELS_RC=$?
set -e
for lane in menu levels; do
  LOG="$SHOT_DIR/bare-$lane.log"
  if ! grep -q "SHOT_OK" "$LOG"; then
    echo "build_v120: 裸包门禁红（bare-$lane 无 SHOT_OK；日志 $LOG）" >&2
    tail -n 15 "$LOG" 2>/dev/null || true
    exit 1
  fi
  # 三证机械面：菜单行须为 MenuLoopVideo（视频活）；选关行须 29/29（banner 全达）
  if [ "$lane" = "menu" ] && ! grep -q "background=MenuLoopVideo" "$LOG"; then
    echo "build_v120: 裸包菜单背景非 MenuLoopVideo——美术降级回归嫌疑" >&2
    exit 1
  fi
  if [ "$lane" = "levels" ] && ! grep -q "banners=29/29" "$LOG"; then
    echo "build_v120: 裸包选关 banner 非 29/29——美术降级回归嫌疑" >&2
    exit 1
  fi
  grep -E "SHOT_OK|MENU background|CAMPAIGN_CARDS" "$LOG" | head -3
done
rm -rf "$BARE_ROOT"
echo "build_v120: 裸包三证机械面 PASS（六卡色差目检=主管验收承载）"

echo "==> [7/7] Linux 导出+装配+结构校验+zip（带执行位）+U 盘同步"
rm -rf "$LINUX_STAGE"
mkdir -p "$LINUX_STAGE"
godot_headless "$RUN_TIMEOUT_SECS" --export-release "Linux" "$LINUX_STAGE/EraTD.x86_64"
if [ ! -f "$LINUX_STAGE/EraTD.x86_64" ]; then
  echo "build_v120: Linux 导出失败——未产出 EraTD.x86_64" >&2
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

if [ -d /e/game-videos ]; then
  mkdir -p "$USB_DIR"
  cp "$WIN_ZIP" "$WIN_ZIP.sha256" "$LINUX_ZIP" "$LINUX_ZIP.sha256" "$USB_DIR/"
  echo "build_v120: 已同步到 U 盘 $USB_DIR"
else
  echo "build_v120: U 盘 E: 不在位——跳过同步（仅本地 $OUT_DIR）"
fi

echo "build_v120: 完成——$WIN_ZIP + $LINUX_ZIP（三道新门禁全过）"
exit 0
