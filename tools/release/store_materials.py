#!/usr/bin/env python3
"""
store_materials.py — T-108 商店物料后期流水线（E 泳道）。

输入=tools/release/record/EraRecord/out/master_final.avi（Godot --write-movie
真实录屏母片，60 s@60fps 含真游戏音频），输出=game/assets/store/ 商店物料：

  1) hero-dual-v020-alpha.gif     双拼主 GIF（断链段+军令段顺序拼接，14 s）
  2) demo-breakchain-v020-alpha.gif 断链减速单段（补线前：断链塔哑火漏敌）
  3) demo-order-build-v020-alpha.gif 军令代建单段（军令→方案卡→塔列队）
  4) demo-60s-v020-alpha.mp4      60 s 完整演示（题图+演出+玩法循环+真音频）
  5) capsule-512-v020-alpha.png   512×512 胶囊图（E1 石斧/E6 星门分屏合成）
  6) shot-01..05-*.png            5 张截图（全部从母片抽帧，非旧图复用）

版本角标：ffmpeg drawtext 右下角 v0.2.0-alpha（等宽字体小号+半透明底），
全部动图/视频/截图统一盖章。像素风协调说明见 REV-20261016-T108-store.md
（结论：矢量小字号等宽角标在 640×360 缩放后与位图 UI 混排差异不可辨，
且版本号更新无需重录——优于引擎内烧录位图角标）。

纪律：仓库相对路径（RL9）；中间产物只落 out/（不入库）；ffmpeg 缺席或
母片缺失即退出非 0（禁假成功）。用法：
  python tools/release/store_materials.py [--keep-intermediate]
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys

REPO_ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
MASTER_AVI = os.path.join(REPO_ROOT, "tools", "release", "record", "EraRecord", "out", "master_final.avi")
OUT_DIR = os.path.join(REPO_ROOT, "tools", "release", "record", "EraRecord", "out")
STORE_DIR = os.path.join(REPO_ROOT, "game", "assets", "store")

VERSION = "v0.2.0-alpha"
FPS_MASTER = 60

# 文件名口径：纯 kebab（asset_validator FILENAME-KEBAB 面，版本号带点会破
# 规则——版本已由角标烧进画面，文件名不重复携带；升版只改 VERSION 重跑）。
FILE_VERSION = "v020-alpha"

# 母片节拍（master_final.log 实证帧号；改剧本须同步）：
#   1881 波2开始；2031 军令起（面板展开）；2093 提交（卡出）；2189 确认（塔落）
#   2339 面板收起；2429 补线；3029 回拉塔线。
SEG_BREAKCHAIN = (1420, 1870)   # 波1尾段：敌到塔线，有弹塔击杀/断链塔哑火
SEG_ORDER = (2010, 2360)        # 军令全链：展开→逐字→卡→确认→塔就位→收面板
SEG_HERO_A = (1420, 1840)       # 双拼前段（7 s）
SEG_HERO_B = (2010, 2450)       # 双拼后段（7.3 s）
TITLE_CARD_SECONDS = 3.0

FONT_MONO = "C:/Windows/Fonts/consolab.ttf"   # 版本角标（等宽，系统自带）
FONT_CJK = "C:/Windows/Fonts/msyh.ttc"        # 中文字幕/题图标题


def fpath(path: str) -> str:
    """filtergraph 内的 Windows 路径：正斜杠+冒号转义+单引号包裹（'C\\:/…'，
    实测 ffmpeg 9.0 gyan build 接受的组合形式）。"""
    escaped = path.replace("\\", "/").replace(":", r"\:")
    return f"'{escaped}'"

SHOTS = [
    ("shot-01-combat", 1740, "波次接敌：有弹塔齐射"),
    ("shot-02-order-card", 2135, "军令方案卡（审批前）"),
    ("shot-03-built-report", 2260, "塔就位+战报气泡"),
    ("shot-04-menu-loop", 80, "主菜单循环（游戏内实拍）"),
    ("shot-05-south-gate", 2700, "南门新防线"),
]


def run(cmd: list[str], *, what: str) -> None:
    proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if proc.returncode != 0:
        sys.stderr.write(f"FAIL [{what}]\n  cmd: {' '.join(cmd)}\n  stderr: {proc.stderr[-1600:]}\n")
        raise SystemExit(1)


def probe(path: str) -> dict[str, str]:
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=width,height,avg_frame_rate,nb_frames",
         "-show_entries", "format=duration,size",
         "-of", "default=noprint_wrappers=1", path],
        capture_output=True, text=True)
    if out.returncode != 0:
        raise SystemExit(f"ffprobe failed on {path}: {out.stderr}")
    info: dict[str, str] = {}
    for line in out.stdout.splitlines():
        if "=" in line:
            k, v = line.split("=", 1)
            info[k.strip()] = v.strip()
    return info


def badge(y_expr: str = "h-th-6", size: int = 16) -> str:
    return (
        f"drawtext=fontfile={fpath(FONT_MONO)}:text='{VERSION}':"
        f"fontsize={size}:fontcolor=white@0.85:box=1:boxcolor=black@0.45:boxborderw=4:"
        f"x=w-tw-8:y={y_expr}"
    )


def cut(src: str, lo: int, hi: int, dst: str, extra_vf: str = "", fps_out: int | None = None) -> None:
    """母片抽段（帧号区间，闭开）→ 中间 mp4（无损参数层，供 GIF/MP4 复用）。"""
    vf = f"select='between(n,{lo},{hi})'"
    if extra_vf:
        vf += f",{extra_vf}"
    if fps_out:
        vf += f",fps={fps_out}"
    run(["ffmpeg", "-y", "-v", "error", "-i", src, "-vf", vf,
         "-fps_mode", "passthrough", "-c:v", "libx264", "-preset", "medium", "-crf", "18",
         "-pix_fmt", "yuv420p", "-an", dst], what=f"cut {dst}")


def make_gif(src_segment: str, dst: str, speed: float, caption: str | None, capfile: str | None = None) -> None:
    """段 mp4 → 版本角标(+字幕) → palettegen/paletteuse 像素风 GIF。"""
    filters = [badge(size=15)]
    if caption:
        filters.append(
            f"drawtext=fontfile={fpath(FONT_CJK)}:textfile={fpath(capfile)}"
            if capfile else
            f"drawtext=fontfile={fpath(FONT_CJK)}:text='{caption}'")
        # 字幕条置顶左
        filters[-1] += ":fontsize=15:fontcolor=0xE8F6FF@0.95:box=1:boxcolor=0x06121F@0.62:boxborderw=5:x=10:y=10"
    chained = ",".join(filters)
    tmp_palette = os.path.join(OUT_DIR, "palette.png")
    run(["ffmpeg", "-y", "-v", "error", "-i", src_segment,
         "-vf", f"{chained},setpts={1 / speed:.5f}*PTS,fps=20,split[s0][s1];[s0]palettegen=max_colors=192:stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle",
         "-loop", "0", dst], what=f"gif {dst}")


def make_mp4() -> str:
    """题图 3 s（E 盘 era1 题图横裁 640×360）+ 母片全段 + 角标 → 60 s MP4（真音频）。"""
    banner = "E:/game-assets/era-banners/era1-banner-dawn.png"
    if not os.path.exists(banner):
        raise SystemExit(f"题图缺失：{banner}（E 盘素材库）")
    title_txt = os.path.join(OUT_DIR, "title.txt")
    with open(title_txt, "w", encoding="utf-8") as fh:
        fh.write("我在游戏觉醒系统\\Awakened System TD\\永久免费 · BYOK 可选")
    title_mp4 = os.path.join(OUT_DIR, "title.mp4")
    run(["ffmpeg", "-y", "-v", "error",
         "-loop", "1", "-t", f"{TITLE_CARD_SECONDS}", "-i", banner,
         "-f", "lavfi", "-t", f"{TITLE_CARD_SECONDS}", "-i", "anullsrc=r=44100:cl=stereo",
         "-vf",
         f"fps=30,scale=640:360:force_original_aspect_ratio=increase,crop=640:360,"
         f"fade=t=in:st=0:d=0.6,fade=t=out:st={TITLE_CARD_SECONDS - 0.6}:d=0.6,"
         + badge(),
         "-r", "30", "-c:v", "libx264", "-preset", "medium", "-crf", "18",
         "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k",
         "-map", "0:v", "-map", "1:a", title_mp4], what="title card")

    body = os.path.join(OUT_DIR, "body.mp4")
    run(["ffmpeg", "-y", "-v", "error", "-i", MASTER_AVI,
         "-vf", badge(), "-r", "30", "-c:v", "libx264", "-preset", "slow", "-crf", "21",
         "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-ar", "44100", body], what="mp4 body")

    dst = os.path.join(STORE_DIR, f"demo-60s-{FILE_VERSION}.mp4")
    run(["ffmpeg", "-y", "-v", "error", "-i", title_mp4, "-i", body,
         "-filter_complex",
         f"[0:v][1:v]xfade=transition=fade:duration=0.5:offset={TITLE_CARD_SECONDS - 0.5}[v];"
         "[0:a][1:a]acrossfade=d=0.5[a]",
         "-map", "[v]", "-map", "[a]",
         "-c:v", "libx264", "-preset", "slow", "-crf", "21", "-pix_fmt", "yuv420p",
         "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", dst], what="demo mp4")
    return dst


def make_capsule() -> str:
    """512×512 胶囊：P4 loading-e1（石斧远古）左半 × loading-e6（星门星舰）右半。

    依据：任务卡标题口径「石斧/星舰同防线分屏」；素材=expand-v1 已有 A 档
    概念图零新生成（P4 批 loading 面，768×512 → 中心竖裁 256×512）。卡片
    括号举例（P1 的 E1/E2 题图）与标题口径冲突时取标题——E6 才是「星舰」。
    """
    e1 = os.path.join(REPO_ROOT, "game", "assets", "expand-v1", "P4", "loading", "loading-e1.png")
    e6 = os.path.join(REPO_ROOT, "game", "assets", "expand-v1", "P4", "loading", "loading-e6.png")
    for path in (e1, e6):
        if not os.path.exists(path):
            raise SystemExit(f"胶囊素材缺失：{path}")
    dst = os.path.join(STORE_DIR, f"capsule-512-{FILE_VERSION}.png")
    title_txt = os.path.join(OUT_DIR, "capsule-title.txt")
    with open(title_txt, "w", encoding="utf-8") as fh:
        fh.write("我在游戏觉醒系统")
    run(["ffmpeg", "-y", "-v", "error", "-i", e1, "-i", e6,
         "-filter_complex",
         ("[0:v]scale=512:512:force_original_aspect_ratio=increase,crop=256:512:(iw-256)/2:0[left];"
          "[1:v]scale=512:512:force_original_aspect_ratio=increase,crop=256:512:(iw-256)/2:0[right];"
          "[left][right]hstack=inputs=2,"
          "drawbox=x=254:y=0:w=4:h=512:color=0x06121F@0.85:t=fill,"
          "vignette=PI/5,"
          f"drawtext=fontfile={fpath(FONT_CJK)}:textfile={fpath(title_txt)}"
          ":fontsize=26:fontcolor=white@0.96:box=1:boxcolor=0x06121F@0.55:boxborderw=8:x=(w-tw)/2:y=h-120,"
          + badge(y_expr="h-th-10", size=17).replace(":x=w-tw-8", ":x=w-tw-14")),
         "-frames:v", "1", dst], what="capsule")
    return dst


def make_shots() -> list[str]:
    made = []
    for name, frame, _desc in SHOTS:
        dst = os.path.join(STORE_DIR, f"{name}-{FILE_VERSION}.png")
        run(["ffmpeg", "-y", "-v", "error", "-i", MASTER_AVI,
             "-vf", f"select='eq(n,{frame})',{badge(size=14)}",
             "-fps_mode", "passthrough", "-frames:v", "1", dst], what=f"shot {name}")
        made.append(dst)
    return made


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep-intermediate", action="store_true")
    args = ap.parse_args()

    if not shutil.which("ffmpeg"):
        raise SystemExit("ffmpeg 不在 PATH（winget Gyan.FFmpeg 已装应可用）")
    if not os.path.exists(MASTER_AVI):
        raise SystemExit(f"母片缺失：{MASTER_AVI}（先跑 tools/release/record_t108.sh）")

    os.makedirs(STORE_DIR, exist_ok=True)
    info = probe(MASTER_AVI)
    print(f"master: {info.get('width')}x{info.get('height')} {info.get('avg_frame_rate')}fps "
          f"{info.get('duration')}s {os.path.getsize(MASTER_AVI) // 1024}KB")

    # ---- 段素材 -------------------------------------------------------------
    seg_break = os.path.join(OUT_DIR, "seg-break.mp4")
    seg_order = os.path.join(OUT_DIR, "seg-order.mp4")
    seg_hero_a = os.path.join(OUT_DIR, "seg-hero-a.mp4")
    seg_hero_b = os.path.join(OUT_DIR, "seg-hero-b.mp4")
    cut(MASTER_AVI, *SEG_BREAKCHAIN, seg_break)
    cut(MASTER_AVI, *SEG_ORDER, seg_order)
    cut(MASTER_AVI, *SEG_HERO_A, seg_hero_a)
    cut(MASTER_AVI, *SEG_HERO_B, seg_hero_b)

    # ---- GIF ①断链单段 -------------------------------------------------------
    gif_break = os.path.join(STORE_DIR, f"demo-breakchain-{FILE_VERSION}.gif")
    make_gif(seg_break, gif_break, speed=1.35,
             caption=None,
             capfile=write_caption("cap-break.txt", "① 补给断链：塔弹尽哑火，敌军漏过防线"))

    # ---- GIF ②军令单段 -------------------------------------------------------
    gif_order = os.path.join(STORE_DIR, f"demo-order-build-{FILE_VERSION}.gif")
    make_gif(seg_order, gif_order, speed=1.15,
             caption=None,
             capfile=write_caption("cap-order.txt", "② 一句军令：方案卡审批，塔列队开建"))

    # ---- 双拼主 GIF（顺序拼接：先断链后军令；单次 palette 全链一致性） --------
    hero_dst = os.path.join(STORE_DIR, f"hero-dual-{FILE_VERSION}.gif")
    hero_seg = os.path.join(OUT_DIR, "seg-hero.mp4")
    run(["ffmpeg", "-y", "-v", "error", "-i", seg_hero_a, "-i", seg_hero_b,
         "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[out]",
         "-map", "[out]", "-c:v", "libx264", "-preset", "medium", "-crf", "18",
         "-pix_fmt", "yuv420p", "-an", hero_seg], what="hero concat seg")
    # 双段字幕各自只在其时段出现：前段烧「① 断链减速」，后段烧「② 军令代建」
    seg_a_dur = (SEG_HERO_A[1] - SEG_HERO_A[0]) / FPS_MASTER / 1.2
    cap_a = write_caption("cap-hero-a.txt", "① 断链减速：弹尽塔哑火，敌漏防线")
    cap_b = write_caption("cap-hero-b.txt", "② 军令代建：一句军令，塔列队就位")
    cap_chain = (
        f"drawtext=fontfile={fpath(FONT_CJK)}:textfile={fpath(cap_a)}"
        ":fontsize=15:fontcolor=0xE8F6FF@0.95:box=1:boxcolor=0x06121F@0.62:boxborderw=5:x=10:y=10:"
        f"enable='lte(t,{seg_a_dur:.2f})',"
        f"drawtext=fontfile={fpath(FONT_CJK)}:textfile={fpath(cap_b)}"
        ":fontsize=15:fontcolor=0xE8F6FF@0.95:box=1:boxcolor=0x06121F@0.62:boxborderw=5:x=10:y=10:"
        f"enable='gt(t,{seg_a_dur:.2f})'")
    run(["ffmpeg", "-y", "-v", "error", "-i", hero_seg,
         "-vf",
         badge(size=15) + ",setpts=0.83333*PTS,fps=20," + cap_chain
         + ",split[s0][s1];[s0]palettegen=max_colors=192:stats_mode=diff[p];"
           "[s1][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle",
         "-loop", "0", hero_dst], what="hero dual gif")

    # ---- 60s MP4 / 胶囊 / 截图 ------------------------------------------------
    demo_mp4 = make_mp4()
    capsule = make_capsule()
    shots = make_shots()

    # ---- 汇总校验（ffprobe 实测；不合规即失败） -------------------------------
    limits = {gif_break: 10 << 20, gif_order: 10 << 20, hero_dst: 10 << 20, demo_mp4: 60 << 20}
    print("\n== 物料核验 ==")
    failures = []
    for path, cap in limits.items():
        size = os.path.getsize(path)
        gi = probe(path)
        dur = float(gi.get("duration", "0") or 0)
        note = "OK" if size <= cap else "OVER-SIZE"
        if size > cap:
            failures.append(path)
        print(f"  {os.path.basename(path)}: {gi.get('width')}x{gi.get('height')} "
              f"{dur:.1f}s {size / 1024 / 1024:.2f}MB {note}")
    if hero_dst:
        hi = probe(hero_dst)
        if float(hi.get("duration", "0") or 0) > 15.5:
            failures.append(hero_dst)
            print(f"  hero-dual 超时长: {hi.get('duration')}s")
    if failures:
        raise SystemExit(f"物料超限：{failures}")
    for path in shots + [capsule]:
        pi = probe(path)
        print(f"  {os.path.basename(path)}: {pi.get('width')}x{pi.get('height')} "
              f"{os.path.getsize(path) / 1024:.0f}KB")

    if not args.keep_intermediate:
        for tmp in (seg_break, seg_order, seg_hero_a, seg_hero_b, hero_seg,
                    os.path.join(OUT_DIR, "hero-a.gif"), os.path.join(OUT_DIR, "hero-b.gif"),
                    os.path.join(OUT_DIR, "palette.png"), os.path.join(OUT_DIR, "title.mp4"),
                    os.path.join(OUT_DIR, "body.mp4")):
            if os.path.exists(tmp):
                os.remove(tmp)
    print("\nstore_materials: 全部物料就绪 →", STORE_DIR)


def write_caption(name: str, text: str) -> str:
    path = os.path.join(OUT_DIR, name)
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(text)
    return path


if __name__ == "__main__":
    main()
