# Third-party code and references

- **LZFSE / LZVN decoder** (`SweetEmu.Core/Compression/Lzfse.cs`): a C# port of
  Apple's reference implementation, <https://github.com/lzfse/lzfse>.
  Copyright (c) 2015-2016, Apple Inc. BSD 3-Clause License:

  > Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
  > 1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
  > 2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
  > 3. Neither the name of the copyright holder(s) nor the names of any contributors may be used to endorse or promote products derived from this software without specific prior written permission.
  >
  > THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

- **LZBITMAP** (`Lzbitmap.cs`): format as documented by libzbitmap
  (<https://github.com/eafer/libzbitmap>) and implemented in apfs-fuse and
  dissect.util; test vectors from dissect.util.
- **APFS, decmpfs, UDIF, HFS+**: written from Apple's *Apple File System
  Reference*, TN1150 and `hfs_format.h` (apple-oss-distributions/hfs), with
  apfs-fuse and dissect.apfs used as references and for verification. The HFS+
  hard-link and folder-count rules follow Apple's fsck_hfs.
- **QEMU** (bundled by the app workflow): GPL-2.0, <https://www.qemu.org>.
