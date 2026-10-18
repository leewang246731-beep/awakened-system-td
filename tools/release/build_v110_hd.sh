#!/usr/bin/env bash
# build_v110_hd.sh — T-133 v1.1.0 可选资产：480p 视频母片合集装配。
#
# build_v020.sh §6 高清包步骤的同链复用（正片 build_v100.sh 不含此步——
# bash 边跑边改脚本有执行损坏风险，故独立成卡）。母片源=U 盘
# E:/game-videos/expand-v1，A~E 五层 manifest 驱动，51 条硬判据。
# 视频内容自 v0.2 起未变（52 ogv 同数），本包与 v0.2 母片包内容一致，
# 仅版本面/README 升 v1.1.0。
#
# 用法： bash tools/release/build_v110_hd.sh

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TAG="v1.1.0"
OUT_DIR="$REPO_ROOT/release/$TAG"
HD_NAME="EraTD-v1.1.0-video-masters-480p"
HD_STAGE="$OUT_DIR/$HD_NAME"
HD_ZIP_PATH="$OUT_DIR/$HD_NAME.zip"
VIDEO_SRC="/e/game-videos/expand-v1"
USB_DIR="/e/game-videos/releases/$TAG"

PY_BIN=""
for cand in python python3; do
  if command -v "$cand" >/dev/null 2>&1; then PY_BIN="$cand"; break; fi
done
if [ -z "$PY_BIN" ]; then
  echo "build_v110_hd: python 不可用" >&2
  exit 2
fi
if [ ! -d "$VIDEO_SRC" ]; then
  echo "build_v110_hd: 母片源 $VIDEO_SRC 不在位" >&2
  exit 2
fi

rm -rf "$HD_STAGE"
mkdir -p "$HD_STAGE/masters"

"$PY_BIN" - "$REPO_ROOT" "$VIDEO_SRC" "$HD_STAGE/masters" <<'PYEOF'
import json, os, shutil, sys
repo, src_root, out = sys.argv[1], sys.argv[2], sys.argv[3]
count = 0
for layer in ("A", "B", "C", "D", "E"):
    manifest = json.load(open(os.path.join(repo, "game", "assets", "expand-v1",
                                           f"{layer}-manifest.json"), encoding="utf-8"))
    os.makedirs(os.path.join(out, layer), exist_ok=True)
    for item in manifest["items"]:
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
《我在游戏觉醒系统》Awakened System TD v1.1.0 视频母片合集（480p）
================================================================

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
视频内容与 v1.0.0 起未变（E1~E6 素材面无新增视频）。
HDREADME

rm -f "$HD_ZIP_PATH"
( cd "$OUT_DIR" && powershell -NoProfile -Command \
    "Compress-Archive -Path '$HD_NAME' -DestinationPath '$HD_NAME.zip'" )
sha256sum "$HD_ZIP_PATH" | sed "s|$REPO_ROOT/||" > "$HD_ZIP_PATH.sha256"
cat "$HD_ZIP_PATH.sha256"
ls -la "$HD_ZIP_PATH"

if [ -d /e/game-videos ]; then
  mkdir -p "$USB_DIR"
  cp "$HD_ZIP_PATH" "$HD_ZIP_PATH.sha256" "$USB_DIR/"
  echo "build_v110_hd: 已同步到 U 盘 $USB_DIR"
fi
echo "build_v110_hd: 完成——$HD_ZIP_PATH"
