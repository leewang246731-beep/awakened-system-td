#!/usr/bin/env python3
"""
redline_scan.py — EraTD CI 红线扫描器（任务 T-027，Wave 0，组件 C-30）

单文件、零三方依赖（Python 3.8+，`python3 redline_scan.py` 直跑）。
实现 docs/engineering/06-quality-discussion.md §3.1 二十三条检查中当前对象
已存在、可机械化的首批 7 条；其余（R1-R4/R6-R8/R10-R12/R14/R15/T5-T8）
随其对象（数据表/流场/语料/金样/覆盖率工具）落地后在后续任务启用。

  R9   绝对/盘符路径扫描（RL9：本机绝对路径禁入代码/测试/配置）
       白名单：tools/**、.gitignore、docs 中登记的环境事实（见 allowlist.txt）
  T1   残留标记扫描（TODO/FIXME/HACK/XXX/WIP/stub/placeholder/占位/待补/
       暂不实现/伪实现/假成功；仅注释行；范围=game/src + game/presentation）
  T2   src↔test 文件映射（game/src/EraCore 每个源文件须有对应 *Tests 文件，
       或在 allowlist.txt 以 `T2:` 条目声明为纯契约/纯数据文件）
  T4   恒真/空断言（Assert.True(true)、字面恒等式、Equal 两参同表达式、
       空 catch、测试方法体内无任何断言）
  R5   密钥模式（sk-[A-Za-z0-9]{20,} 与 api_key/secret/token/password
       非空非占位字面量；排除 config/、docs/、tools/）
  R13  git 基线 8402414 可达性（RL13；--baseline 可覆盖供自测）
  T3   测试方法 F-ID/C-ID 关联覆盖率（`// [F-xxx]` 注释格式；WARNING 级，
       不影响退出码——报告缺口供 review 与回填）

豁免机制（D-18：豁免须可审查）：
  行级   ：违规行或其上一行含 `ci-allow: R9 T-027 <理由>`（列出检查 ID 或 *）
  路径级 ：tools/ci/allowlist.txt，每条带理由；`路径 :: 节标题` 仅豁免该节
           （节标题行起至下一个 "## " 标题前）；`ID: 路径` 前缀把豁免限定到
           单个检查。

输出：stdout 人读报告；--json <path> 另存机器报告（违规列表+计数+退出码）。
退出码：0=无 FAIL（WARNING 允许）；1=存在 FAIL；2=参数/运行错误。

自测：tools/ci/test_redline_scan.py（零依赖直跑，含正反样例与豁免样例）。
规范依据：docs/CODE_STANDARDS.md（T-026）；docs/engineering/07-decided-plan.md D-18。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass, field

DEFAULT_BASELINE = "8402414"  # RL13：v2.1 基线（07-decided-plan 环境事实表）

# ---------------------------------------------------------------- 常量表

TEXT_EXTS = {
    ".cs", ".gd", ".py", ".sh", ".ps1", ".md", ".txt", ".json", ".tscn",
    ".godot", ".cfg", ".props", ".targets", ".csproj", ".sln", ".config",
    ".xml", ".yaml", ".yml", ".editorconfig", ".gitignore", ".gitattributes",
    ".import", ".tres",
}
TEXT_NAMES = {"README", "LICENSE", "Makefile", "allowlist.txt"}

# 分节标题（Markdown 一级/二级标题）——节级豁免的边界
SECTION_HEADING_RE = re.compile(r"^\s*##\s")

# R9：盘符路径。URL（https://、res://、user://）因盘符字母前还有字母而被
# 负向后行断言排除；转义形式 "D:\\x" 由第一个反斜杠命中。
ABS_PATH_RE = re.compile(r"(?<![A-Za-z0-9])[A-Za-z]:[\\/]")

# T1：残留标记（仅注释行生效，避免 Godot placeholder_text 等合法 API）
T1_MARKER_RE = re.compile(
    r"\bTODO\b|\bFIXME\b|\bHACK\b|\bXXX\b|\bWIP\b|\bstub\b|\bplaceholder\b"
    r"|占位|待补|暂不实现|伪实现|假成功",
    re.IGNORECASE,
)

# R5：密钥模式
SK_KEY_RE = re.compile(r"sk-[A-Za-z0-9]{20,}")
KEY_LITERAL_RE = re.compile(
    r"(?i)\b(?:api[_-]?key|apikey|secret|token|password)\b\s*[:=]\s*[\"']([^\"']*)[\"']"
)
KEY_PLACEHOLDER_HINTS = (
    "<", ">", "${", "your", "xxx", "placeholder", "占位", "example",
    "changeme", "dummy", "sample", "test",
)
KEY_MIN_LITERAL_LEN = 8  # 短于 8 字符的字面量视为弱样例而非密钥

# T4：恒真/空断言行级模式
TAUTOLOGY_LINE_PATTERNS = (
    (re.compile(r"\bAssert\.True\s*\(\s*true\s*\)"), "恒真断言 Assert.True(true)"),
    (re.compile(r"\bAssert\.True\s*\(\s*\d+\s*==\s*\d+\s*\)"), "字面恒等式断言"),
    (re.compile(r"\bAssert\.False\s*\(\s*false\s*\)"), "恒假转真断言 Assert.False(false)"),
    (re.compile(r"\bassert\s+True\b(?!\w)"), "恒真断言 assert True"),
    (re.compile(r"\bAssert\.(?:Equal|Same|StrictEqual)\s*\(\s*([^,()]+?)\s*,\s*\1\s*[,)]"),
     "两参同表达式断言（自反断言）"),
    (re.compile(r"catch\s*(?:\([^)]*\))?\s*\{\s*\}"), "空 catch 吞异常"),
)

# 测试方法识别（T3/T4 方法级检查）
TEST_ATTR_RE = re.compile(r"^\s*\[\s*(?:Fact|Theory)\b")
METHOD_SIG_RE = re.compile(r"^\s*(?:public|internal)\s+(?:async\s+)?[\w<>\[\],\s]+\s(\w+)\s*\(")
HAS_ASSERT_RE = re.compile(r"\bAssert\.|\bassert\s|\.Should\(")

# 类声明行（仅代码行；注释行在 _class_declarations 中过滤）
CLASS_DECL_RE = re.compile(r"^\s*(?:\w+\s+)*?class\s+\w+")

# T3：F-ID/C-ID 关联注释 `// [F-123]`（或 [C-30]；亦接受 Trait 声明）。
# 方法级标签优先生效；所在测试类的声明行（含其上方 3 行内）带标签时，
# 该类全部测试方法视为已关联（06 §3.1 T3 允许测试类级关联）。
FID_COMMENT_RE = re.compile(r"\[\s*[FC]-\d{1,3}\s*\]")
FID_TRAIT_RE = re.compile(r"Trait\s*\(\s*\"[^\"]*\"\s*,\s*\"[FC]-\d{1,3}\"")

CI_ALLOW_RE = re.compile(r"ci-allow:\s*([A-Za-z0-9*,\s\-]+)")


# ---------------------------------------------------------------- 数据结构

@dataclass
class Finding:
    """单条违规。severity: FAIL（阻塞）或 WARN（仅报告）。"""
    check: str
    path: str
    line: int
    severity: str
    message: str


@dataclass
class CheckMeta:
    check_id: str
    name: str
    default_severity: str  # 该检查发现项的缺省严重级别


@dataclass
class Report:
    root: str
    baseline: str
    file_source: str
    checks: list = field(default_factory=list)      # [{id,name,status,fail,warn}]
    findings: list = field(default_factory=list)     # [Finding]

    @property
    def exit_code(self) -> int:
        return 1 if any(f.severity == "FAIL" for f in self.findings) else 0

    def json_dict(self) -> dict:
        return {
            "root": self.root,
            "baseline": self.baseline,
            "file_source": self.file_source,
            "checks": self.checks,
            "counts": {
                "checks": len(self.checks),
                "fail_checks": sum(1 for c in self.checks if c["status"] == "FAIL"),
                "warn_checks": sum(1 for c in self.checks if c["status"] == "WARN"),
                "findings": len(self.findings),
                "fail_findings": sum(1 for f in self.findings if f.severity == "FAIL"),
                "warn_findings": sum(1 for f in self.findings if f.severity == "WARN"),
            },
            "findings": [
                {"check": f.check, "path": f.path, "line": f.line,
                 "severity": f.severity, "message": f.message}
                for f in self.findings
            ],
            "exit_code": self.exit_code,
        }


# ---------------------------------------------------------------- 文件收集

def _git(root: str, *args: str):
    return subprocess.run(
        ["git", "-C", root, *args], capture_output=True, timeout=60)


def collect_files(root: str):
    """收集待扫文本文件（仓库相对路径，'/' 分隔）。

    优先 git 清单（tracked ∪ untracked-not-ignored），与 CI 检出态一致并
    自动排除 gitignored 载荷（.godot/obj/bin/编辑器缓存等）；git 不可用时
    回退为文件系统遍历（仅剪掉 .git）。
    """
    try:
        r1 = _git(root, "ls-files", "-z")
        r2 = _git(root, "status", "--porcelain", "-z", "--untracked-files=all")
        if r1.returncode == 0 and r2.returncode == 0:
            files = set(p for p in r1.stdout.decode("utf-8", "replace").split("\0") if p)
            for entry in r2.stdout.decode("utf-8", "replace").split("\0"):
                if not entry:
                    continue
                if entry.startswith("?? "):
                    files.add(entry[3:])
            return _filter_text(root, files), "git"
    except (OSError, subprocess.TimeoutExpired):
        pass
    found = set()
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d != ".git"]
        for fn in filenames:
            rel = os.path.relpath(os.path.join(dirpath, fn), root)
            found.add(rel.replace(os.sep, "/"))
    return _filter_text(root, found), "filesystem"


def _filter_text(root: str, rel_files):
    out = []
    for rel in sorted(rel_files):
        base = os.path.basename(rel)
        if os.path.splitext(base)[1].lower() in TEXT_EXTS or base in TEXT_NAMES:
            if os.path.isfile(os.path.join(root, rel.replace("/", os.sep))):
                out.append(rel)
    return out


# ---------------------------------------------------------------- 豁免机制

@dataclass
class AllowEntry:
    path: str            # 仓库相对路径或目录前缀
    marker: str | None   # 节标题标记：仅豁免 [标记行, 下一节标题) 内的行
    checks: set | None   # None=全部检查；否则 {'T2'} 等限定
    reason: str


def load_allowlist(path: str):
    entries = []
    if not path or not os.path.isfile(path):
        return entries
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for raw in fh:
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            reason = ""
            if "|" in line:
                line, reason = line.split("|", 1)
                reason = reason.strip()
            line = line.strip()
            checks = None
            m = re.match(r"^([RT]\d+)\s*:\s*(.+)$", line)
            if m:
                checks = {m.group(1)}
                line = m.group(2).strip()
            marker = None
            if "::" in line:
                line, marker = line.split("::", 1)
                line, marker = line.strip(), marker.strip()
            if line:
                entries.append(AllowEntry(line, marker, checks, reason))
    return entries


class Exemptions:
    """路径级/节级豁免判定（行级 ci-allow 见 line_allows）。"""

    def __init__(self, root: str, entries):
        self.root = root
        self.entries = entries
        self._section_cache = {}

    def _match_entries(self, rel: str, check_id: str):
        for e in self.entries:
            if e.checks is not None and check_id not in e.checks:
                continue
            if rel == e.path or rel.startswith(e.path.rstrip("/") + "/"):
                yield e

    def file_exempt(self, rel: str, check_id: str) -> bool:
        return any(e.marker is None for e in self._match_entries(rel, check_id))

    def _section_range(self, rel: str, entry: AllowEntry):
        key = (rel, entry.marker)
        if key in self._section_cache:
            return self._section_cache[key]
        rng = (0, 0)
        path = os.path.join(self.root, rel.replace("/", os.sep))
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as fh:
                lines = fh.readlines()
        except OSError:
            lines = []
        start = None
        for i, ln in enumerate(lines):
            if start is None:
                if entry.marker and entry.marker in ln:
                    start = i
            elif SECTION_HEADING_RE.match(ln):
                rng = (start, i - 1)
                break
        else:
            if start is not None:
                rng = (start, len(lines) - 1)
        self._section_cache[key] = rng
        return rng

    def line_exempt(self, rel: str, line_no: int, check_id: str) -> bool:
        """line_no 从 1 计。节级豁免：标记行起至下一 '## ' 标题前。"""
        for e in self._match_entries(rel, check_id):
            if e.marker is None:
                return True
            lo, hi = self._section_range(rel, e)
            if lo <= line_no - 1 <= hi:
                return True
        return False


def _allow_tokens(text: str):
    m = CI_ALLOW_RE.search(text)
    if not m:
        return []
    return [t for t in re.split(r"[\s,]+", m.group(1)) if t]


def line_allows(lines, line_no: int, check_id: str) -> bool:
    """行级豁免（1 基行号）：
    - 命中行本行含 `ci-allow: <ID列表>`（行内尾注或注释行均可）；
    - 或其上一行是『纯注释行』且含 ci-allow 标记（经典独立注释行形态）。
    上一行必须是纯注释行（//、#、<!--、*、; 起头），避免某行行内尾注
    顺带豁免下一行。"""
    idx = line_no - 1
    if 0 <= idx < len(lines):
        tokens = _allow_tokens(lines[idx])
        if "*" in tokens or check_id in tokens:
            return True
        prev = idx - 1
        if 0 <= prev < len(lines):
            tokens = _allow_tokens(lines[prev])
            if ("*" in tokens or check_id in tokens):
                stripped = lines[prev].lstrip()
                if stripped.startswith(("//", "#", "<!--", "*", ";")):
                    return True
    return False


# ---------------------------------------------------------------- 公共上下文

class ScanContext:
    def __init__(self, root: str, files, file_source: str, exemptions: Exemptions,
                 baseline: str):
        self.root = root
        self.files = files
        self.file_source = file_source
        self.exemptions = exemptions
        self.baseline = baseline
        self._cache = {}

    def read(self, rel: str):
        if rel not in self._cache:
            path = os.path.join(self.root, rel.replace("/", os.sep))
            try:
                with open(path, "r", encoding="utf-8", errors="replace") as fh:
                    self._cache[rel] = fh.read().splitlines()
            except OSError:
                self._cache[rel] = []
        return self._cache[rel]

    def under(self, rel: str, prefix: str) -> bool:
        return rel == prefix or rel.startswith(prefix + "/")

    def suppressed(self, rel: str, line_no: int, lines, check_id: str) -> bool:
        if self.exemptions.file_exempt(rel, check_id):
            return True
        if self.exemptions.line_exempt(rel, line_no, check_id):
            return True
        return line_allows(lines, line_no, check_id)


# ---------------------------------------------------------------- 各检查

def check_r9_abs_paths(ctx: ScanContext):
    """① 绝对/盘符路径（RL9）。范围：仓库文本文件，扣除 tools/、config/、
    game/data/ 与 allowlist 登记项。"""
    out = []
    for rel in ctx.files:
        if (ctx.under(rel, "tools") or ctx.under(rel, "config")
                or ctx.under(rel, "game/data")):
            continue
        lines = ctx.read(rel)
        for i, ln in enumerate(lines, 1):
            m = ABS_PATH_RE.search(ln)
            if m and not ctx.suppressed(rel, i, lines, "R9"):
                out.append(Finding(
                    "R9", rel, i, "FAIL",
                    f"本机绝对/盘符路径 '{m.group(0)}…'（RL9：代码与配置禁绝对路径；"
                    f"资源用 res://、可写数据经 IFileStore 注入根；"
                    f"环境事实只能登记于 docs/engineering/07-decided-plan.md §三（附））"))
    return out


def _is_comment_line(ln: str, ext: str) -> bool:
    s = ln.lstrip()
    if ext == ".gd":
        return s.startswith("#")
    return s.startswith("//") or s.startswith("/*") or s.startswith("*")


def check_t1_markers(ctx: ScanContext):
    """② 残留标记（TODO/FIXME/HACK/stub/placeholder/假成功等；仅注释行）。
    范围：game/src 与 game/presentation 源码。"""
    out = []
    for rel in ctx.files:
        ext = os.path.splitext(rel)[1].lower()
        if ext not in (".cs", ".gd"):
            continue
        if not (ctx.under(rel, "game/src") or ctx.under(rel, "game/presentation")):
            continue
        lines = ctx.read(rel)
        for i, ln in enumerate(lines, 1):
            if _is_comment_line(ln, ext):
                m = T1_MARKER_RE.search(ln)
                if m and not ctx.suppressed(rel, i, lines, "T1"):
                    out.append(Finding(
                        "T1", rel, i, "FAIL",
                        f"注释残留标记 '{m.group(0)}'（伪代码/占位防造假；"
                        f"交付代码禁 TODO/FIXME/HACK/stub/占位/假成功类标记）"))
    return out


def _test_filenames(ctx: ScanContext):
    return [os.path.basename(f) for f in ctx.files
            if ctx.under(f, "game/src/EraCore.Tests") and f.endswith(".cs")]


def check_t2_src_test_map(ctx: ScanContext):
    """③ src↔test 映射：game/src/EraCore 每个源文件须有对应 *Tests 文件
    （<Stem>Tests.cs 或 <Stem><Suffix>Tests.cs），或在 allowlist 以 `T2:` 条目
    声明为纯契约/纯数据文件。"""
    out = []
    test_names = _test_filenames(ctx)
    for rel in ctx.files:
        if not ctx.under(rel, "game/src/EraCore") or not rel.endswith(".cs"):
            continue
        if ctx.under(rel, "game/src/EraCore.Tests"):
            continue
        stem = os.path.splitext(os.path.basename(rel))[0]
        pat = re.compile(
            r"^(?:" + re.escape(stem) + r"Tests(?:\..+)?\.cs"
            r"|" + re.escape(stem) + r"[A-Z_][A-Za-z0-9_]*Tests\.cs)$")
        if any(pat.match(t) for t in test_names):
            continue
        lines = ctx.read(rel)
        if not ctx.suppressed(rel, 1, lines, "T2"):
            out.append(Finding(
                "T2", rel, 1, "FAIL",
                f"源文件无对应测试文件（找不到 {stem}Tests.cs / {stem}*Tests.cs；"
                f"纯契约/纯数据文件须在 tools/ci/allowlist.txt 以 'T2:' 条目声明）"))
    return out


def iter_test_methods(lines):
    """产出 (方法名, 起始行idx, 结束行idx)——[Fact]/[Theory] 属性起至方法体
    收花括号止（含属性区，供 T3 关联注释扫描）。"""
    n = len(lines)
    i = 0
    while i < n:
        if TEST_ATTR_RE.match(lines[i]):
            j, name = i, None
            while j < n:
                m = METHOD_SIG_RE.match(lines[j])
                if m:
                    name = m.group(1)
                if "{" in lines[j]:
                    break
                j += 1
            if name is None or j >= n:
                i += 1
                continue
            depth, started, k = 0, False, j
            while k < n:
                depth += lines[k].count("{") - lines[k].count("}")
                if "{" in lines[k]:
                    started = True
                if started and depth <= 0:
                    break
                k += 1
            yield name, i, min(k, n - 1)
            i = k + 1
        else:
            i += 1


def check_t4_tautology(ctx: ScanContext):
    """④ 恒真/空断言（T4 启发式：Assert.True(true)、字面恒等式、Equal 两参
    同表达式、空 catch、无断言测试方法）。范围：game/src/EraCore.Tests。"""
    out = []
    for rel in ctx.files:
        if not (ctx.under(rel, "game/src/EraCore.Tests") and rel.endswith(".cs")):
            continue
        lines = ctx.read(rel)
        for i, ln in enumerate(lines, 1):
            for pat, msg in TAUTOLOGY_LINE_PATTERNS:
                if pat.search(ln) and not ctx.suppressed(rel, i, lines, "T4"):
                    out.append(Finding("T4", rel, i, "FAIL", msg))
                    break
        for name, lo, hi in iter_test_methods(lines):
            body = "\n".join(lines[lo:hi + 1])
            if not HAS_ASSERT_RE.search(body):
                if not ctx.suppressed(rel, lo + 1, lines, "T4"):
                    out.append(Finding(
                        "T4", rel, lo + 1, "FAIL",
                        f"测试方法 {name} 体内无任何断言（'不抛异常即过'不计为覆盖）"))
    return out


def check_r5_secrets(ctx: ScanContext):
    """⑤ 密钥模式（RL5 部分）：sk- 前缀长串与 api_key/secret/token/password
    非空非占位字面量。范围：仓库文本文件，扣除 config/（本地密钥存放处）、
    docs/（占位示例）与 tools/（扫描器自身样例）。"""
    out = []
    for rel in ctx.files:
        if (ctx.under(rel, "config") or ctx.under(rel, "docs")
                or ctx.under(rel, "tools")):
            continue
        lines = ctx.read(rel)
        for i, ln in enumerate(lines, 1):
            hits = []
            m = SK_KEY_RE.search(ln)
            if m:
                hits.append(f"疑似密钥 '{m.group(0)[:12]}…'")
            for lm in KEY_LITERAL_RE.finditer(ln):
                value = lm.group(1)
                if (len(value) >= KEY_MIN_LITERAL_LEN
                        and not any(h in value.lower() for h in KEY_PLACEHOLDER_HINTS)):
                    hits.append(f"密钥字段非空字面量 '{lm.group(0)[:30]}…'")
            if hits and not ctx.suppressed(rel, i, lines, "R5"):
                out.append(Finding(
                    "R5", rel, i, "FAIL",
                    "; ".join(hits) + "（RL5：密钥只能经 config/secrets.local.json"
                    "（gitignored）或环境变量；永不入代码/日志/战报/cassette）"))
    return out


def check_r13_baseline(ctx: ScanContext):
    """⑥ git 基线可达性（RL13）。"""
    try:
        r = _git(ctx.root, "cat-file", "-e", f"{ctx.baseline}^{{commit}}")
    except (OSError, subprocess.TimeoutExpired) as exc:
        return [Finding("R13", "-", 0, "FAIL", f"git 不可用，无法核验基线：{exc}")]
    if r.returncode != 0:
        return [Finding(
            "R13", "-", 0, "FAIL",
            f"git 基线 {ctx.baseline} 不可达（RL13：基线不可达时禁止实现任何"
            f"'延续 v2.1'机制，须先恢复仓库或按新规格重写并过 review）")]
    return []


def _tagged_class_lines(lines):
    """返回带 F-ID/C-ID 标签的类声明行号集合（0 基）。
    标签可在声明行本行（行内尾注）或其上方 3 行内的注释中。"""
    tagged = set()
    for i, ln in enumerate(lines):
        stripped = ln.lstrip()
        if (not CLASS_DECL_RE.match(ln)
                or stripped.startswith(("//", "*", "/*", "#")) or ";" in ln):
            continue
        window = lines[max(0, i - 3): i + 1]
        if any(FID_COMMENT_RE.search(w) for w in window):
            tagged.add(i)
    return tagged


def _class_declarations(lines):
    """返回按行序的 [(声明行号 0基, 是否带标签)]。"""
    decls = []
    tagged = _tagged_class_lines(lines)
    for i, ln in enumerate(lines):
        stripped = ln.lstrip()
        if (CLASS_DECL_RE.match(ln) and not stripped.startswith(("//", "*", "/*", "#"))
                and ";" not in ln):
            decls.append((i, i in tagged))
    return decls


def _owner_class_tagged(class_decls, line_idx: int) -> bool:
    """line_idx 属于声明于其前的最后一个类；返回该类是否带标签。"""
    owner_tagged = False
    found = False
    for c, is_tagged in class_decls:
        if c <= line_idx:
            owner_tagged = is_tagged
            found = True
        else:
            break
    return found and owner_tagged


def check_t3_fid_coverage(ctx: ScanContext):
    """⑦ 测试方法 F-ID/C-ID 关联覆盖率（WARNING 级报告，不阻塞）。
    认可格式：方法属性区/体内的 `// [F-123]`、`// [C-30]` 注释，
    [Trait("Feature", "F-123")] 声明，或所在测试类声明处的标签
    （类级关联，06 §3.1 T3）。"""
    out = []
    total = tagged = 0
    for rel in ctx.files:
        if not (ctx.under(rel, "game/src/EraCore.Tests") and rel.endswith(".cs")):
            continue
        lines = ctx.read(rel)
        class_decls = _class_declarations(lines)
        for name, lo, hi in iter_test_methods(lines):
            total += 1
            span = "\n".join(lines[lo:hi + 1])
            if (FID_COMMENT_RE.search(span) or FID_TRAIT_RE.search(span)
                    or _owner_class_tagged(class_decls, lo)):
                tagged += 1
                continue
            out.append(Finding(
                "T3", rel, lo + 1, "WARN",
                f"测试方法 {name} 缺少 F-ID/C-ID 关联注释（格式：`// [F-xxx]`，"
                f"或在测试类声明处类级标注；见 docs/CODE_STANDARDS.md CS-3.2）"))
    ctx.t3_stats = (tagged, total)
    return out


CHECK_FUNCS = [
    ("R9", "绝对/盘符路径扫描（RL9）", check_r9_abs_paths),
    ("T1", "残留标记扫描（TODO/FIXME/stub/占位/假成功）", check_t1_markers),
    ("T2", "src↔test 文件映射", check_t2_src_test_map),
    ("T4", "恒真/空断言扫描", check_t4_tautology),
    ("R5", "密钥模式扫描（RL5）", check_r5_secrets),
    ("R13", "git 基线可达性（RL13）", check_r13_baseline),
    ("T3", "测试 F-ID 关联覆盖率（WARNING 级）", check_t3_fid_coverage),
]


# ---------------------------------------------------------------- 主流程

def run_scan(root: str, baseline: str = DEFAULT_BASELINE,
             allowlist_path: str | None = None, only: set | None = None) -> Report:
    root = os.path.abspath(root)
    if allowlist_path is None:
        allowlist_path = os.path.join(root, "tools", "ci", "allowlist.txt")
    files, file_source = collect_files(root)
    exemptions = Exemptions(root, load_allowlist(allowlist_path))
    ctx = ScanContext(root, files, file_source, exemptions, baseline)

    report = Report(root=root, baseline=baseline, file_source=file_source)
    for check_id, name, fn in CHECK_FUNCS:
        if only and check_id not in only:
            continue
        findings = fn(ctx)
        fail = sum(1 for f in findings if f.severity == "FAIL")
        warn = sum(1 for f in findings if f.severity == "WARN")
        status = "FAIL" if fail else ("WARN" if warn else "PASS")
        note = ""
        if check_id == "T3" and getattr(ctx, "t3_stats", None):
            tagged, total = ctx.t3_stats
            pct = f"{100.0 * tagged / total:.0f}%" if total else "n/a"
            note = f"{tagged}/{total} 方法已关联 ({pct})"
        report.checks.append(
            {"id": check_id, "name": name, "status": status,
             "fail": fail, "warn": warn, "note": note})
        report.findings.extend(findings)
    report.findings.sort(key=lambda f: (f.check, f.path.lower(), f.line))
    return report


def format_report(report: Report) -> str:
    width = max(len(c["id"]) for c in report.checks) if report.checks else 4
    lines = [
        "=" * 72,
        f"redline_scan — CI 红线扫描（T-027；06 §3.1 首批 7 条）",
        f"root: {report.root}   baseline: {report.baseline}   "
        f"files: {report.file_source}",
        "=" * 72,
    ]
    for c in report.checks:
        count = c["fail"] + c["warn"]
        suffix = f" ({count})" if count else ""
        note = f"  {c['note']}" if c.get("note") else ""
        lines.append(f"[{c['status']:>4}] {c['id']:<{width}}  {c['name']}{suffix}{note}")
    if report.findings:
        lines.append("-" * 72)
        for f in report.findings:
            loc = f"{f.path}:{f.line}" if f.path != "-" else "-"
            lines.append(f"{f.severity} [{f.check}] {loc}  {f.message}")
    fail_total = sum(1 for f in report.findings if f.severity == "FAIL")
    warn_total = sum(1 for f in report.findings if f.severity == "WARN")
    lines.append("-" * 72)
    lines.append(
        f"summary: {len(report.checks)} checks | "
        f"{fail_total} FAIL | {warn_total} WARN | "
        f"exit_code={report.exit_code}")
    return "\n".join(lines)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="EraTD CI 红线扫描器（T-027）")
    default_root = os.path.dirname(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__))))
    parser.add_argument("--root", default=default_root, help="仓库根目录")
    parser.add_argument("--baseline", default=DEFAULT_BASELINE,
                        help=f"git 基线 commit（默认 {DEFAULT_BASELINE}）")
    parser.add_argument("--allowlist", default=None,
                        help="allowlist 路径（默认 <root>/tools/ci/allowlist.txt）")
    parser.add_argument("--json", default=None, help="机器可读 JSON 报告输出路径")
    parser.add_argument("--checks", default=None,
                        help="只跑指定检查（逗号分隔，如 R9,T2）")
    args = parser.parse_args(argv)

    try:
        only = {c.strip() for c in args.checks.split(",")} if args.checks else None
        report = run_scan(args.root, args.baseline, args.allowlist, only)
    except Exception as exc:  # 扫描器自身故障按运行错误处理
        print(f"redline_scan: 运行错误：{exc}", file=sys.stderr)
        return 2

    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass
    print(format_report(report))
    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(report.json_dict(), fh, ensure_ascii=False, indent=2)
        print(f"json report: {args.json}")
    return report.exit_code


if __name__ == "__main__":
    sys.exit(main())
