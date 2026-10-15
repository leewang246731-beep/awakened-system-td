#!/usr/bin/env bash
# build_v020.sh — T-096-v02 v0.2.0-alpha（视觉扩充版）Windows 发包流水线。
#
# build_v010.sh（T-096 v0.1）的升级复用：同环境纪律/同导出链/同冒烟判据，
# 差量=视觉扩充素材入包 + 高清包（480p mp4 母片合集）第二资产。
#
# 产物（本地 release/（gitignored）+ U 盘 E:\game-videos\releases\v0.2.0-alpha\）：
#   ① EraTD-v0.2.0-alpha-win64.zip（正片：v0.1 全部内容 + cinematics 52 ogv
#      [51 条+《零的真相》] + 8 Boss 序列帧 + expand-v1 插画聚合件 49 图
#      [2×BOX 降采样，tools/art/expand_aggregate_images.py] + 成就图标 24 +
#      素材 manifest 台账——31 号 §五.5"ogv 480p 全量内置正片"批复落地）
#   ② EraTD-v0.2.0-alpha-video-masters-480p.zip（高清包=可选资产：A~E 五层
#      51 条 480p mp4 母片合集，供爱好者/剪辑二开；E 盘不在位时警告跳过）
#
# 步骤：
#   1) dotnet build 全解决方案 Release（零警告门禁同 G3）
#   2) Godot headless --import
#   3) Godot headless --export-release "Windows Desktop"（模板 junction 先例
#      同 v0.1：见 REV-20261015-T096 §2——模板目录名必须带 .mono 后缀）
#   4) 装配发布树：exe + data_EraTD_windows_x86_64 + game/content（res:// 外
#      内容，SimHost 向上探测）+ game/assets/cinematics（ogv/序列帧/manifest
#      ——ExpandAssetPaths.RepoRoot walk-up 命中）+ game/assets/expand-v1
#      （P1/P3/P4 聚合插画+图标+manifest 台账——ExternalImagePath 发布轨回退
#      命中）+ start-game.bat + 快速开始.txt + LICENSE + ASSETS-LICENSE
#   5) 导出件冒烟（SMOKE_OK；EXPAND_OK 段新增 images_resolved=49+皮肤九宫
#      硬判据——T-096-v02）
#   6) 高清包装配（manifest 驱动 51 mp4；E:/game-videos/expand-v1）
#   7) 打 zip + sha256 清单 + U 盘同步
#
# 用法： bash tools/release/build_v020.sh
# 前置： Godot 4.7.2 mono + 导出模板就位；图片聚合已入库
#       （python tools/art/expand_aggregate_images.py --verify 全过）。

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SLN="$REPO_ROOT/game/EraTD.sln"
PROJECT_DIR="$REPO_ROOT/game/presentation/EraTD"
GODOT_CONSOLE="$REPO_ROOT/tools/godot_extract/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"

TAG="v0.2.0-alpha"
PKG_NAME="EraTD-v0.2.0-alpha-win64"
HD_NAME="EraTD-v0.2.0-alpha-video-masters-480p"
OUT_DIR="$REPO_ROOT/release/$TAG"
STAGE="$OUT_DIR/$PKG_NAME"
ZIP_PATH="$OUT_DIR/$PKG_NAME.zip"
HD_ZIP_PATH="$OUT_DIR/$HD_NAME.zip"
USB_DIR="/e/game-videos/releases/$TAG"
VIDEO_SRC="/e/game-videos/expand-v1"

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
  echo "build_v020: dotnet 不可用（PATH 无 dotnet 且未找到 D 盘 SDK）" >&2
  exit 2
fi
# GodotTools 的 dotnet publish 步骤按 PATH 搜 dotnet 可执行文件（仅
# DOTNET_ROOT 不够——REV-20261015-T096 §2 实测），故 PATH 必须含 D 盘 SDK。
if [ ! -f "$GODOT_CONSOLE" ]; then
  echo "build_v020: 找不到 Godot console exe：$GODOT_CONSOLE（07 §三（附））" >&2
  exit 2
fi
# 导出模板就位检查（junction 已把默认查找路径指到 D 盘登记位置；目录名带 .mono）
TPL_DIR="$APPDATA/Godot/export_templates/4.7.2.stable.mono"
if [ ! -f "$TPL_DIR/windows_release_x86_64.exe" ]; then
  echo "build_v020: 导出模板缺失：$TPL_DIR/windows_release_x86_64.exe" >&2
  exit 2
fi

# ---- 发布素材就位预检（聚合轨硬前置；缺=提示先跑聚合脚本）---------------------
AGG_PNG_COUNT=$(find "$REPO_ROOT/game/assets/expand-v1/P1" \
    "$REPO_ROOT/game/assets/expand-v1/P3" \
    "$REPO_ROOT/game/assets/expand-v1/P4" -name "*.png" 2>/dev/null | wc -l)
if [ "$AGG_PNG_COUNT" -ne 73 ]; then
  echo "build_v020: expand-v1 聚合图应为 73（49 插画+24 图标），实得 $AGG_PNG_COUNT" >&2
  echo "  先执行：python tools/art/expand_aggregate_images.py（U 盘在位）" >&2
  exit 2
fi
OGV_COUNT=$(find "$REPO_ROOT/game/assets/cinematics" -name "*.ogv" | wc -l)
if [ "$OGV_COUNT" -ne 52 ]; then
  echo "build_v020: cinematics ogv 应为 52（51 条+《零的真相》），实得 $OGV_COUNT" >&2
  exit 2
fi

echo "==> [1/7] dotnet build Release（零警告）"
"$DOTNET_CLI" build "$SLN" -c Release --nologo -p:TreatWarningsAsErrors=true

echo "==> [2/7] Godot headless --import"
if command -v timeout >/dev/null 2>&1; then
  timeout "$RUN_TIMEOUT_SECS" "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" --import || \
    echo "build_v020: import 退出码非 0（非致命告警常见，继续）"
else
  "$GODOT_CONSOLE" --headless --path "$PROJECT_DIR" --import || true
fi

echo "==> [3/7] Godot --export-release \"Windows Desktop\""
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
  echo "build_v020: 导出失败——未产出 EraTD.exe" >&2
  exit 1
fi

echo "==> [4/7] 装配发布树（content/cinematics/expand-v1/启动器/许可/说明）"
mkdir -p "$STAGE/game/assets"
cp -r "$REPO_ROOT/game/content" "$STAGE/game/content"
# 31 号 §五.5：ogv 480p 全量内置正片（52 ogv + 8 序列帧 + manifest 台账）
cp -r "$REPO_ROOT/game/assets/cinematics" "$STAGE/game/assets/cinematics"
# T-096-v02 图片聚合轨（49 插画 + 24 图标 + P1/P3/P4 manifest + 聚合台账
# + A~E 视频层 manifest——AI 生成内容披露的素材台账随包分发）
cp -r "$REPO_ROOT/game/assets/expand-v1" "$STAGE/game/assets/expand-v1"
cp "$REPO_ROOT/tools/release/templates/start-game.bat" "$STAGE/start-game.bat"
cp "$REPO_ROOT/tools/release/templates/快速开始.txt" "$STAGE/快速开始.txt"
cp "$REPO_ROOT/LICENSE" "$STAGE/LICENSE"
cp "$REPO_ROOT/ASSETS-LICENSE" "$STAGE/ASSETS-LICENSE"

echo "==> [5/7] 导出件冒烟（headless --quit，判据=SMOKE_OK/EXPAND_OK）"
SMOKE_DATA="$(mktemp -d -t era_v020_smoke_XXXXXX)"
SMOKE_LOG="$SMOKE_DATA/run.log"
if ( cd "$STAGE" && ERA_DATA_ROOT="$SMOKE_DATA" ./EraTD.exe --headless --quit \
       --log-file "$(cygpath -w "$SMOKE_LOG" 2>/dev/null || echo "$SMOKE_LOG")" ); then
  SMOKE_RC=0
else
  SMOKE_RC=$?
fi
if ! grep -q "SMOKE_OK" "$SMOKE_LOG" 2>/dev/null; then
  echo "build_v020: 导出件冒烟未捕获 SMOKE_OK（rc=$SMOKE_RC，日志 $SMOKE_LOG）" >&2
  tail -n 20 "$SMOKE_LOG" 2>/dev/null || true
  exit 1
fi
grep -E "SMOKE_OK|EXPAND_OK" "$SMOKE_LOG"
rm -rf "$SMOKE_DATA"

echo "==> [6/7] 高清包装配（480p mp4 母片合集，可选资产）"
HD_STAGE="$OUT_DIR/$HD_NAME"
rm -rf "$HD_STAGE"
mkdir -p "$HD_STAGE/masters"
HD_BUILT=0
PY_HD=""
for cand in python python3; do
  if command -v "$cand" >/dev/null 2>&1; then PY_HD="$cand"; break; fi
done
if [ -z "$PY_HD" ]; then
  echo "build_v020: python 不可用——高清包跳过（正片不受影响）"
elif [ ! -d "$VIDEO_SRC" ]; then
  echo "build_v020: 母片源 $VIDEO_SRC 不在位——高清包跳过（正片不受影响）"
else
  "$PY_HD" - "$REPO_ROOT" "$VIDEO_SRC" "$HD_STAGE/masters" <<'PYEOF'
import json, os, shutil, sys
repo, src_root, out = sys.argv[1], sys.argv[2], sys.argv[3]
count = 0
for layer in ("A", "B", "C", "D", "E"):
    manifest = json.load(open(os.path.join(repo, "game", "assets", "expand-v1",
                                           f"{layer}-manifest.json"), encoding="utf-8"))
    os.makedirs(os.path.join(out, layer), exist_ok=True)
    for item in manifest["items"]:
        # A 层 file=裸文件名；B~E 层 file=U 盘绝对路径（历史台账口径不一）——
        # 统一取 basename 归一，母片源=src_root/{layer}/{basename}
        name = item["file"].replace("\\", "/").rsplit("/", 1)[-1]
        src = os.path.join(src_root, layer, name)
        if not os.path.isfile(src):
            print(f"hd-pack: 母片缺失 {layer}/{name}（{src}）", file=sys.stderr)
            sys.exit(1)
        shutil.copy2(src, os.path.join(out, layer, name))
        count += 1
if count != 51:
    print(f"hd-pack: 五层母片应为 51，实得 {count}", file=sys.stderr)
    sys.exit(1)
print(f"hd-pack: 51 masters staged from {src_root}")
PYEOF
  cat > "$HD_STAGE/README.txt" <<'HDREADME'
《我在游戏觉醒系统》EraTD v0.2.0-alpha 视频母片合集（480p）
============================================================

本包为可选下载资产：五层 51 条过场视频的 480p mp4 母片（正片包内为
Theora ogv 转码件，画质口径一致；母片保留 H.264 原始编码，供爱好者
剪辑/二开使用，见 ASSETS-LICENSE 的 CC BY-NC 4.0 边界）。

- A 待机/章节 7 条（菜单循环 1+跃迁 5+终章 1[90s 尾帧链]）
- B 仪式 15 条（奇观 6+军师升级 5+人格解锁 4）
- C 战斗 17 条（Boss 入场 4+败亡 4+关卡开场 7+首通 2）
- D 记忆 8 条（考古记忆闪回）
- E 彩蛋 4 条

全部由火山方舟 Seedance 2.5 生成（AI 生成内容，逐条台账见仓库
game/assets/expand-v1/{A..E}-manifest.json）。清单/时长/成本字段口径
与正片包内 game/assets/cinematics/manifest.json 对应。
HDREADME
  rm -f "$HD_ZIP_PATH"
  ( cd "$OUT_DIR" && powershell -NoProfile -Command \
      "Compress-Archive -Path '$HD_NAME' -DestinationPath '$HD_NAME.zip'" )
  HD_SHA256_FILE="$OUT_DIR/$HD_NAME.zip.sha256"
  sha256sum "$HD_ZIP_PATH" | sed "s|$REPO_ROOT/||" > "$HD_SHA256_FILE"
  cat "$HD_SHA256_FILE"
  ls -la "$HD_ZIP_PATH"
  HD_BUILT=1
fi

echo "==> [7/7] 打 zip + sha256"
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
  if [ "$HD_BUILT" -eq 1 ]; then
    cp "$HD_ZIP_PATH" "$OUT_DIR/$HD_NAME.zip.sha256" "$USB_DIR/"
  fi
  echo "build_v020: 已同步到 U 盘 $USB_DIR"
else
  echo "build_v020: U 盘 E: 不在位——跳过同步（仅本地 $OUT_DIR）"
fi

echo "build_v020: 完成——$ZIP_PATH"
if [ "$HD_BUILT" -eq 1 ]; then
  echo "build_v020: 高清包——$HD_ZIP_PATH"
fi
exit 0
