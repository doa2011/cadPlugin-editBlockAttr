# -*- coding: utf-8 -*-
"""从 Autodesk 官方 AutoCAD.NET NuGet 包准备 CLR4 编译引用副本。"""
import os
import re
import sys

NAMES = ('acmgd.dll', 'acdbmgd.dll', 'accoremgd.dll')
PATTERN = re.compile(br'\.NETFramework,Version=v4\.[0-9]')


def main():
    if len(sys.argv) != 3:
        print('用法: prepare_nuget_refs.py <NuGet全局包目录> <输出目录>')
        return 2
    packages, out_dir = sys.argv[1], sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)
    for name in NAMES:
        matches = []
        for root, dirs, files in os.walk(packages):
            if name.lower() in [f.lower() for f in files]:
                matches.append(os.path.join(root, name))
        if not matches:
            print('NuGet 缓存中找不到:', name)
            return 1
        # NuGet package version folder is part of path; prefer exact 24.3.0 package.
        matches.sort(key=lambda p: ('autocad.net' not in p.lower(),
                                    '24.3.0' not in p,
                                    len(p)))
        src = matches[0]
        with open(src, 'rb') as f:
            data = f.read()
        patched, count = PATTERN.subn(b'.NETFramework,Version=v4.0', data)
        if count == 0:
            print('未找到可降级的 .NETFramework TargetFrameworkAttribute:', src)
            return 1
        dst = os.path.join(out_dir, name)
        with open(dst, 'wb') as f:
            f.write(patched)
        print('%s -> %s (替换 %d 处)' % (src, dst, count))
    return 0


if __name__ == '__main__':
    sys.exit(main())
