#!/usr/bin/env python3
"""Version helper for AutoPalExplorer.

Subcommands:
  next   Print the next version (max(csproj, Pal.json) with the revision bumped).
  set    Write a given version into the .csproj <Version> and the matching
         entry's AssemblyVersion in Pal.json (surgical edit, other formatting
         is preserved).

Only the last (revision) component is auto-incremented. To change the major /
minor / build parts, pass an explicit version, e.g. `set 1.1.0.0`.
"""
import argparse
import os
import re
import sys

# Objects in Pal.json contain no nested "{}" (arrays use "[]"), so a single
# object can be matched with this pattern.
OBJ_RE = r"\{[^{}]*\}"


def read_text(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def write_text(path, txt):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(txt)


def read_csproj_version(path):
    if not path or not os.path.isfile(path):
        return None
    m = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", read_text(path))
    return m.group(1) if m else None


def read_pal_version(path, internal):
    if not path or not os.path.isfile(path):
        return None
    txt = read_text(path)
    for obj in re.findall(OBJ_RE, txt):
        if re.search(r'"InternalName"\s*:\s*"%s"' % re.escape(internal), obj):
            m = re.search(r'"AssemblyVersion"\s*:\s*"([^"]*)"', obj)
            return m.group(1) if m else None
    return None


def parse_ver(v):
    parts = []
    for x in v.split("."):
        try:
            parts.append(int(x))
        except ValueError:
            parts.append(0)
    while len(parts) < 4:
        parts.append(0)
    return parts[:4]


def ver_key(v):
    return tuple(parse_ver(v))


def bump_revision(v):
    p = parse_ver(v)
    p[3] += 1
    return ".".join(map(str, p))


def cmd_next(args):
    cands = [c for c in (read_csproj_version(args.csproj),
                         read_pal_version(args.pal, args.internal_name)) if c]
    base = max(cands, key=ver_key) if cands else "1.0.0.0"
    print(bump_revision(base))


def write_csproj(path, version):
    txt = read_text(path)
    if re.search(r"<Version>\s*[^<]*</Version>", txt):
        new = re.sub(r"<Version>\s*[^<]*</Version>",
                     "<Version>%s</Version>" % version, txt, count=1)
    else:
        # Insert into the first <PropertyGroup>.
        new = re.sub(r"(<PropertyGroup>)",
                     r"\1\n    <Version>%s</Version>" % version, txt, count=1)
    if new != txt:
        write_text(path, new)


def write_pal(path, version, internal):
    if not path or not os.path.isfile(path):
        sys.stderr.write("warning: Pal.json not found at %s, skipping\n" % path)
        return
    txt = read_text(path)
    hit = [False]

    def repl(m):
        obj = m.group(0)
        if re.search(r'"InternalName"\s*:\s*"%s"' % re.escape(internal), obj):
            obj = re.sub(r'("AssemblyVersion"\s*:\s*")[^"]*(")',
                         r"\g<1>%s\g<2>" % version, obj, count=1)
            hit[0] = True
        return obj

    new = re.sub(OBJ_RE, repl, txt)
    if not hit[0]:
        sys.stderr.write(
            "warning: no entry with InternalName '%s' in %s\n" % (internal, path))
        return
    if new != txt:
        write_text(path, new)


def cmd_set(args):
    write_csproj(args.csproj, args.version)
    write_pal(args.pal, args.version, args.internal_name)


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--csproj", required=True)
    p.add_argument("--pal", default=None)
    p.add_argument("--internal-name", default="AutoPalExplorer")
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("next")
    s = sub.add_parser("set")
    s.add_argument("version")
    args = p.parse_args()
    if args.cmd == "next":
        cmd_next(args)
    else:
        cmd_set(args)


if __name__ == "__main__":
    main()
