# third-party notices

| Component | Credit and license |
| --- | --- |
| Ryujinx / Kenji-NX | [Ryujinx MIT notice](licenses/Ryujinx-Team-MIT.txt), [Kenji-NX MIT notice](licenses/Ryujinx-MIT.txt), and [component notices](licenses/Ryujinx-third-party.md) |
| Dune by sakasakiking | [MIT notice](licenses/Dune-MIT.txt) |
| Managed NuGet dependencies | Every package in both `.deps.json` graphs is covered by the generated package metadata, copyright attribution, declared license text, and available package notice files under the download's `licenses/packages` folder |
| Concentus | Native Linux archives retain the BSD-3-Clause notice from the SHA-256-pinned `Concentus` 2.2.2 package |
| SDL and GLFW | Exact native/package hashes, package metadata and Zlib notices are retained. SDL x64 embeds a source revision; the ARM64 binary reports its revision as unavailable, which the inventory records rather than guessing |
| FFmpeg | Exact package/native hashes, per-RID build configurations and source commits, LGPL-2.1-or-later text, and deterministic archives of the complete per-RID corresponding source are retained. The NuGet package metadata says LGPL-2.1-only; the binaries identify themselves as LGPL-2.1-or-later, and the inventory records that discrepancy |
| Shaderc | Exact package/native hashes, v2024.3 source/DEPS pins, package metadata, Apache-2.0 text, and pinned glslang, SPIR-V, LLVM, libc++, and libc++abi notices are retained |
| OpenAL Soft | Native Linux uses the system `libopenal.so.1` shared library under LGPL-2.0-or-later rather than redistributing another binary |
| TOTK Dynamic FPS | [GPL-2.0 license](third_party/totk-dfps/LICENSE), [source and changes](third_party/totk-dfps/NX-NOTICE.md) |
| Local translation and OCR dependencies | Notices included with an optional translation runtime; native Linux archives do not bundle that runtime or download OCR models |

The NX Infinity license does not replace these component licenses.
