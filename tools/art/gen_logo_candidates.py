#!/usr/bin/env python3
"""gen_logo_candidates.py — W10 界面大修：主菜单 logo 牌匾背景候选生成（vibeapi/gpt-image-2）。

业主 2026-10-19 授权（生图途径+AI 自选候选）。生成策略：图内不带文字
（AI 中文字形不可靠）——只生成"牌匾/横幅构图"美术，中文标题由游戏内
字体+描边排版叠加。4 候选 → docs/screenshots/w10-logo/ 落盘 → 主管目选。

用法： python tools/art/gen_logo_candidates.py
"""
import base64
import json
import os
import sys
import urllib.request

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
OUT = os.path.join(REPO, "docs", "screenshots", "w10-logo")
CFG = json.load(open(os.path.join(REPO, "config", "secrets.local.json"), encoding="utf-8"))["vibeapi_image"]

PROMPTS = [
    # 1 六时代色带渐变 + 城墙剪影
    ("c1", "Wide horizontal game title banner artwork, pixel-art style, NO TEXT no letters no words. "
           "A majestic ancient stone wall with watchtower silhouette at center, behind it a sweeping gradient sky "
           "transitioning through six color bands bottom-to-top: stone age ochre, classical marble white, industrial "
           "soot orange, modern glass blue, futuristic neon cyan, deep space violet with stars. Empty central area "
           "reserved for title text. Cinematic, high contrast, clean edges, 16-bit pixel art aesthetic."),
    # 2 塔防炮塔群像 + 母题形变
    ("c2", "Wide horizontal game logo banner, pixel-art style, NO TEXT. A row of six stylized defense towers "
           "standing together like a team lineup, each in a different era theme: wooden watchtower, stone citadel, "
           "steam-punk cannon turret, modern missile launcher, sci-fi railgun, cosmic monolith. Dawn sky with warm "
           "rim light, empty sky space in upper half for title. Crisp 16-bit pixel art, vibrant but harmonious."),
    # 3 守望之眼 + 徽章风
    ("c3", "Wide horizontal emblem-style game banner, pixel-art, NO TEXT. Central glowing eye-shaped artifact "
           "hovering above a small fortress, radiating six-colored light beams (ochre, white, orange, blue, cyan, "
           "violet), dark atmospheric background with drifting memory shards, mystical sci-fi fusion. Center-left "
           "kept dark and clean for title overlay. Detailed 16-bit pixel art."),
    # 4 极简牌匾
    ("c4", "Wide horizontal minimalist title plate artwork, pixel-art, NO TEXT. An ornate carved stone plaque "
           "frame with subtle circuit-line engravings glowing softly cyan at the edges, weathered ancient texture "
           "meeting faint futuristic glow, completely empty flat center for title text, dark vignette. "
           "Elegant, high contrast, 16-bit pixel art."),
]

def main():
    os.makedirs(OUT, exist_ok=True)
    for tag, prompt in PROMPTS:
        body = json.dumps({
            "model": "gpt-image-2",
            "prompt": prompt,
            "size": "1536x1024",
            "n": 1,
        }).encode()
        req = urllib.request.Request(
            CFG["url"].rstrip("/") + "/v1/images/generations",
            data=body,
            headers={"Authorization": f"Bearer {CFG['key']}", "Content-Type": "application/json"},
        )
        try:
            with urllib.request.urlopen(req, timeout=300) as resp:
                data = json.loads(resp.read())
            item = data["data"][0]
            if item.get("b64_json"):
                png = base64.b64decode(item["b64_json"])
                path = os.path.join(OUT, f"logo-{tag}.png")
                open(path, "wb").write(png)
                print(f"{tag}: {len(png)} bytes -> {path}")
            else:
                print(f"{tag}: NO b64 (url mode?) keys={list(item.keys())}")
        except Exception as e:
            print(f"{tag}: FAIL {e}")

if __name__ == "__main__":
    main()
