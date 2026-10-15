#!/usr/bin/env python3
"""
mod_validate.py — EraTD 模组离线校验器（任务 T-103，组件 C-30/C-02）

单文件、零三方依赖（Python 3.8+）。离线校验一个 mods 目录，判据与
EraCore/Mods 装载器（T-103）同一套封闭 schema 的 Python 侧镜像（供 CI 与
模组作者本地自检；装载器仍是唯一权威——两侧判据漂移=bug）：

  MANIFEST   包形态：目录包须有 mod.json（mod_id/name_zh/name_en/version/
             requires）；单文件 .json 自带同款头；mod_id 唯一且
             ^[a-z][a-z0-9-]{1,31}$
  DEPENDENCY 依赖：缺失依赖（不在可装载集，含级联）→ FAIL
  CYCLE      循环依赖 → FAIL（列出环成员）
  NAMESPACE  命名空间：裸 id 自动命名空间化 modid:id；跨命名空间全名 id 必须
             patch:true；patch 目标须为已装载内容（本体按 --content 预载）
  DUPLICATE  非 patch 的同 id 冲突 → FAIL（覆盖通道=patch:true）
  PATCH      patch 目标不存在 → FAIL
  SCHEMA     K-2 内容矩阵：content_type ∈ 15 类；items[] 逐条过封闭字段表
             （Building/Projectile/Enemy/Era 深表 + 通用块；未知普通键忽略——
             模组扩展缝，与 TowerSpec 同语义）
  DSL        三族封闭词表（F-094/F-158）：on_hit（效果原语）→
             Building/Projectile/StatusEffect；world_ops（世界操作）→
             Era/WaveGroup/Level；system_commands（系统命令）→ Scroll/Hero
             （verb ∈ K-3 19 动词）。未知 op/verb、落错内容类型、坏参数、
             entry 未声明键 → FAIL
  JSON       解析失败（带解析器行号）；IO 文件不可读

行级报错：SCHEMA/DSL 错误带 file:line + JSON 指针（与装载器 ModLoadError
同口径）；多错误一次全量收集（非遇错即停）。

用法：
  python3 mod_validate.py <mods_dir> [--content <game/content>] [--json <path>]
  退出码：0=无 FAIL；1=存在 FAIL；2=参数/运行错误。

规范依据：v2.1 §16 条 3/条 5（回收档案 G.1）——内容注册表 JSON/按 id 排序/
modid:contentid/坏模组弹错/CI 校验；DSL 三族=效果原语/世界操作/系统命令；
动词清单不可由模组扩展（K-3）。自测见 test_mod_validate.py。
"""

from __future__ import annotations

import argparse
import bisect
import json
import os
import re
import sys

# ---------------------------------------------------------------- 常量表

CONTENT_TYPES = {
    "Resource", "Building", "Projectile", "StatusEffect", "Enemy", "WaveGroup",
    "Era", "Tech", "Wonder", "Hero", "Scroll", "DropTable", "Level",
    "Localization", "ActionParamSpace",
}

MOD_ID_RE = re.compile(r"^[a-z][a-z0-9-]{1,31}$")
CONTENT_ID_RE = re.compile(r"^[a-z][a-z0-9-]*$")

TOWER_CLASSES = {"physical", "pierce", "splash", "energy", "slow", "support"}
TIER_LINES = {"standard", "specialization"}
LANE_STANCES = {"hold", "fall", "push"}

# K-3 19 动作（SystemActionKind 名；动词清单不可模组扩展）
SYSTEM_VERBS = {
    "LookAround", "InspectTile", "QueryWorld",
    "BuildPlan", "UpgradeTower", "SellTower", "SetRally",
    "RerouteLogistics", "SetStockpile",
    "FocusFire", "SetTowerMode", "TriggerScroll", "HeroOrder",
    "AskPlayer", "ReportToPlayer", "TodoBoard", "SpawnScout",
    "SetAlarm", "StopTask",
}

# DSL 效果原语 op 表：op → {arg: 规格}；规格=("num"|lo|hi) ("int"|lo|hi)
# ("text",) ("enum", 集合)
DSL_EFFECT_PRIMITIVES = {
    "damage_bonus_percent": {"percent": ("num", 0.0, 100.0)},
    "zone_damage_bonus": {"percent": ("num", 0.0, 100.0)},
    "slow_percent": {"percent": ("num", 0.0, 100.0), "ticks": ("int", 0.0, None)},
    "root_ticks": {"ticks": ("int", 0.0, None)},
    "burn_dps": {"dps": ("num", 0.0, None), "ticks": ("int", 0.0, None)},
    "splash_radius_cells": {"radius_cells": ("num", 0.0, None)},
    "heal_percent": {"percent": ("num", 0.0, 100.0)},
    "attack_speed_bonus_percent": {"percent": ("num", 0.0, None)},
    "range_bonus_cells": {"cells": ("num", 0.0, None)},
}
DSL_WORLD_OPS = {
    "grant_resource": {"resource": ("text",), "amount": ("num", 0.0, None)},
    "reveal_zone": {"zone": ("text",)},
    "set_lane_stance": {"lane": ("text",), "stance": ("enum", LANE_STANCES)},
    "spawn_wave_group": {"wave_group": ("text",)},
    "set_terrain_cost": {"x": ("int", 0.0, None), "y": ("int", 0.0, None),
                         "cost": ("num", 0.0, None)},
}
# 字段 → (族名, 允许内容类型, op 表 / verb 集)
DSL_FIELDS = {
    "on_hit": ("effect primitives", {"Building", "Projectile", "StatusEffect"},
               DSL_EFFECT_PRIMITIVES),
    "world_ops": ("world operations", {"Era", "WaveGroup", "Level"},
                  DSL_WORLD_OPS),
    "system_commands": ("system commands", {"Scroll", "Hero"}, SYSTEM_VERBS),
}

# 深表：字段 → (类型, required, 最小, 最大, 枚举集)；None=无界
DEEP_FIELDS = {
    "Building": {
        "class": ("enum", True, None, None, TOWER_CLASSES),
        "tier": ("int", True, 1, 4, None),
        "max_tier": ("int", True, 1, 4, None),
        "cost_total": ("num", True, 0, None, None),
        "dps": ("num", True, 0, None, None),
        "damage_per_shot": ("num", True, 0, None, None),
        "reload_ticks": ("int", False, 0, None, None),
        "range_cells": ("num", True, 0, None, None),
        "hp": ("int", True, 1, None, None),
        "ammo_item": ("text", False, None, None, None),
        "ammo_per_shot": ("int", False, 0, None, None),
        "upgrades_to": ("text", False, None, None, None),
        "tier_line": ("enum", False, None, None, TIER_LINES),
    },
    "Projectile": {
        "damage_per_shot": ("num", True, 0, None, None),
        "speed_px_per_tick": ("num", True, 0, None, None),
        "pierce_count": ("int", False, 1, None, None),
    },
    "Enemy": {
        "hp": ("num", True, 1, None, None),
        "armor": ("num", True, 0, None, None),
        "speed_px_per_tick": ("num", True, 0, None, None),
        "sp_id": ("text", False, None, None, None),
    },
    "Era": {
        "era_index": ("int", True, 1, 6, None),
    },
}
NAMELESS_TYPES = {"ActionParamSpace", "Localization"}
TIER_GROWTH_KEYS = {"dps_multiplier_per_tier", "cost_multiplier_per_tier",
                    "range_multiplier_per_tier"}
TRAIT_KEYS = {
    "burst_windup_ticks", "one_shot", "root_ticks", "slow_percent", "slow_ticks",
    "aura_radius_cells", "aura_effects", "aura_regen_hp_per_s",
    "splash_radius_cells", "burn_dps", "burn_ticks",
}


class Finding:
    def __init__(self, check, mod, file, line, pointer, message):
        self.check, self.mod = check, mod
        self.file, self.line, self.pointer = file, line, pointer
        self.message = message

    def render(self):
        return (f"[{self.check}] {self.file}:{self.line or 1} "
                f"({self.mod or '-'}) {self.pointer}: {self.message}")


class LineIndex:
    """JSON 指针 → 1-based 行（与装载器 ModJsonLineMap 同构；名字 token 记
    值偏移行）。括号/引号配对扫描，解析失败保留已索引前缀。"""

    def __init__(self, text):
        self.pointer_lines = {}
        starts = [0]
        for i, ch in enumerate(text):
            if ch == "\n":
                starts.append(i + 1)
        self._starts = starts
        self._scan(text)

    def _scan(self, text):
        n = len(text)
        stack = []          # ("{", ptr) 或 ("[", ptr, next_index)
        pending_key = None  # 刚读过冒号的 key 指针（值 token 即将到来）
        i = 0
        while i < n:
            ch = text[i]
            if ch == '"':
                j = i + 1
                while j < n:
                    if text[j] == "\\":
                        j += 2
                        continue
                    if text[j] == '"':
                        break
                    j += 1
                token = text[i + 1:j]
                k = j + 1
                while k < n and text[k] in " \t\r\n":
                    k += 1
                if stack and stack[-1][0] == "{" and k < n and text[k] == ":":
                    ptr = f"{stack[-1][1]}/{token}"
                    k += 1
                    while k < n and text[k] in " \t\r\n":
                        k += 1
                    self.pointer_lines.setdefault(
                        ptr, bisect.bisect_right(self._starts, k))
                    pending_key = ptr
                i = j + 1
                continue
            if ch in "{[":
                if stack and stack[-1][0] == "[":
                    ptr = f"{stack[-1][1]}/{stack[-1][2]}"
                    stack[-1] = ("[", stack[-1][1], stack[-1][2] + 1)
                else:
                    ptr = pending_key or ""
                self.pointer_lines.setdefault(
                    ptr, bisect.bisect_right(self._starts, i))
                if ch == "{":
                    stack.append(("{", ptr))
                else:
                    stack.append(("[", ptr, 0))
                pending_key = None
                i += 1
                continue
            if ch in "}]":
                if stack:
                    stack.pop()
                pending_key = None
                i += 1
                continue
            if ch == ",":
                pending_key = None
                i += 1
                continue
            i += 1

    def line(self, pointer, default=1):
        return self.pointer_lines.get(pointer, default)


# ---------------------------------------------------------------- 校验器

class ModValidator:
    def __init__(self):
        self.findings = []
        self.packages = []   # [{mod_id, display, requires, files, rel, single}]
        self.overlay = {}    # (content_type, id) → source_mod（""=本体）

    def error(self, check, mod, file, line, pointer, message):
        self.findings.append(Finding(check, mod, file, line, pointer, message))

    # ---- 文件读取 -------------------------------------------------------

    def load_json(self, path, display, mod_id):
        try:
            with open(path, encoding="utf-8") as fh:
                text = fh.read()
        except OSError as exc:
            self.error("IO", mod_id, display, 1, "", str(exc))
            return None, None
        try:
            return json.loads(text), text
        except ValueError as exc:
            line = 1
            m = re.search(r"line (\d+)", str(exc))
            if m:
                line = int(m.group(1))
            self.error("JSON", mod_id, display, line, "", f"malformed JSON: {exc}")
            return None, None

    # ---- 发现 ------------------------------------------------------------

    def discover(self, mods_dir):
        if not os.path.isdir(mods_dir):
            return
        claimed = set()
        for name in sorted(os.listdir(mods_dir)):
            path = os.path.join(mods_dir, name)
            if name.endswith(".json") and os.path.isfile(path):
                self._claim_single_file(path, name, claimed)
            elif os.path.isdir(path):
                self._claim_directory(path, name, claimed)

    def _manifest_error(self, mod_id, display, pointer, message):
        self.error("MANIFEST", mod_id, display, 1, pointer, message)

    def _claim_single_file(self, path, name, claimed):
        root, _ = self.load_json(path, name, mod_id="")
        if not isinstance(root, dict):
            return
        mod_id = root.get("mod_id")
        if not isinstance(mod_id, str) or not mod_id.strip():
            self._manifest_error("", name, "/mod_id",
                                 f"single-file mod '{name}' lacks 'mod_id' at document root")
            return
        if not MOD_ID_RE.match(mod_id):
            self._manifest_error(mod_id, name, "/mod_id",
                                 f"mod_id '{mod_id}' must match lowercase a-z0-9- (len 2..32)")
            return
        if not self._manifest_fields_ok(root, mod_id, name):
            return
        if mod_id in claimed:
            self._manifest_error(mod_id, name, "/mod_id",
                                 f"another package already claims mod id '{mod_id}'")
            return
        claimed.add(mod_id)
        self.packages.append({"mod_id": mod_id, "display": name,
                              "requires": root.get("requires") or [],
                              "files": [path], "rel": [name], "single": True})

    def _claim_directory(self, path, name, claimed):
        manifest = os.path.join(path, "mod.json")
        display = f"{name}/mod.json"
        if not os.path.isfile(manifest):
            self._manifest_error("", display, "",
                                 f"directory package '{name}' lacks mod.json")
            return
        root, _ = self.load_json(manifest, display, mod_id="")
        if not isinstance(root, dict):
            return
        mod_id = root.get("mod_id")
        if not isinstance(mod_id, str) or not mod_id.strip():
            self._manifest_error("", display, "/mod_id",
                                 f"mod.json of '{name}' lacks 'mod_id'")
            return
        if not MOD_ID_RE.match(mod_id):
            self._manifest_error(mod_id, display, "/mod_id",
                                 f"mod_id '{mod_id}' must match lowercase a-z0-9- (len 2..32)")
            return
        if not self._manifest_fields_ok(root, mod_id, display):
            return
        if mod_id in claimed:
            self._manifest_error(mod_id, display, "/mod_id",
                                 f"another package already claims mod id '{mod_id}'")
            return
        claimed.add(mod_id)
        files, rel = [], []
        content_dir = os.path.join(path, "content")
        for base, _dirs, fnames in os.walk(content_dir):
            for fn in sorted(fnames):
                if fn.endswith(".json"):
                    files.append(os.path.join(base, fn))
                    rel.append(os.path.relpath(os.path.join(base, fn), path)
                               .replace("\\", "/"))
        self.packages.append({"mod_id": mod_id, "display": display,
                              "requires": root.get("requires") or [],
                              "files": files, "rel": rel, "single": False})

    def _manifest_fields_ok(self, root, mod_id, display):
        ok = True
        for field in ("name_zh", "name_en", "version"):
            value = root.get(field)
            if not isinstance(value, str) or not value.strip():
                self._manifest_error(mod_id, display, f"/{field}",
                                     f"manifest requires non-empty '{field}'")
                ok = False
        requires = root.get("requires", [])
        if requires and (not isinstance(requires, list) or any(
                not isinstance(r, str) or not r.strip() for r in requires)):
            self._manifest_error(mod_id, display, "/requires",
                                 "'requires' must be an array of mod id strings")
            ok = False
        return ok

    # ---- 依赖 ------------------------------------------------------------

    def load_order(self):
        by_id = {p["mod_id"]: p for p in self.packages}
        dropped = set()

        def first_missing(mod_id):
            for req in by_id[mod_id]["requires"]:
                if req not in by_id or req in dropped:
                    return req
            return None

        changed = True
        while changed:
            changed = False
            for mod_id in by_id:
                if mod_id in dropped:
                    continue
                miss = first_missing(mod_id)
                if miss is not None:
                    dropped.add(mod_id)
                    self.error("DEPENDENCY", mod_id, "mod.json", 1, "/requires",
                               f"requires '{miss}' which is not among the loadable mods")
                    changed = True

        color, walk_stack, in_cycle = {}, [], set()

        def visit(mod_id):
            state = color.get(mod_id, 0)
            if state == 1:
                try:
                    start = walk_stack.index(mod_id)
                except ValueError:
                    start = 0
                for member in walk_stack[start:]:
                    in_cycle.add(member)
                return
            if state == 2:
                return
            color[mod_id] = 1
            walk_stack.append(mod_id)
            for dep in by_id[mod_id]["requires"]:
                if dep in by_id and dep not in dropped:
                    visit(dep)
            walk_stack.pop()
            color[mod_id] = 2

        for mod_id in sorted(by_id):
            if mod_id not in dropped:
                visit(mod_id)
        for member in sorted(in_cycle):
            self.error("CYCLE", member, "mod.json", 1, "/requires",
                       f"part of a dependency cycle: {' -> '.join(sorted(in_cycle))}")

        remaining = {m: p for m, p in by_id.items()
                     if m not in dropped and m not in in_cycle}
        pending = {m: sum(1 for r in p["requires"] if r in remaining)
                   for m, p in remaining.items()}
        dependents = {m: [] for m in remaining}
        for mod_id, package in remaining.items():
            for req in package["requires"]:
                if req in remaining:
                    dependents[req].append(mod_id)
        ready = sorted(m for m, count in pending.items() if count == 0)
        order = []
        while ready:
            mod_id = ready.pop(0)
            order.append(remaining[mod_id])
            for dep in dependents[mod_id]:
                pending[dep] -= 1
                if pending[dep] == 0:
                    ready.append(dep)
                    ready.sort()
        return order

    # ---- 本体预载（patch 目标锚点） ---------------------------------------

    def preload_first_party(self, content_dir):
        if not content_dir or not os.path.isdir(content_dir):
            return
        for base, _dirs, fnames in os.walk(content_dir):
            for fn in sorted(fnames):
                if not fn.endswith(".json"):
                    continue
                path = os.path.join(base, fn)
                root, _ = self.load_json(path, fn, mod_id="")
                if not isinstance(root, dict):
                    continue
                ctype = root.get("content_type")
                items = root.get("items")
                if ctype not in CONTENT_TYPES or not isinstance(items, list):
                    continue
                for item in items:
                    if isinstance(item, dict) and isinstance(item.get("id"), str):
                        self.overlay[(ctype, item["id"])] = ""

    # ---- 内容文档 ----------------------------------------------------------

    def validate_content_file(self, path, display, mod_id):
        """返回 [(content_type, item_id, patch, line)]——通过 item 级校验者。"""
        root, text = self.load_json(path, display, mod_id=mod_id)
        if not isinstance(root, dict):
            if root is not None:
                self.error("SCHEMA", mod_id, display, 1, "",
                           "document root must be a JSON object")
            return []
        ctype = root.get("content_type")
        if ctype not in CONTENT_TYPES:
            self.error("SCHEMA", mod_id, display, 1, "/content_type",
                       f"document content_type must be a K-2 ContentType name "
                       f"(got {ctype!r})")
            return []
        items = root.get("items")
        if not isinstance(items, list):
            self.error("SCHEMA", mod_id, display, 1, "/items",
                       f"document of kind {ctype} lacks an 'items' array")
            return []
        lines = LineIndex(text)
        parsed = []
        for idx, item in enumerate(items):
            result = self._validate_item(ctype, item, idx, display, mod_id, lines)
            if result is not None:
                parsed.append((ctype, *result))
        return parsed

    def _validate_item(self, ctype, item, idx, display, mod_id, lines):
        pointer = f"/items/{idx}"
        item_line = lines.line(pointer)

        def err(check, ptr, line, message):
            self.error(check, mod_id, display, line or item_line, ptr, message)

        if not isinstance(item, dict):
            err("SCHEMA", pointer, item_line, f"{ctype} item must be a JSON object")
            return None
        item_id = item.get("id")
        if not isinstance(item_id, str) or not item_id.strip():
            err("SCHEMA", pointer + "/id", item_line, "item requires non-empty 'id'")
            return None
        declared = item.get("content_type")
        if declared != ctype:
            err("SCHEMA", pointer + "/content_type",
                lines.line(pointer + "/content_type", item_line),
                f"item must carry content_type '{ctype}' (got {declared!r})")
            return None
        if ctype not in NAMELESS_TYPES:
            for field in ("name_zh", "name_en"):
                if not isinstance(item.get(field), str) or not item[field].strip():
                    err("SCHEMA", f"{pointer}/{field}",
                        lines.line(f"{pointer}/{field}", item_line),
                        f"item requires non-empty '{field}'")
        aliases = item.get("aliases")
        if aliases is not None and (not isinstance(aliases, list) or any(
                not isinstance(a, str) or not a.strip() for a in aliases)):
            err("SCHEMA", pointer + "/aliases",
                lines.line(pointer + "/aliases", item_line),
                "'aliases' must be an array of non-empty strings")
        patch = item.get("patch") is True

        for field, rule in DEEP_FIELDS.get(ctype, {}).items():
            if field not in item or item[field] is None:
                if rule[1]:
                    err("SCHEMA", pointer, item_line, f"missing required field '{field}'")
                continue
            self._validate_field_value(item[field], rule, field, f"{pointer}/{field}",
                                       lines.line(f"{pointer}/{field}", item_line),
                                       err)

        tg = item.get("tier_growth")
        if tg is not None:
            self._validate_closed_block(tg, "tier_growth", TIER_GROWTH_KEYS,
                                        pointer, item_line, lines, err)
        traits = item.get("traits")
        if traits is not None:
            self._validate_traits(traits, pointer, item_line, lines, err)

        for field, (family, allowed, table) in DSL_FIELDS.items():
            if field not in item:
                continue
            ptr = f"{pointer}/{field}"
            if ctype not in allowed:
                err("DSL", ptr, lines.line(ptr, item_line),
                    f"DSL field '{field}' ({family}) is not valid on {ctype} content")
                continue
            array = item[field]
            if not isinstance(array, list):
                err("DSL", ptr, lines.line(ptr, item_line),
                    f"'{field}' must be an array of op objects")
                continue
            for i, entry in enumerate(array):
                self._validate_dsl_entry(entry, field, family, table,
                                         f"{ptr}/{i}",
                                         lines.line(f"{ptr}/{i}", lines.line(ptr, item_line)),
                                         lines, err)

        return (item_id, patch, item_line)

    def _validate_field_value(self, value, rule, field, ptr, line, err):
        ftype, _required, lo, hi, enums = rule
        if ftype == "enum":
            if not isinstance(value, str) or value not in (enums or set()):
                err("SCHEMA", ptr, line,
                    f"'{field}' must be one of {'|'.join(sorted(enums or set()))} "
                    f"(got {value!r})")
            return
        if ftype == "text":
            if not isinstance(value, str) or not value.strip():
                err("SCHEMA", ptr, line, f"'{field}' must be a non-empty string")
            return
        if not isinstance(value, (int, float)) or isinstance(value, bool):
            err("SCHEMA", ptr, line, f"'{field}' must be a number (got {value!r})")
            return
        if ftype == "int" and float(value) != int(value):
            err("SCHEMA", ptr, line, f"'{field}' must be an integer (got {value!r})")
            return
        number = float(value)
        if (lo is not None and number < lo) or (hi is not None and number > hi):
            err("SCHEMA", ptr, line,
                f"'{field}' out of range [{lo}, {hi if hi is not None else '∞'}] "
                f"(got {value!r})")

    def _validate_closed_block(self, block, name, keys, pointer, item_line, lines, err):
        ptr = f"{pointer}/{name}"
        if not isinstance(block, dict):
            err("SCHEMA", ptr, lines.line(ptr, item_line), f"'{name}' must be an object")
            return
        for key, value in block.items():
            if key.startswith("_"):
                continue
            child = f"{ptr}/{key}"
            line = lines.line(child, item_line)
            if key not in keys:
                err("SCHEMA", child, line,
                    f"'{name}' carries unknown key '{key}' (closed block)")
            elif not isinstance(value, (int, float)) or isinstance(value, bool):
                err("SCHEMA", child, line, f"'{name}.{key}' must be a number")

    def _validate_traits(self, traits, pointer, item_line, lines, err):
        ptr = f"{pointer}/traits"
        if not isinstance(traits, dict):
            err("SCHEMA", ptr, lines.line(ptr, item_line), "'traits' must be an object")
            return
        for key, value in traits.items():
            if key.startswith("_"):
                continue
            child = f"{ptr}/{key}"
            line = lines.line(child, item_line)
            if key not in TRAIT_KEYS:
                err("SCHEMA", child, line,
                    f"'traits' carries unknown trait '{key}' (closed SP-1b set)")
            elif key == "aura_effects":
                if not isinstance(value, list) or any(
                        not isinstance(v, str) for v in value):
                    err("SCHEMA", child, line,
                        "'traits.aura_effects' must be an array of strings")
            elif not isinstance(value, (int, float, bool)):
                err("SCHEMA", child, line,
                    f"'traits.{key}' must be a number or boolean")

    def _validate_dsl_entry(self, entry, field, family, table, eptr, eline, lines, err):
        if not isinstance(entry, dict):
            err("DSL", eptr, eline, f"{family} entry must be a JSON object")
            return
        op_field = "verb" if field == "system_commands" else "op"
        op = entry.get(op_field)
        if not isinstance(op, str) or not op.strip():
            err("DSL", eptr, eline,
                f"{family} entry requires non-empty '{op_field}'")
            return
        if field == "system_commands":
            if op not in table:
                err("DSL", eptr, eline,
                    f"{family}: unknown verb '{op}' — the 19-verb K-3 list is "
                    f"closed (动词不可扩展)")
                return
            known = {"verb", "args"}
        else:
            if op not in table:
                err("DSL", eptr, eline,
                    f"{family}: unknown op '{op}' (closed set: "
                    f"{'|'.join(sorted(table))})")
                return
            args_rule = table[op]
            for arg, spec in args_rule.items():
                if arg not in entry:
                    err("DSL", eptr, eline, f"op '{op}' requires arg '{arg}'")
                    continue
                value = entry[arg]
                aptr = f"{eptr}/{arg}"
                aline = lines.line(aptr, eline)
                if spec[0] == "text":
                    if not isinstance(value, str) or not value.strip():
                        err("DSL", aptr, aline,
                            f"arg '{arg}' of '{op}' must be a non-empty string")
                elif spec[0] == "enum":
                    if value not in spec[1]:
                        err("DSL", aptr, aline,
                            f"arg '{arg}' of '{op}' must be one of "
                            f"{'|'.join(sorted(spec[1]))}")
                else:
                    lo, hi = spec[1], spec[2]
                    if (not isinstance(value, (int, float))
                            or isinstance(value, bool)
                            or (spec[0] == "int" and float(value) != int(value))
                            or (lo is not None and value < lo)
                            or (hi is not None and value > hi)):
                        err("DSL", aptr, aline,
                            f"arg '{arg}' of '{op}' out of range (got {value!r})")
            known = {op_field} | set(args_rule)
        for key in entry:
            if key.startswith("_") or key in known:
                continue
            err("DSL", f"{eptr}/{key}", lines.line(f"{eptr}/{key}", eline),
                f"{family} entry '{op}' carries undeclared key '{key}' "
                f"(closed shape)")

    # ---- 命名空间 / overlay -----------------------------------------------

    def apply_namespace(self, ctype, item_id, patch, mod_id, display, line):
        def err(check, message):
            self.error(check, mod_id, display, line, "/id", message)

        if patch:
            if ":" not in item_id:
                err("NAMESPACE",
                    "patch:true requires the FULL namespaced target id "
                    "('modid:contentid' or first-party 'era1:…')")
                return None
            if (ctype, item_id) not in self.overlay:
                err("PATCH",
                    f"patch target '{item_id}' is not loaded under {ctype} "
                    f"(patch may only replace existing content)")
                return None
            self.overlay[(ctype, item_id)] = mod_id
            return item_id

        if ":" in item_id:
            prefix, rest = item_id.split(":", 1)
            if prefix != mod_id:
                err("NAMESPACE",
                    f"id '{item_id}' uses foreign namespace '{prefix}' without "
                    f"patch:true — mods add under their own '{mod_id}:' namespace "
                    f"or patch existing content")
                return None
            if not CONTENT_ID_RE.match(rest):
                err("NAMESPACE", f"contentid part '{rest}' is invalid")
                return None
            full_id = item_id
        else:
            if not CONTENT_ID_RE.match(item_id):
                err("NAMESPACE",
                    f"content id '{item_id}' must be lowercase a-z0-9- "
                    f"(auto-namespaced '{mod_id}:{item_id}')")
                return None
            full_id = f"{mod_id}:{item_id}"

        if (ctype, full_id) in self.overlay:
            err("DUPLICATE",
                f"duplicate id '{full_id}' under {ctype} (use patch:true to override)")
            return None
        self.overlay[(ctype, full_id)] = mod_id
        return full_id

    # ---- 主流程 ------------------------------------------------------------

    def run(self, mods_dir, content_dir):
        self.preload_first_party(content_dir)
        self.discover(mods_dir)
        order = self.load_order()
        loaded_docs = 0
        for package in order:
            mod_id = package["mod_id"]
            for path, rel in zip(package["files"], package["rel"]):
                display = rel if package["single"] else f"{mod_id}/{rel}"
                for ctype, item_id, patch, line in self.validate_content_file(
                        path, display, mod_id):
                    self.apply_namespace(ctype, item_id, patch, mod_id, display, line)
                loaded_docs += 1
        return self.findings, [p["mod_id"] for p in order], loaded_docs


# ---------------------------------------------------------------- CLI

def main(argv=None):
    parser = argparse.ArgumentParser(
        description="EraTD mod offline validator (T-103; mirrors EraCore/ModLoader)")
    parser.add_argument("mods_dir", help="mods 目录（或单个包所在目录）")
    parser.add_argument("--content", default=None,
                        help="本体内容目录（game/content；patch 目标预载）")
    parser.add_argument("--json", dest="json_out", default=None,
                        help="机器报告输出路径")
    args = parser.parse_args(argv)

    mods_dir = args.mods_dir
    if os.path.isfile(mods_dir) and mods_dir.endswith(".json"):
        mods_dir = os.path.dirname(os.path.abspath(mods_dir))
    if not os.path.isdir(mods_dir):
        print(f"mod_validate: mods 目录不存在：{mods_dir}", file=sys.stderr)
        return 2

    findings, order, loaded_docs = ModValidator().run(mods_dir, args.content)
    for finding in findings:
        print("FAIL " + finding.render())
    print(f"summary: {len(findings)} FAIL, {len(order)} mods loaded "
          f"({loaded_docs} content documents)")

    if args.json_out:
        payload = {
            "mods_dir": mods_dir,
            "order": order,
            "failures": [
                {"check": f.check, "mod": f.mod, "file": f.file, "line": f.line,
                 "pointer": f.pointer, "message": f.message}
                for f in findings
            ],
            "summary": {"failures": len(findings), "mods": len(order)},
        }
        with open(args.json_out, "w", encoding="utf-8") as fh:
            json.dump(payload, fh, ensure_ascii=False, indent=1)

    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
