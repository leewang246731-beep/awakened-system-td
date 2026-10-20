#!/usr/bin/env python3
"""gen_advisor_portraits.py — W10/T-163 前置：军师面板六时代立绘（vibeapi/gpt-image-2）。

军师=「觉醒系统」拟人人格，随六时代进化同一角色的六种形态（叙事一致性）。
产物：1024x1024 母带 → docs/screenshots/w10-icons-raw/（存档，gitignored）
      256x256 BOX 4x 整数降采样 → game/presentation/EraTD/textures/ui/portraits/era{N}-advisor.png
图内一律 NO TEXT。断点续跑：目标位已有即跳过。
用法：python tools/art/gen_advisor_portraits.py
密钥：config/secrets.local.json → vibeapi_image（gitignored，R5）
"""
import base64
import json
import os
import urllib.request

from PIL import Image

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
OUT_RAW = os.path.join(REPO, "docs", "screenshots", "w10-icons-raw")
OUT_DST = os.path.join(REPO, "game", "presentation", "EraTD", "textures", "ui", "portraits")
CFG = json.load(open(os.path.join(REPO, "config", "secrets.local.json"), encoding="utf-8"))["vibeapi_image"]

# 同一角色（觉醒系统·军师）六时代形态——服装/载体/光环随时代演进，面部特征保持一致
PERSONAS = [
    (1, "stone-age shaman form: fur-and-bone attire, ochre face paint, a glowing amber eye marking, "
        "moss green and bone white palette"),
    (2, "classical philosopher form: white marble-toned robes with gold trim, laurel circlet, "
        "a faint golden glyph on the brow, marble white and antique gold palette"),
    (3, "steampunk engineer form: brass goggles on forehead, soot-stained coat, copper gauntlet, "
        "one eye glows amber, brass and soot palette"),
    (4, "modern operator form: glass-blue holo-visor, dark tactical jacket with circuit lines, "
        "earpiece, steel grey and glass blue palette"),
    (5, "near-future hologram form: translucent neon-cyan projection with violet edge glow, "
        "floating interface shards, cyan and violet palette"),
    (6, "cosmic warden form: starlight-violet crystalline figure, deep-space void backdrop, "
        "constellation halo, violet and crystal teal palette"),
]

STYLE = ("Bust portrait for a game strategy advisor panel, high-detail 16-bit pixel art, "
         "front-facing, calm confident expression, dark simple background with subtle era-colored "
         "rim light, square composition, head and shoulders occupy the frame, chunky visible pixels. "
         "NO TEXT, no letters, no numbers, no watermark. The character is an awakened game-system "
         "spirit advisor, same person across all eras — consistent youthful androgynous face with "
         "short dark hair and luminous eyes; this era's form: {persona}.")


def fetch_one(prompt: str) -> bytes:
    body = json.dumps({"model": "gpt-image-2", "prompt": prompt, "size": "1024x1024", "n": 1}).encode()
    req = urllib.request.Request(
        CFG["url"].rstrip("/") + "/v1/images/generations",
        data=body,
        headers={"Authorization": f"Bearer {CFG['key']}", "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=300) as resp:
        data = json.loads(resp.read())
    item = data["data"][0]
    if item.get("b64_json"):
        return base64.b64decode(item["b64_json"])
    if item.get("url"):
        with urllib.request.urlopen(item["url"], timeout=300) as r:
            return r.read()
    raise RuntimeError(f"unexpected response keys={list(item.keys())}")


def main():
    os.makedirs(OUT_RAW, exist_ok=True)
    os.makedirs(OUT_DST, exist_ok=True)
    ok = fail = skip = 0
    for era, persona in PERSONAS:
        dst = os.path.join(OUT_DST, f"era{era}-advisor.png")
        if os.path.exists(dst):
            print(f"[skip] era{era}")
            skip += 1
            continue
        try:
            png = fetch_one(STYLE.format(persona=persona))
            raw_path = os.path.join(OUT_RAW, f"era{era}-advisor.png")
            open(raw_path, "wb").write(png)
            im = Image.open(raw_path).convert("RGB")
            im.resize((im.width // 4, im.height // 4), Image.Resampling.BOX).save(dst, optimize=True)
            print(f"[ok]   era{era}-advisor.png {len(png)}B", flush=True)
            ok += 1
        except Exception as e:
            print(f"[FAIL] era{era}: {e}", flush=True)
            fail += 1
    print(f"DONE ok={ok} fail={fail} skip={skip}")
    raise SystemExit(1 if fail else 0)


if __name__ == "__main__":
    main()
