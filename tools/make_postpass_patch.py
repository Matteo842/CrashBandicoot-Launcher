import difflib
import pathlib
import re
import sys

gate = pathlib.Path(r"D:\GitHub\RecompOne\_gate_regen\main.cs")
cur = pathlib.Path(r"D:\GitHub\RecompOne\CrashBandicoot.Recompiled\main.cs")
out = pathlib.Path(r"D:\GitHub\CrashBandicoot-Launcher\CrashBandicoot.Launcher\Recomp\Patches\main.cs.patch")

def read_lf(p: pathlib.Path) -> str:
    return p.read_text(encoding="utf-8").replace("\r\n", "\n").replace("\r", "\n")

a = read_lf(gate).splitlines(keepends=True)
b = read_lf(cur).splitlines(keepends=True)
diff = list(difflib.unified_diff(a, b, fromfile="main.cs", tofile="main.cs", n=3))
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text("".join(diff), encoding="utf-8", newline="\n")
print(f"wrote {out} ({out.stat().st_size} bytes, {len(diff)} lines)")


def apply_unified(original: str, patch: str) -> str:
    src = original.splitlines(keepends=True)
    lines = patch.splitlines(keepends=True)
    out_lines: list[str] = []
    i = 0
    idx = 0
    while idx < len(lines):
        line = lines[idx]
        if line.startswith("---") or line.startswith("+++") or line.startswith("diff"):
            idx += 1
            continue
        m = re.match(r"@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", line)
        if not m:
            idx += 1
            continue
        old_start = int(m.group(1)) - 1
        while i < old_start:
            out_lines.append(src[i])
            i += 1
        idx += 1
        while idx < len(lines) and not lines[idx].startswith("@@") and not (
            lines[idx].startswith("diff")
        ):
            pl = lines[idx]
            if pl.startswith("\\"):
                idx += 1
                continue
            tag, content = pl[0], pl[1:]
            if not content.endswith("\n"):
                content += "\n"
            if tag == " ":
                if src[i].rstrip("\n\r") != content.rstrip("\n\r"):
                    raise SystemExit(
                        f"context mismatch at {i+1}:\n expected {content!r}\n got {src[i]!r}"
                    )
                out_lines.append(src[i])
                i += 1
            elif tag == "-":
                if src[i].rstrip("\n\r") != content.rstrip("\n\r"):
                    raise SystemExit(f"delete mismatch at {i+1}")
                i += 1
            elif tag == "+":
                out_lines.append(content)
            else:
                raise SystemExit(f"bad tag {tag!r}")
            idx += 1
    while i < len(src):
        out_lines.append(src[i])
        i += 1
    return "".join(out_lines)


patched = apply_unified(read_lf(gate), out.read_text(encoding="utf-8"))
expect = read_lf(cur)
print("match", patched == expect)
if patched != expect:
    for n, (x, y) in enumerate(zip(patched.splitlines(), expect.splitlines())):
        if x != y:
            print("first diff line", n + 1)
            print("patched:", x[:120])
            print("expect:", y[:120])
            break
    print("len", len(patched), len(expect))
    sys.exit(1)
print("post-pass patch OK")
