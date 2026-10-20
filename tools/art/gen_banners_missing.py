#!/usr/bin/env python3
"""gen_banners_missing.py — W10/T-162 前置：补产缺失的关卡题图 banner（vibeapi/gpt-image-2）。

背景：expand-v1/P1/banners/ 现仅 7 张（e1-l1..l4 + e2-l1..l3），战役共 29 关
（content/levels/e1~e6.json），缺 22 张；另加无尽/程序关 2 张模式卡 → 共 24 张。
风格基准=banner-e1-l1（高精度 16-bit 像素画、3:2 横幅、谷地走廊构图、视线
引导线、大气层次）。图内一律 NO TEXT（关卡名由 UI 字体叠加）。

产物：1536x1024 母带 → docs/screenshots/w10-banners-raw/（存档）
      768x512 BOX 整数降采样 → game/assets/expand-v1/P1/banners/（入库位）
断点续跑：目标位已有文件即跳过。
用法：  python tools/art/gen_banners_missing.py
密钥：  config/secrets.local.json → vibeapi_image（gitignored，R5 纪律）
"""
import base64
import json
import os
import urllib.request

from PIL import Image

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
OUT_RAW = os.path.join(REPO, "docs", "screenshots", "w10-banners-raw")
OUT_DST = os.path.join(REPO, "game", "assets", "expand-v1", "P1", "banners")
CFG = json.load(open(os.path.join(REPO, "config", "secrets.local.json"), encoding="utf-8"))["vibeapi_image"]

# 时代色板块（与 EraCore 六时代叙事一致：e1 蛮荒赭绿 / e2 古典大理石 /
# e3 蒸汽黄铜 / e4 电气玻璃蓝 / e5 聚变霓虹 / e6 晶体深空）
ERA = {
    "e1": "primal stone-age era palette: ochre earth, wild moss green, bone white, dawn haze",
    "e2": "classical antiquity era palette: marble white, aqueduct stone, dusty gold, cypress green",
    "e3": "steampunk industrial era palette: brass, copper pipes, soot-stained brick, warm amber lamp glow",
    "e4": "modern electric era palette: glass and steel, cool glass-blue, searchlight beams, warm window lights",
    "e5": "near-future fusion era palette: neon cyan and violet, holographic glow, warm fusion-core light",
    "e6": "cosmic crystal era palette: deep-space violet, crystal teal, starlight, ethereal glow",
    "mix": "all six era palettes blended across the scene (stone age to cosmic)",
}

# (目标文件名, 时代键, 场景主题)——关卡名见 content/levels/e{era}.json
JOBS = [
    ("banner-e1-final-treader-court", "e1",
     "a primal warlord's throne court of standing stones and bone banners atop a trampled plain, "
     "stampeding beast herds raising dust on the horizon"),
    ("banner-e2-final-iron-hoof", "e2",
     "a classical marble city gate under siege, armored cavalry charging down a stone causeway, "
     "broken aqueduct in the background"),
    ("banner-e3-l1-first-steam-whistle", "e3",
     "a brass steam locomotive letting out its first whistle at a wooden platform station, "
     "crowd of top-hat onlookers, gear-crane in background. STRICT pixel art only: chunky "
     "visible pixels, hard pixel edges, limited retro palette, absolutely NO smooth "
     "painterly rendering, NO digital painting look"),
    ("banner-e3-l2-ore-rails", "e3",
     "ore mine with iron rail tracks spiraling into a mountainside, loaded minecarts, "
     "lantern-lit tunnels, pithead wheels"),
    ("banner-e3-l3-dig-and-decree", "e3",
     "an archaeological dig inside a steam-era city: half-buried ancient relic under timber scaffolding, "
     "brass survey instruments, posted decree boards"),
    ("banner-e3-l4-armor-and-ammo", "e3",
     "a fortified steam cannon emplacement with riveted iron armor plates, ammunition belts and "
     "sandbag redoubt, valve towers venting steam"),
    ("banner-e3-final-fortress-throne", "e3",
     "a colossal rolling steam fortress like a land battleship, throne mounted on its deck, "
     "smokestacks and pistons, ground trembling debris"),
    ("banner-e4-l1-dawn-grid", "e4",
     "dawn over an electric power grid, transmission pylons marching to the horizon, "
     "first light glinting on sagging wires, glass city waking"),
    ("banner-e4-l2-steel-conveyor", "e4",
     "steel conveyor belt factory lines carrying parts across a gorge, robotic arms, "
     "cargo gantries, cool industrial lighting"),
    ("banner-e4-l3-cores-unearthed", "e4",
     "modern excavation of ruins around a glowing multi-core processor monolith, "
     "server racks and sensor tripods, caution tape"),
    ("banner-e4-l4-minefield-night", "e4",
     "a minefield at night: warning lamps, barbed wire entanglements, sweeping searchlight "
     "beams, distant blacked-out skyline"),
    ("banner-e4-final-night-owl", "e4",
     "a night-owl-shaped stealth bomber descending over a darkened city, faint radar rings, "
     "wing lights cutting through clouds"),
    ("banner-e5-l1-fusion-dawn", "e5",
     "a fusion reactor dome igniting at dawn, torus core glowing through viewing glass, "
     "engineers on a lit catwalk"),
    ("banner-e5-l2-drone-corridors", "e5",
     "air corridors of drone swarms flying light-lanes between neon towers, "
     "holographic route markers, rooftop launch pads"),
    ("banner-e5-l3-delegated-digs", "e5",
     "an AI-managed excavation site: robotic arms carefully brushing a floating monolith, "
     "holographic interface panels, tethered survey drones"),
    ("banner-e5-l4-field-and-void", "e5",
     "a shimmering force-field dome over a fortress while an annihilation beam scorches a "
     "void sphere into the ground before it"),
    ("banner-e5-final-the-dominator", "e5",
     "the Dominator descends: a giant mecha mothership lowering over a neon city, "
     "god-rays through its thruster wash, tiny defense line below"),
    ("banner-e6-l1-zero-point-dawn", "e6",
     "zero-point dawn: a crystalline spire lighting a dark plain, rings of energy rippling "
     "outward, stars visible in a violet sky"),
    ("banner-e6-l2-portal-lattice", "e6",
     "a network of glowing portal gates linked by threads of light, floating over ruins, "
     "travelers silhouetted mid-transit"),
    ("banner-e6-l3-echo-excavation", "e6",
     "echo archaeology: spectral holographic replays of an ancient battle hovering over "
     "excavation pits, crystal probes taking readings"),
    ("banner-e6-l4-colossus-line", "e6",
     "a colossus defense line: giant crystal mechs standing shoulder to shoulder as a wall "
     "against a tide of void creatures"),
    ("banner-e6-final-the-watchkeeper", "e6",
     "the Watchkeeper descends: a colossal cosmic entity of crystal and starlight forming "
     "above the world, deep-space sky, awestruck defenders below"),
    ("banner-mode-endless", "mix",
     "an endless mode keyart: an infinite battlefield wall stretching beyond the horizon, "
     "waves of enemies marching in endless procession, towers of every era standing together"),
    ("banner-mode-procedural", "mix",
     "a procedurally generated world keyart: a kaleidoscopic archipelago of terrain chunks "
     "rearranging themselves in mid-air, glowing grid seams, dice-like generation seeds"),
]

# 风格模板（对齐既有 banner-e1-l1 的目检结论：高精度 16-bit 像素画/3:2 横幅/
# 谷地走廊构图/视线引导线/大气层次/无文字）
STYLE = ("High-detail 16-bit pixel art game level banner, wide horizontal landscape. "
         "NO TEXT, no letters, no numbers, no watermark, no UI. Composition: a broad "
         "valley corridor viewed from a slight high angle, a road or waterway leading the "
         "eye from lower-left foreground toward the horizon, clusters of defense towers "
         "on elevated ground, layered atmospheric depth with soft haze, dramatic but "
         "clean lighting, crisp pixels, harmonious palette. {era}. Scene: {theme}.")


def fetch_one(prompt: str) -> bytes:
    body = json.dumps({"model": "gpt-image-2", "prompt": prompt, "size": "1536x1024", "n": 1}).encode()
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
    if item.get("url"):  # url 模式兜底（gen_logo_candidates 的已知缺陷在此补上）
        with urllib.request.urlopen(item["url"], timeout=300) as r:
            return r.read()
    raise RuntimeError(f"unexpected response keys={list(item.keys())}")


def main():
    os.makedirs(OUT_RAW, exist_ok=True)
    os.makedirs(OUT_DST, exist_ok=True)
    ok = fail = skip = 0
    for name, era_key, theme in JOBS:
        dst = os.path.join(OUT_DST, f"{name}.png")
        if os.path.exists(dst):
            print(f"[skip] {name}")
            skip += 1
            continue
        prompt = STYLE.format(era=ERA[era_key], theme=theme)
        try:
            png = fetch_one(prompt)
            raw_path = os.path.join(OUT_RAW, f"{name}.png")
            open(raw_path, "wb").write(png)
            im = Image.open(raw_path)
            im.convert("RGB").resize((im.width // 2, im.height // 2), Image.Resampling.BOX).save(dst, optimize=True)
            print(f"[ok]   {name} {len(png)}B -> {dst}")
            ok += 1
        except Exception as e:
            print(f"[FAIL] {name}: {e}")
            fail += 1
    print(f"DONE ok={ok} fail={fail} skip={skip} total={len(JOBS)}")
    raise SystemExit(1 if fail else 0)


if __name__ == "__main__":
    main()
