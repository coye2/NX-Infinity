# licenses

Original NX Infinity code uses the [MIT license](LICENSE), unless a file says otherwise. Ryujinx retains its [MIT copyright and license](licenses/Ryujinx-Team-MIT.txt), Kenji-NX retains its [MIT copyright and license](licenses/Ryujinx-MIT.txt), and Dune retains its [original notice](licenses/Dune-MIT.txt).

Dependencies have their own licenses. The download includes their notices alongside the application. The separate TOTK Dynamic FPS module uses GPL-2.0; its corresponding source and build files are in [third_party/totk-dfps](third_party/totk-dfps/).

Native Linux archives include the complete modified TOTK module source, a
complete managed-package notice inventory, and a machine-readable native
dependency inventory with exact package and per-RID asset hashes. FFmpeg is
distributed as replaceable shared libraries with the exact corresponding-source
archive and build configuration for each RID; the binaries report
LGPL-2.1-or-later while their NuGet metadata says LGPL-2.1-only, and both
observations are retained. OpenAL Soft is not bundled; the application loads the
system `libopenal.so.1`.

See [component notices](THIRD-PARTY-NOTICES.md) for the retained credits. Games, firmware and keys are supplied by the user.
