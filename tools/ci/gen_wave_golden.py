#!/usr/bin/env python3
"""gen_wave_golden.py — T-035 wave golden table generator/checker (F-171).

game/content/golden/wave-e2-golden.json carries waves 1..30 of the E2
benchmark ({wave, count, elite, shield, interval_s}). The wave parameters
(threat t, base/begin/spacing/scaling, shield start wave) live in the
golden file's own header; this tool recomputes every row from them with a
Python transcription of the frozen T-036 formulas (v3.2 §6/§7 + F.2):

  count      = floor(clamp((base + max(0, floor((wave-begin)/spacing)))
                          / max(scaling, 0.1), 0, 40) * count_mult)
  count_mult = 5*t/3                       (F.2 数量乘子, 07 #26 bracketization)
  elite      = max(0, 1 + floor((wave-8)/4))          (精英线)
  shield     = 10 + 8*wave if wave >= start_wave else 0 (护盾 E3 起)
  interval_s = round(120 * clamp(2 - 1.4*(t-0.3)/0.9, 0.5, 2), 4)

Modes (CS-6.1 金样更新纪律):
  python tools/ci/gen_wave_golden.py                # CHECK (default): verify
                                                   # the committed rows match
                                                   # the formulas, exit 1 on
                                                   # drift
  python tools/ci/gen_wave_golden.py --update-golden
                                                   # REGENERATE rows from the
                                                   # header parameters and
                                                   # rewrite the file (only
                                                   # sanctioned way to change
                                                   # golden content)
"""

import argparse
import json
import math
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
GOLDEN_PATH = REPO_ROOT / "game" / "content" / "golden" / "wave-e2-golden.json"

WAVE_RANGE = range(1, 31)  # waves 1..30 (F.5 benchmark horizon, 第 30 波门禁)


def multipliers(t):
    """F.2 triple: count (5/3)t, hp 0.5+(t-0.3)/0.9, interval clamped [0.5, 2]."""
    return {
        "count": 5.0 * t / 3.0,
        "hp": 0.5 + (t - 0.3) / 0.9,
        "interval": min(2.0, max(0.5, 2.0 - 1.4 * (t - 0.3) / 0.9)),
    }


def wave_count(wave, base, begin, spacing, scaling, count_mult):
    """v3.2 §6 数量公式 (07 #26 括号化): add, divide, clamp 0..40, multiply, floor."""
    if wave < 0 or count_mult < 0:
        return 0
    increment = max(0, (wave - begin) // spacing) if spacing > 0 else 0
    ramped = (base + increment) / max(scaling, 0.1)
    capped = min(40.0, max(0.0, ramped))
    return math.floor(capped * count_mult)


def elite_count(wave):
    """精英线 1+floor((wave-8)/4), clamped at 0 below wave 8."""
    if wave < 0:
        return 0
    return max(0, 1 + (wave - 8) // 4)


def shield_at(wave, start_wave):
    """护盾 10+8*wave from start_wave on (E3 起), 0 before."""
    if wave < start_wave:
        return 0
    return 10 + 8 * wave


def build_rows(spec):
    t = spec["threat"]
    params = spec["params"]
    mult = multipliers(t)
    rounding = spec["interval_s_rounding"]
    rows = []
    for wave in WAVE_RANGE:
        rows.append({
            "wave": wave,
            "count": wave_count(wave, params["base"], params["begin"],
                                params["spacing"], params["scaling"], mult["count"]),
            "elite": elite_count(wave),
            "shield": shield_at(wave, spec["shield_start_wave"]),
            "interval_s": round(spec["interval_base_s"] * mult["interval"], rounding),
        })
    return rows


def diff_rows(committed, expected):
    problems = []
    if len(committed) != len(expected):
        problems.append(f"row count: committed {len(committed)}, expected {len(expected)}")
        return problems
    for c, e in zip(committed, expected):
        for key in ("wave", "count", "elite", "shield"):
            if c.get(key) != e[key]:
                problems.append(f"wave {e['wave']} {key}: committed {c.get(key)}, expected {e[key]}")
        committed_interval = round(float(c.get("interval_s", 0.0)), 6)
        expected_interval = round(float(e["interval_s"]), 6)
        if abs(committed_interval - expected_interval) > 1e-6:
            problems.append(
                f"wave {e['wave']} interval_s: committed {c.get('interval_s')}, expected {e['interval_s']}")
    return problems


def load_spec():
    if not GOLDEN_PATH.is_file():
        print(f"gen_wave_golden: golden file missing: {GOLDEN_PATH}", file=sys.stderr)
        sys.exit(2)
    with GOLDEN_PATH.open(encoding="utf-8") as fh:
        doc = json.load(fh)
    for key in ("threat", "params", "shield_start_wave", "interval_base_s", "interval_s_rounding", "rows"):
        if key not in doc:
            print(f"gen_wave_golden: golden header missing key '{key}'", file=sys.stderr)
            sys.exit(2)
    return doc


def main():
    parser = argparse.ArgumentParser(description="T-035 wave golden generator/checker")
    parser.add_argument(
        "--update-golden",
        action="store_true",
        help="regenerate the golden rows from the header parameters (CS-6.1: the only sanctioned rewrite)",
    )
    args = parser.parse_args()

    doc = load_spec()

    if args.update_golden:
        doc["rows"] = build_rows(doc)
        GOLDEN_PATH.parent.mkdir(parents=True, exist_ok=True)
        with GOLDEN_PATH.open("w", encoding="utf-8", newline="\n") as fh:
            json.dump(doc, fh, ensure_ascii=False, indent=2)
            fh.write("\n")
        print(f"gen_wave_golden: regenerated {GOLDEN_PATH.relative_to(REPO_ROOT)} "
              f"({len(doc['rows'])} rows)")
        return

    problems = diff_rows(doc["rows"], build_rows(doc))
    if problems:
        for line in problems:
            print(f"gen_wave_golden: DRIFT {line}")
        print("gen_wave_golden: FAIL (regenerate with: "
              "python tools/ci/gen_wave_golden.py --update-golden)", file=sys.stderr)
        sys.exit(1)
    print(f"gen_wave_golden: OK ({len(doc['rows'])} rows match the T-036 formulas)")


if __name__ == "__main__":
    main()
