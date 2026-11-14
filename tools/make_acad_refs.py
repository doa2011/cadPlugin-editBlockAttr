# -*- coding: utf-8 -*-
"""
生成多版本编译所需的「编译期引用副本」。

说明
----
本机只有 AutoCAD 2010 (18.0.0.0 / CLR2) 与 AutoCAD 2024 (24.3.0.0 / CLR4,
TargetFrameworkAttribute = .NETFramework v4.7) 两套托管 DLL。MSBuild 的
ResolveAssemblyReference 会拒绝「目标框架高于当前工程」的引用（MSB3274），
导致用 2024 的 DLL 编译 net40 / net45 目标时引用被丢弃、报 CS0246。

这里复制 2024 的三个 DLL 作为「编译期引用副本」，只把其中的
TargetFrameworkAttribute 字符串从 v4.7 等长替换为 v4.0（程序集版本保持
24.3.0.0，内部 AssemblyRef 也保持自洽）。这些副本仅供编译器读取元数据，
绝不参与部署；运行时 AutoCAD 加载的是自己安装目录里的真实 DLL。

编译完成后由 retarget_assembly_refs.py 把产物的 AssemblyRef 版本号定向到
各个 AutoCAD 年份对应的程序集版本。
"""
import os
import sys
import shutil

CAD2024 = r'D:\Program Files\Autodesk\AutoCAD 2024'
NAMES = ('acmgd.dll', 'acdbmgd.dll', 'accoremgd.dll')

OLD_TF = b'.NETFramework,Version=v4.7'
NEW_TF = b'.NETFramework,Version=v4.0'


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_dir = os.path.join(root, 'build', 'refs', 'clr4')
    if not os.path.isdir(CAD2024):
        print('找不到 AutoCAD 2024 安装目录:', CAD2024)
        return 1
    if len(OLD_TF) != len(NEW_TF):
        print('等长替换前提不成立')
        return 1

    if not os.path.isdir(out_dir):
        os.makedirs(out_dir)

    for name in NAMES:
        src = os.path.join(CAD2024, name)
        dst = os.path.join(out_dir, name)
        if not os.path.isfile(src):
            print('缺失:', src)
            return 1
        data = bytearray(open(src, 'rb').read())
        count = data.count(OLD_TF)
        data = bytearray(bytes(data).replace(OLD_TF, NEW_TF))
        open(dst, 'wb').write(data)
        print('%-16s %9d B  TargetFramework 替换 %d 处 -> %s'
              % (name, len(data), count, dst))

    print('\n完成。编译期引用副本目录:', out_dir)
    return 0


if __name__ == '__main__':
    sys.exit(main())
