#!/usr/bin/env bash
# push_itch.sh — T-107 / F-143 / F-145：butler 一键推送 itch.io（双通道）。
#
# 通道口径（D-34 桌面分发裁定）：
#   windows → EraTD-v0.5.0-alpha-win64.zip   （Windows Desktop 导出件）
#   linux   → EraTD-v0.5.0-alpha-linux64.zip （Linux x11 导出件）
# 两通道同 --userversion=v0.5.0-alpha；itch 页面承载桌面 zip（Web Demo 为
# Q19 引擎门控跟进项，REV-20261016-T106/D-34）。
#
# 前置（缺任一=响亮失败并打印补齐指引，不伪造上线）：
#   1) butler 二进制：tools/butler/windows-amd64/butler.exe（D 盘纪律，
#      gitignored；来源=官方 itchio/butler releases v15.32.0，itch.io/butler
#      直链在本机被 429 挑战页拦截——REV-20261016-T107 §1）
#   2) API key：config/itch_key.txt（一行；或环境变量 BUTLER_API_KEY）
#      ——itch.io → 用户设置 → API keys → Generate new key
#   3) 推送目标：config/itch_target.txt（一行，格式 用户名/项目名，如
#      yourname/awakened-system-td；或环境变量 ITCH_TARGET）
#      ——目标项目不存在时 butler 会自动建为草稿页（官方 butler pushing 文档）
#
# 用法：
#   bash tools/release/push_itch.sh            # 双通道全推
#   bash tools/release/push_itch.sh windows    # 只推 windows
#   bash tools/release/push_itch.sh linux      # 只推 linux
# 产物校验：推送后打印页面 URL 与匿名 HTTP 状态（草稿页 404=预期，需业主
# 在 dashboard 装配页面文案/封面并点 Publish 后才 200）。

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BUTLER_DEFAULT="$REPO_ROOT/tools/butler/windows-amd64/butler.exe"
VERSION="${ITCH_VERSION:-v0.5.0-alpha}"
ARTIFACT_DIR="${ITCH_ARTIFACT_DIR:-$REPO_ROOT/release/$VERSION}"
WIN_ZIP="$ARTIFACT_DIR/EraTD-$VERSION-win64.zip"
LINUX_ZIP="$ARTIFACT_DIR/EraTD-$VERSION-linux64.zip"

CHANNELS="${1:-all}"

fail_hint() {  # fail_hint <缺失项说明>
  cat >&2 <<HINT
push_itch: $1
补齐三步（详见 docs/engineering/reviews/REV-20261016-T107-itch.md）：
  1) butler：确认 $BUTLER_DEFAULT 存在（安装实录见 REV §1）
  2) echo '你的APIKey' > config/itch_key.txt
     （itch.io → Settings → API keys → Generate new key）
  3) echo '用户名/项目名' > config/itch_target.txt
     （如 yourname/awakened-system-td；项目不存在时 butler 自动建草稿页）
HINT
  exit 2
}

# ---- 前置三件套 -------------------------------------------------------------
if [ -n "${BUTLER_API_KEY:-}" ]; then
  API_KEY="$BUTLER_API_KEY"
elif [ -s "$REPO_ROOT/config/itch_key.txt" ]; then
  API_KEY="$(tr -d '[:space:]' < "$REPO_ROOT/config/itch_key.txt")"
else
  API_KEY=""
fi
[ -n "$API_KEY" ] || fail_hint "API key 缺失（config/itch_key.txt 与 BUTLER_API_KEY 均为空）"

if [ -n "${ITCH_TARGET:-}" ]; then
  TARGET="$ITCH_TARGET"
elif [ -s "$REPO_ROOT/config/itch_target.txt" ]; then
  TARGET="$(tr -d '[:space:]' < "$REPO_ROOT/config/itch_target.txt")"
else
  TARGET=""
fi
case "$TARGET" in
  */*) : ;;
  *) fail_hint "推送目标缺失或格式不对（应为 用户名/项目名，实得 '${TARGET:-空}'）" ;;
esac

if [ -x "$BUTLER_DEFAULT" ]; then
  BUTLER="$BUTLER_DEFAULT"
elif command -v butler >/dev/null 2>&1; then
  BUTLER="butler"
else
  BUTLER=""
fi
[ -n "$BUTLER" ] || fail_hint "butler 不可用（$BUTLER_DEFAULT 不存在且 PATH 无 butler）"

# 密钥只经环境变量注入 butler，不回显不入日志（CS-4.3/R5）。
export BUTLER_API_KEY="$API_KEY"

echo "==> butler $( "$BUTLER" -V ) → $TARGET（channels=${CHANNELS}，userversion=$VERSION）"

push_channel() {  # push_channel <channel> <zip>
  local channel="$1" zip="$2"
  if [ ! -f "$zip" ]; then
    echo "push_itch: 缺产物 $zip——先跑 bash tools/release/build_v050.sh" >&2
    exit 3
  fi
  echo "==> push $TARGET:$channel ← $zip"
  "$BUTLER" push "$zip" "$TARGET:$channel" --userversion "$VERSION"
}

case "$CHANNELS" in
  windows) push_channel windows "$WIN_ZIP" ;;
  linux)   push_channel linux "$LINUX_ZIP" ;;
  all)     push_channel windows "$WIN_ZIP"
           push_channel linux "$LINUX_ZIP" ;;
  *) echo "push_itch: 未知通道参数 '$CHANNELS'（可用：windows|linux|all）" >&2; exit 2 ;;
esac

# ---- 推送后状态提示（草稿页匿名 404=预期；Publish 后 200）--------------------
USER_NAME="${TARGET%%/*}"
PROJECT_NAME="${TARGET##*/}"
PAGE_URL="https://${USER_NAME}.itch.io/${PROJECT_NAME}"
HTTP_CODE="$(curl -s -o /dev/null -w '%{http_code}' "$PAGE_URL" || echo 000)"
echo "==> 完成。页面：$PAGE_URL（匿名 HTTP $HTTP_CODE）"
if [ "$HTTP_CODE" = "200" ]; then
  echo "    页面已公开（200）。"
else
  echo "    非 200（草稿页 404=预期）：到 itch.io dashboard → Edit page →"
  echo "    ①粘贴 game/assets/store/page.md 文案 ②封面=capsule-512 PNG"
  echo "    ③截图/GIF 按 page.md 挂接清单 ④Kind=Downloadable、\$0 或捐赠"
  echo "    ⑤保存后点 Publish 再跑本脚本复核 200。"
fi
exit 0
