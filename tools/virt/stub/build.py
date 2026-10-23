#!/usr/bin/env python3
"""Rebuild ramfb_stub.bin (committed, so Windows users need no compiler).

Needs clang, ld.lld and llvm-objcopy.
"""
import os, subprocess, tempfile
HERE = os.path.dirname(os.path.abspath(__file__))
cflags = ['--target=aarch64-none-elf', '-ffreestanding', '-nostdlib', '-Os', '-mgeneral-regs-only',
          '-mstrict-align', '-fno-stack-protector', '-Wall', '-Wextra', '-Werror']
with tempfile.TemporaryDirectory() as t:
    objs = []
    for src in ('start.S', 'stub.c'):
        o = os.path.join(t, src + '.o')
        subprocess.check_call(['clang', *cflags, '-c', os.path.join(HERE, src), '-o', o])
        objs.append(o)
    elf = os.path.join(t, 'stub.elf')
    subprocess.check_call(['ld.lld', '-T', os.path.join(HERE, 'link.ld'), *objs, '-o', elf])
    out = os.path.join(HERE, 'ramfb_stub.bin')
    # .bss lives past the end of the file image; the loader area is plain RAM
    subprocess.check_call(['llvm-objcopy', '-O', 'binary', '--only-section=.text',
                           '--only-section=.params', '--only-section=.data', elf, out])
    syms = subprocess.check_output(['llvm-nm', elf], text=True)
    print(syms)
    print(out, os.path.getsize(out), 'bytes')
