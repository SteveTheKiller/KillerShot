# e_sqlcipher.dll - vendored SQLCipher native (x64)

Replaces the deprecated `SQLitePCLRaw.lib.e_sqlcipher` NuGet package, whose entire line
was marked "legacy, no longer maintained" by its author in 2025. Building from upstream
source keeps us on current SQLCipher releases; the `SQLitePCLRaw.provider.e_sqlcipher`
package (a managed P/Invoke shim) still loads this DLL by name exactly as before.

The recipe reproduces SQLitePCL.raw's own build (github.com/ericsink/cb): the SQLCipher
amalgamation compiled with the LibTomCrypt crypto provider and AES-256-CBC, so the file
format, KDF (PBKDF2-HMAC-SHA512, 256000 iterations) and defaults are identical -
databases created under the old package open unchanged. KillerNotes and Killendar
also use this DLL; their copies are the same signed file.

## Current binary

| | |
|---|---|
| SQLCipher | 4.20.0 community (SQLite 3.54.1) |
| LibTomCrypt | 1.18.2 |
| Toolchain | llvm-mingw 20260616 (clang 22.1.8), ucrt |
| e_sqlcipher.dll sha256 | signed `91db382675516caa0b6dea22776c054ac354e0178e861359405d7f9832042f57` (unsigned build was `c7aef1d1588adb83bf3b8a58eefb22baddf66ffdc81ff22cd06e683bf4f8b3be`) |
| Signature | Signed and timestamped 2026-11-16 with the Open Source Developer Stephen Riley certificate. |

Source tarballs (verify before building):

| Tarball | URL | sha256 |
|---|---|---|
| sqlcipher v4.20.0 | github.com/sqlcipher/sqlcipher/archive/refs/tags/v4.20.0.tar.gz | `38705f51b61ac0636a5fd064f9a77fafabc7227b2aa2513f1b14c8bb1102affc` |
| libtomcrypt 1.18.2 | github.com/libtom/libtomcrypt/releases/download/v1.18.2/crypt-1.18.2.tar.xz | `96ad4c3b8336050993c5bc2cf6c057484f2b0f9f763448151567fbab5e767b84` |
| llvm-mingw 20260616 | github.com/mstorsjo/llvm-mingw/releases/download/20260616/llvm-mingw-20260616-ucrt-ubuntu-22.04-x86_64.tar.xz | `534b92e067b22a6b4441f48ae9240a3341b17825d04d577eab0cf85c44b4deda` |

## Dependency verification, November 16, 2026 (4.20.0)

[SQLCipher 4.20.0](https://www.zetetic.net/blog/2026/10/27/sqlcipher-4.20.0-release/) is a
maintenance release: SQLite 3.54.1, a deferred-error fix when `PRAGMA cipher_migrate` fails
(KillerShot never calls it) and a WebAssembly-only change. `PBKDF2_ITER` is still 256000
(src/sqlcipher.c line 111). The LibTomCrypt and llvm-mingw tarballs matched the hashes above.
The rebuilt DLL is PE32+ x64 and reports `4.20.0 community`.

`--selftest-sqlcipher-compat <folder> create` run on the 4.19.0 build made an account album and
a password album; `open 4.20.0` on the 4.20.0 build opened both, returned identical picture bytes,
found the note through search, refused a wrong password and changed the password. Album, storage
and library self-tests were rerun on the 4.20.0 build. Repeat the compat pair on every bump.

Build note: copying a DLL in keeps its old timestamp, and MSBuild then keeps embedding the
previous one. Touch the file (or rebuild clean) after replacing it.

## Dependency verification, October 20, 2026

The rebuilt binary reports `4.19.0 community`, retains `kdf_iter=256000`, and passes encrypted
round trips, FTS5 search and wrong-password rejection. Existing 4.18 account and password
albums open with the new DLL; the password album's search results and bytes are preserved.
PE inspection confirms x64, the expected SQLite exports and Windows system/UCRT imports.
The app and CLI NuGet audits reported no known package vulnerabilities. NuGet does not audit
this vendored DLL. The native build emits LibTomCrypt's existing ENDIAN_LITTLE macro warning.
All 26 album/storage scenarios pass with the rebuilt DLL. Packaged extraction preserves its
hash. A native URI regression reproduces plaintext output with invalid `hexkey=ZZ` on 4.18;
4.19 rejects the same URI with SQLITE_MISUSE and creates no plaintext database.

[SQLCipher 4.19.0](https://www.zetetic.net/blog/2026/09/08/sqlcipher-4.19.0-release/)
fixes two low-risk issues affecting 4.18.0: export alias quoting and invalid `hexkey` URI handling.
The reviewed password export uses the fixed alias `rekeyed`, and the production album code
uses connection-string builder passwords with no `hexkey` use found. Those observations limit
the identified paths; they do not establish that the native library has no vulnerabilities.

Signing, timestamping and final release review remain open. No release was performed.

## Build (Linux host, cross-compiles to Windows x64)

Extract the three tarballs into one folder. Generate the amalgamation first
(needs tclsh):

```sh
cd sqlcipher-4.20.0 && ./configure --with-tempstore=yes && make sqlite3.c && cd ..
```

Then run `build-e_sqlcipher.sh` (in this folder). It compiles the amalgamation plus the
34 LibTomCrypt files SQLCipher's libtomcrypt provider needs, with the same define set
SQLitePCL.raw used, and strips the result.

## Verification checklist (done for the current binary, repeat on every rebuild)

- PE32+ x64 DLL; imports limited to KERNEL32, ADVAPI32 and ucrt api-sets
- Exports include sqlite3_key, sqlite3_key_v2, sqlite3_rekey, sqlite3_open_v2
- `PBKDF2_ITER` in src/sqlcipher.c still 256000, or existing databases stop opening
- A native build of the identical sources passes a round-trip smoke test: FTS5 MATCH
  works, the file header is not plaintext, a wrong key fails with SQLITE_NOTADB,
  `PRAGMA cipher_version` reports the expected version
- On Windows: open an existing encrypted database, run a search, change the password

## Licenses

SQLCipher: [BSD-style](LICENSE-SQLCipher.txt) (Zetetic LLC).
LibTomCrypt 1.18.2: [dual WTFPL/public domain](LICENSE-LibTomCrypt.txt).
Both are GPLv3-compatible. This is SQLCipher Community Edition built unofficially;
it is not a Zetetic-supported binary.
