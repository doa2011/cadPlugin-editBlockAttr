# -*- coding: utf-8 -*-
"""
按 6 种 .NET 目标框架编译基准程序集，再输出 AutoCAD 2010~2024 各平台专用 DLL。

原理
----
本机只有 AutoCAD 2010 (18.0.0.0 / CLR 2.0) 与 AutoCAD 2024
(24.3.0.0 / CLR 4.0) 两套托管接口 DLL。做法：

  1) 用 2010 的 DLL 编译 net35 基准（2010~2012 共用，CLR 2.0）；
  2) 用 2024 的 DLL 生成的「编译期引用副本」分别编译
     net40 / net45 / net46 / net472 / net48 基准（2013~2024 共用，CLR 4.0）；
  3) 把每个基准产物里 acmgd / acdbmgd / accoremgd 的 AssemblyRef 版本号
     改写为对应年份 AutoCAD 真实的程序集版本，输出成独立文件。

这样每个 DLL 的目标框架符合 Autodesk 官方的版本对应表，且程序集引用号
与目标 AutoCAD 精确匹配，不依赖 bindingRedirect 或 publisher policy。

用法：python tools\\build_all.py
"""
import os
import sys
import shutil
import subprocess
import argparse

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from acad_meta import parse, assembly_refs, retarget  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(ROOT, 'tools')
PROJ = os.path.join(TOOLS, 'MultiVersion.csproj')
SRC = os.path.join(ROOT, 'AutoCADBlockAttrPlugin')
REF_2010 = os.path.join(ROOT, 'lib')
REF_CLR4 = os.path.join(ROOT, 'build', 'refs', 'clr4')
BUILD_OUT = os.path.join(ROOT, 'build', 'out')
DIST = os.path.join(ROOT, 'dist_all')

# (年份, AutoCAD 托管程序集版本, .NET Framework 目标, 是否 2013+ 分支)
VERSIONS = [
    (2010, (18, 0, 0, 0), 'net35', False),
    (2011, (18, 1, 0, 0), 'net35', False),
    (2012, (18, 2, 0, 0), 'net35', False),
    (2013, (19, 0, 0, 0), 'net40', True),
    (2014, (19, 1, 0, 0), 'net40', True),
    (2015, (20, 0, 0, 0), 'net45', True),
    (2016, (20, 1, 0, 0), 'net45', True),
    (2017, (21, 0, 0, 0), 'net46', True),
    (2018, (22, 0, 0, 0), 'net46', True),
    (2019, (23, 0, 0, 0), 'net47', True),
    (2020, (23, 1, 0, 0), 'net47', True),
    (2021, (24, 0, 0, 0), 'net48', True),
    (2022, (24, 1, 0, 0), 'net48', True),
    (2023, (24, 2, 0, 0), 'net48', True),
    (2024, (24, 3, 0, 0), 'net48', True),
]


def step(msg):
    print('\n' + '=' * 66 + '\n' + msg + '\n' + '=' * 66)


def make_refs():
    step('1/4  生成 2024 托管 DLL 的编译期引用副本')
    rc = subprocess.call([sys.executable, os.path.join(TOOLS, 'make_acad_refs.py')])
    if rc != 0:
        raise RuntimeError('生成编译期引用副本失败')


def build_bases(selected_versions):
    step('2/4  按目标框架编译基准程序集（6 次）')
    frameworks = []
    for year, ver, fx, plus in selected_versions:
        if (fx, plus) not in frameworks:
            frameworks.append((fx, plus))

    bases = {}
    for fx, plus in frameworks:
        out = os.path.join(BUILD_OUT, 'base_' + fx)
        if os.path.isdir(out):
            shutil.rmtree(out)
        os.makedirs(out)
        ref = REF_CLR4 if plus else REF_2010
        cmd = [
            'dotnet', 'build', PROJ, '-c', 'Release', '-v:quiet', '--nologo',
            '--no-incremental',
            '-p:AcadNetFx=' + fx,
            '-p:AcadSrcDir=' + SRC,
            '-p:AcadRefDir=' + ref,
            '-p:AcadOutDir=' + out + '\\',
        ]
        if plus:
            cmd.append('-p:AcadDefines=ACAD_2013_PLUS')
        print('  编译 %-6s (refs=%s, 2013+=%s) ...' % (fx, os.path.basename(ref), plus))
        p = subprocess.call(cmd)
        dll = os.path.join(out, 'AutoCADBlockAttrPlugin.dll')
        if p != 0 or not os.path.isfile(dll):
            raise RuntimeError('基准编译失败: ' + fx)
        bases[(fx, plus)] = dll
        print('       -> %s (%d B)' % (dll, os.path.getsize(dll)))
    return bases


def produce(bases, selected_versions):
    step('3/4  定向程序集版本号并输出平台专用 DLL')
    # 不删除整个目录：旧 DLL 可能正被运行中的 AutoCAD 锁定，
    # 目录删除在某些环境下还会被安全策略拦截。直接逐个覆盖写即可。
    if not os.path.isdir(DIST):
        os.makedirs(DIST)
    made = []
    locked = []
    for year, ver, fx, plus in selected_versions:
        dst = os.path.join(DIST, 'AutoCADBlockAttrPlugin_AutoCAD%d_%s.dll' % (year, fx.upper()))
        try:
            refs = retarget(bases[(fx, plus)], dst, ver)
        except (IOError, OSError):
            # 文件被 acad.exe 占用时改用备用名，保证本轮产物始终可用。
            dst = os.path.join(DIST, 'AutoCADBlockAttrPlugin_AutoCAD%d_%s_new.dll' % (year, fx.upper()))
            refs = retarget(bases[(fx, plus)], dst, ver)
            locked.append(year)
        print('  %d  %-6s -> %-42s %d.%d.%d.%d  [%s]' % (
            year, fx, os.path.basename(dst), ver[0], ver[1], ver[2], ver[3],
            ', '.join(r['name'] for r in refs)))
        made.append((year, ver, fx, plus, dst))
    if locked:
        print('  [!] 原文件被 AutoCAD 占用，已改用 *_new.dll：%s'
              % ', '.join(str(y) for y in locked))
        print('      关闭 AutoCAD 后重跑 build_all.bat 即可覆盖原文件名。')
    return made


def verify(made):
    step('4/4  逐个校验产物')
    ok = True
    for year, ver, fx, plus, dst in made:
        m = parse(dst)
        refs = assembly_refs(m)
        data = open(dst, 'rb').read()
        want_rt = 'v2.0.50727' if fx == 'net35' else 'v4.0.30319'
        checks = [
            ('CLR 运行时', m['runtime'] == want_rt, m['runtime'] + ' / ' + want_rt),
            ('x64', m['machine'] == '0x8664', m['machine']),
            ('引用版本', len(refs) > 0 and all(r['version'] == ver for r in refs),
             ','.join('%d.%d.%d.%d' % r['version'] for r in refs)),
            # 命令名写在自定义特性的 SerString 里（UTF-8 / #Blob），
            # 界面中文文案写在用户字符串堆里（UTF-16LE / #US）。
            ('新命令名', b'EditBlockAtt' in data, 'EditBlockAtt'),
            ('旧命令名已移除', b'EditBlockAttr' not in data, ''),
            ('已预填默认值', '统一修改为'.encode('utf-16-le') in data, ''),
            ('无全局统一文字', '全局统一文字'.encode('utf-16-le') not in data, ''),
            # NETLOAD 加载钩子与命令行横幅
            ('加载钩子', b'IExtensionApplication' in data, ''),
            ('加载结果横幅', '加载结果：成功'.encode('utf-16-le') in data, ''),
            ('用法简介', '用法    ：'.encode('utf-16-le') in data, ''),
            ('帮助命令', b'EditBlockAttHelp' in data, ''),
            ('程序集版本 1.2', b'1.2.0.0' in data, ''),
            ('角标标签页', '角标修改'.encode('utf-16-le') in data, ''),
            ('XLSX导入导出', b'XlsxLite' in data, ''),
        ]
        bad = [n for n, good, _ in checks if not good]
        ok &= not bad
        print('  %d  %-6s %-12s %-8s %s' % (
            year, fx, want_rt, m['machine'],
            'OK' if not bad else 'FAIL: ' + ','.join(bad)))
    print('\n校验结果:', '全部通过' if ok else '存在失败项')
    return ok


def main():
    parser = argparse.ArgumentParser(description='编译指定 AutoCAD 版本或全部版本')
    parser.add_argument('--year', default='all',
                        help='AutoCAD 年份：2010~2024，或 all')
    args = parser.parse_args()

    if args.year.lower() == 'all':
        selected = VERSIONS
    else:
        try:
            year = int(args.year)
        except ValueError:
            parser.error('--year 必须是 all 或 2010~2024 的年份')
        selected = [v for v in VERSIONS if v[0] == year]
        if not selected:
            parser.error('不支持的 AutoCAD 年份: %s' % args.year)

    # GitHub Actions 会从 Autodesk 官方 NuGet 包准备 CLR4 编译引用；
    # 本地开发环境则仍通过 make_acad_refs.py 从 AutoCAD 2024 安装目录生成。
    needs_clr4 = any(v[3] for v in selected)
    ref_files = [os.path.join(REF_CLR4, n)
                 for n in ('acmgd.dll', 'acdbmgd.dll', 'accoremgd.dll')]
    if needs_clr4 and not all(os.path.isfile(p) for p in ref_files):
        make_refs()

    step('构建目标: %s' % (', '.join(str(v[0]) for v in selected)))
    bases = build_bases(selected)
    made = produce(bases, selected)
    good = verify(made)
    print('\n交付目录:', DIST)
    print('共 %d 个 AutoCAD 平台专用 DLL' % len(made))
    return 0 if good else 1


if __name__ == '__main__':
    sys.exit(main())
