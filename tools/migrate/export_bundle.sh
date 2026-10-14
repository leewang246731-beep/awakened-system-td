#!/bin/bash
# 旧电脑执行：打包 SSH 密钥 + 全部 skills + API 密钥到 U 盘（含敏感信息，用完删除）
set -e
OUT="/e/zcode-migration"
STAGE=$(mktemp -d)
mkdir -p "$STAGE"/{ssh,skills,config}
cp ~/.ssh/id_ed25519 ~/.ssh/id_ed25519.pub "$STAGE/ssh/" 2>/dev/null || echo "WARN: no ssh key"
cp -r ~/.zcode/skills/. "$STAGE/skills/" 2>/dev/null || echo "WARN: no skills dir"
cp /d/game/config/ark_key.txt /d/game/config/gh_token.txt /d/game/config/secrets.local.json "$STAGE/config/" 2>/dev/null || true
mkdir -p "$OUT"
ZIP="$OUT/zcode_bundle_$(date +%Y%m%d).zip"
(cd "$STAGE" && powershell.exe -NoProfile -Command "Compress-Archive -Path ssh,skills,config -DestinationPath '$(cygpath -w "$ZIP")' -Force")
rm -rf "$STAGE"
echo "BUNDLE OK: $ZIP ($(du -h "$ZIP" | cut -f1)) — 含全部密钥与 skills，妥善保管，恢复后建议删除"
