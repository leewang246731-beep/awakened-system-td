#!/usr/bin/env python3
"""
test_mod_validate.py — mod_validate.py 自测（任务 T-103；零三方依赖，
`python3 test_mod_validate.py` 直跑）。

用例（正反样例，全部临时目录、用后删除）：
  1. clean_samples_pass       正样例：仓库自带 game/assets/mods/samples 两模组
                              + 本体内容预载 → 0 FAIL、退出码 0、装载序=按 id
  2. bad_schema_line_cited    反样例：坏 class 枚举 → SCHEMA FAIL 带 file:line
                              +JSON 指针（行号=真实行）
  3. unknown_dsl_op_rejected  DSL 三族：未知效果原语 op / 未知系统命令 verb /
                              落错内容类型 → DSL FAIL + 行号
  4. namespace_patch_rules    命名空间：跨命名空间无 patch → NAMESPACE；patch
                              目标缺失（未预载本体）→ PATCH；本体预载后 → 0 FAIL；
                              非 patch 同 id 冲突 → DUPLICATE
  5. dependency_rules         依赖：缺失依赖（含级联）→ DEPENDENCY；循环依赖 →
                              CYCLE（环成员逐个报）
  6. manifest_rules           包形态：目录缺 mod.json / mod_id 违规 / 缺字段 /
                              重复 mod_id → MANIFEST
  7. multi_error_collection   多错误一次收集（不遇错即停）+ 好模组照常装载
  8. cli_exit_codes_and_json  CLI 退出码（0/1）与 --json 报告结构
  9. malformed_json_line      坏 JSON → JSON FAIL 带解析器行号
"""

from __future__ import annotations

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))

_spec = importlib.util.spec_from_file_location(
    "mod_validate", os.path.join(HERE, "mod_validate.py"))
mv = importlib.util.module_from_spec(_spec)
sys.modules["mod_validate"] = mv
_spec.loader.exec_module(mv)

PASS = 0
FAIL = 0


def check(cond: bool, label: str) -> None:
    global PASS, FAIL
    if cond:
        PASS += 1
        print(f"  ok   {label}")
    else:
        FAIL += 1
        print(f"  FAIL {label}")


def write(path: str, content: str) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(content)


MANIFEST = """{
  "mod_id": "{mid}",
  "name_zh": "模组{mid}",
  "name_en": "Mod {mid}",
  "version": "1.0.0",
  "requires": [{reqs}]
}
"""

GOOD_BUILDING = """{
  "content_type": "Building",
  "items": [
    {
      "id": "{iid}",
      "content_type": "Building",
      "class": "pierce",
      "name_zh": "测试塔",
      "name_en": "Test Tower",
      "tier": 1,
      "max_tier": 2,
      "cost_total": 45,
      "dps": 15.0,
      "damage_per_shot": 15,
      "reload_ticks": 30,
      "range_cells": 4.5,
      "hp": 130,
      "ammo_per_shot": 0,
      "tier_growth": {
        "dps_multiplier_per_tier": 2.0,
        "cost_multiplier_per_tier": 3.0,
        "range_multiplier_per_tier": 1.25
      },
      "traits": {}
    }
  ]
}
"""

# class 故意坏在第 7 行（items[0] 从第 4 行起）
BAD_CLASS_BUILDING = GOOD_BUILDING.replace('"class": "pierce"',
                                           '"class": "laser"')


def manifest(mid: str, *requires: str) -> str:
    reqs = "".join(f'"{r}", ' for r in requires).rstrip(", ")
    return MANIFEST.replace("{mid}", mid).replace("{reqs}", reqs)


def run_validator(mods_dir: str, content_dir: str = None):
    return mv.ModValidator().run(mods_dir, content_dir)


# ---------------------------------------------------------------- 用例

def case_clean_samples_pass() -> None:
    print("1. clean_samples_pass")
    findings, order, docs = run_validator(
        os.path.join(REPO, "game", "assets", "mods", "samples"),
        os.path.join(REPO, "game", "content"))
    check(findings == [], f"0 FAIL（实际 {len(findings)}）")
    check(order == ["more-arrows", "rebalance-patch"], "装载序=按 id 排序")
    check(docs == 3, f"3 份内容文档（实际 {docs}）")


def case_bad_schema_line_cited() -> None:
    print("2. bad_schema_line_cited")
    tmp = tempfile.mkdtemp(prefix="mv_test_")
    try:
        write(os.path.join(tmp, "bad", "mod.json"), manifest("bad"))
        write(os.path.join(tmp, "bad", "content", "buildings.json"), BAD_CLASS_BUILDING)
        findings, _order, _docs = run_validator(tmp)
        match = [f for f in findings if f.check == "SCHEMA" and f.pointer == "/items/0/class"]
        check(len(match) == 1, "SCHEMA FAIL 恰一条（/items/0/class）")
        if match:
            f = match[0]
            check(f.file == "bad/content/buildings.json", "file=模组相对路径")
            check(f.line == 7, f"line=真实行号 7（实际 {f.line}）")
            check("'class'" in f.message and "laser" in f.message, "消息含字段与坏值")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


DSL_CASES = [
    # (字段, entry, 内容类型, 期望 check 关键字)
    ("on_hit", '{"op": "instant_win"}', "Building", "unknown op 'instant_win'"),
    ("system_commands", '{"verb": "SpawnBoss"}', "Scroll", "unknown verb 'SpawnBoss'"),
    ("world_ops", '{"op": "reveal_zone","zone":"z1"}', "Building",
     "not valid on Building"),
    ("on_hit", '{"op":"slow_percent","percent":150,"ticks":30}', "Building",
     "out of range"),
]


def case_unknown_dsl_op_rejected() -> None:
    print("3. unknown_dsl_op_rejected")
    for i, (field, entry, ctype, expect) in enumerate(DSL_CASES):
        tmp = tempfile.mkdtemp(prefix="mv_test_")
        try:
            doc = GOOD_BUILDING.replace('"traits": {}',
                                        f'"traits": {{}},\n      "{field}": [{entry}]')
            doc = doc.replace('"content_type": "Building"',
                              f'"content_type": "{ctype}"', 1)
            # Scroll 深表不存在（走通用块）；Building 深表要求仍在——逐例适配
            if ctype == "Scroll":
                doc = """{
  "content_type": "Scroll",
  "items": [
    {
      "id": "sc",
      "content_type": "Scroll",
      "name_zh": "卷轴", "name_en": "Scroll",
      "%s": [%s]
    }
  ]
}
""" % (field, entry)
            write(os.path.join(tmp, "mm", "mod.json"), manifest("mm"))
            fname = {"Building": "buildings.json", "Scroll": "scrolls.json",
                     "Projectile": "projectiles.json"}.get(ctype, "items.json")
            write(os.path.join(tmp, "mm", "content", fname), doc)
            findings, _order, _docs = run_validator(tmp)
            hits = [f for f in findings if f.check == "DSL" and expect in f.message]
            check(len(hits) >= 1, f"DSL {i+1}: {expect}（{field}@{ctype}）")
            if hits:
                check(hits[0].line >= 1, f"DSL {i+1}: 行号非零")
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


def case_namespace_patch_rules() -> None:
    print("4. namespace_patch_rules")
    tmp = tempfile.mkdtemp(prefix="mv_test_")
    try:
        # 4a. 跨命名空间无 patch
        write(os.path.join(tmp, "mm", "mod.json"), manifest("mm"))
        write(os.path.join(tmp, "mm", "content", "b.json"),
              GOOD_BUILDING.replace('"id": "{iid}"', '"id": "other:tower"'))
        findings, _o, _d = run_validator(tmp)
        check(any(f.check == "NAMESPACE" and "foreign namespace" in f.message
                  for f in findings), "跨命名空间无 patch → NAMESPACE")
        # 4b. patch 目标缺失（未预载本体）
        shutil.rmtree(os.path.join(tmp, "mm"))
        write(os.path.join(tmp, "mm", "mod.json"), manifest("mm"))
        patch_doc = GOOD_BUILDING.replace('"id": "{iid}"',
                                          '"id": "era1:tower-wood-arrow",\n      "patch": true')
        write(os.path.join(tmp, "mm", "content", "b.json"), patch_doc)
        findings, _o, _d = run_validator(tmp)
        check(any(f.check == "PATCH" for f in findings),
              "patch 目标缺失 → PATCH")
        # 4c. 本体预载后同 payload → 0 FAIL
        findings, _o, _d = run_validator(
            tmp, os.path.join(REPO, "game", "content"))
        check(findings == [], f"本体预载后 patch 通过（实际 {len(findings)}）")
        # 4d. 非 patch 同 id 冲突（同模组裸+显式自命名空间 → 同 full id）
        shutil.rmtree(os.path.join(tmp, "mm"))
        write(os.path.join(tmp, "mm", "mod.json"), manifest("mm"))
        one = """    {
      "id": "REPL",
      "content_type": "Building",
      "class": "pierce", "name_zh": "塔", "name_en": "T",
      "tier": 1, "max_tier": 2, "cost_total": 45, "dps": 15.0,
      "damage_per_shot": 15, "reload_ticks": 30, "range_cells": 4.5,
      "hp": 130, "ammo_per_shot": 0,
      "tier_growth": { "dps_multiplier_per_tier": 2.0,
                       "cost_multiplier_per_tier": 3.0,
                       "range_multiplier_per_tier": 1.25 },
      "traits": {}
    }"""
        doc = ('{\n  "content_type": "Building",\n  "items": [\n'
               + one.replace("REPL", "clash") + ",\n"
               + one.replace("REPL", "mm:clash") + '\n  ]\n}\n')
        write(os.path.join(tmp, "mm", "content", "b.json"), doc)
        findings, _o, _d = run_validator(tmp)
        check(any(f.check == "DUPLICATE" and "mm:clash" in f.message
                  for f in findings), "同 id 非 patch 冲突 → DUPLICATE")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def case_dependency_rules() -> None:
    print("5. dependency_rules")
    tmp = tempfile.mkdtemp(prefix="mv_test_")
    try:
        write(os.path.join(tmp, "aa", "mod.json"), manifest("aa", "ghost"))
        write(os.path.join(tmp, "bb", "mod.json"), manifest("bb", "aa"))
        write(os.path.join(tmp, "cc", "mod.json"), manifest("cc"))
        write(os.path.join(tmp, "cc", "content", "b.json"),
              GOOD_BUILDING.replace("{iid}", "tower-cc"))
        findings, order, _d = run_validator(tmp)
        dep = [f for f in findings if f.check == "DEPENDENCY"]
        check(any(f.mod == "aa" and "ghost" in f.message for f in dep),
              "缺失依赖 → DEPENDENCY（a）")
        check(any(f.mod == "bb" for f in dep), "级联：依赖失败者的依赖者也失败（b）")
        check(order == ["cc"], "环外/无关模组照常装载")

        shutil.rmtree(os.path.join(tmp, "aa"))
        shutil.rmtree(os.path.join(tmp, "bb"))
        write(os.path.join(tmp, "xx", "mod.json"), manifest("xx", "yy"))
        write(os.path.join(tmp, "yy", "mod.json"), manifest("yy", "xx"))
        findings, order, _d = run_validator(tmp)
        cyc = [f for f in findings if f.check == "CYCLE"]
        check({f.mod for f in cyc} == {"xx", "yy"}, "循环依赖两成员各报 CYCLE")
        check(order == ["cc"], "环成员剔除后仅剩 cc")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def case_manifest_rules() -> None:
    print("6. manifest_rules")
    tmp = tempfile.mkdtemp(prefix="mv_test_")
    try:
        write(os.path.join(tmp, "nopkg", "content", "b.json"), "{}")
        write(os.path.join(tmp, "badid", "mod.json"), manifest("Bad_ID"))
        write(os.path.join(tmp, "nofield", "mod.json"),
              manifest("nofield").replace('"version": "1.0.0",', ""))
        write(os.path.join(tmp, "solo", "mod.json"), manifest("solo"))
        write(os.path.join(tmp, "solo.json"), manifest("solo"))
        findings, _order, _d = run_validator(tmp)
        checks = {(f.check, f.mod) for f in findings}
        check(any(c[0] == "MANIFEST" and "nopkg" in f.message
                  for c, f in zip(checks, findings)), "目录缺 mod.json 报 MANIFEST")
        check(any(f.mod == "Bad_ID" or "Bad_ID" in f.message for f in findings),
              "mod_id 违规报 MANIFEST")
        check(any(f.mod == "nofield" and "version" in f.message for f in findings),
              "缺 version 报 MANIFEST")
        check(any("already claims mod id 'solo'" in f.message for f in findings),
              "重复 mod_id 报 MANIFEST")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def case_multi_error_collection() -> None:
    print("7. multi_error_collection")
    tmp = tempfile.mkdtemp(prefix="mv_test_")
    try:
        write(os.path.join(tmp, "badmod", "mod.json"), manifest("badmod"))
        # b1 坏 class；b2 坏 tier——两错都报，且文件可继续（多错误收集）
        doc = """{
  "content_type": "Building",
  "items": [
    { "id": "b1", "content_type": "Building", "class": "laser",
      "name_zh": "一", "name_en": "One", "tier": 1, "max_tier": 1,
      "cost_total": 1, "dps": 1, "damage_per_shot": 1, "reload_ticks": 30,
      "range_cells": 1, "hp": 1 },
    { "id": "b2", "content_type": "Building", "class": "pierce",
      "name_zh": "二", "name_en": "Two", "tier": 9, "max_tier": 2,
      "cost_total": 1, "dps": 1, "damage_per_shot": 1, "reload_ticks": 30,
      "range_cells": 1, "hp": 1 }
  ]
}
"""
        write(os.path.join(tmp, "badmod", "content", "b.json"), doc)
        write(os.path.join(tmp, "goodmod", "mod.json"), manifest("goodmod"))
        write(os.path.join(tmp, "goodmod", "content", "b.json"),
              GOOD_BUILDING.replace("{iid}", "tower-good"))
        findings, order, _d = run_validator(tmp)
        schema = [f for f in findings if f.check == "SCHEMA"]
        check(len(schema) == 2, f"两条 item 错全收集（实际 {len(schema)}）")
        # badmod manifest 合格 → 包保留装载（坏 item 已剔除），goodmod 照常
        check(order == ["badmod", "goodmod"], f"两包都装载（实际 {order}）")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def case_cli_exit_codes_and_json() -> None:
    print("8. cli_exit_codes_and_json")
    tmp = tempfile.mkdtemp(prefix="mv_test_")
    try:
        samples = os.path.join(REPO, "game", "assets", "mods", "samples")
        env = dict(os.environ)
        env["PYTHONIOENCODING"] = "utf-8"
        r_ok = subprocess.run(
            [sys.executable, os.path.join(HERE, "mod_validate.py"), samples,
             "--content", os.path.join(REPO, "game", "content")],
            capture_output=True, text=True, env=env)
        check(r_ok.returncode == 0, "干净样例退出码 0")

        write(os.path.join(tmp, "mm", "mod.json"), manifest("mm"))
        write(os.path.join(tmp, "mm", "content", "b.json"), BAD_CLASS_BUILDING)
        json_path = os.path.join(tmp, "report.json")
        r_bad = subprocess.run(
            [sys.executable, os.path.join(HERE, "mod_validate.py"), tmp,
             "--json", json_path],
            capture_output=True, text=True, env=env)
        check(r_bad.returncode == 1, "违规树退出码 1")
        with open(json_path, encoding="utf-8") as fh:
            payload = json.load(fh)
        check(payload["summary"]["failures"] >= 1, "--json 报告含 failures")
        entry = payload["failures"][0]
        check(all(k in entry for k in
                  ("check", "mod", "file", "line", "pointer", "message")),
              "--json 字段齐全（file/line/pointer/...）")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def case_malformed_json_line() -> None:
    print("9. malformed_json_line")
    tmp = tempfile.mkdtemp(prefix="mv_test_")
    try:
        write(os.path.join(tmp, "mm", "mod.json"), manifest("mm"))
        write(os.path.join(tmp, "mm", "content", "b.json"),
              '{\n  "content_type": "Building",\n  "items": [ oops\n')
        findings, _o, _d = run_validator(tmp)
        hits = [f for f in findings if f.check == "JSON"]
        check(len(hits) == 1, "坏 JSON 报 JSON 一条")
        if hits:
            check(hits[0].line >= 3, f"解析器行号（实际 {hits[0].line}）")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main() -> int:
    case_clean_samples_pass()
    case_bad_schema_line_cited()
    case_unknown_dsl_op_rejected()
    case_namespace_patch_rules()
    case_dependency_rules()
    case_manifest_rules()
    case_multi_error_collection()
    case_cli_exit_codes_and_json()
    case_malformed_json_line()
    print(f"\n{PASS} passed, {FAIL} failed")
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
