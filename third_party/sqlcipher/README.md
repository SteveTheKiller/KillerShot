# e_sqlcipher.dll - vendored SQLCipher native (x64)

Replaces the deprecated `SQLitePCLRaw.lib.e_sqlcipher` NuGet package, whose entire line
was marked "legacy, no longer maintained" by its author in 2025. Building from upstream
source keeps us on current SQLCipher releases; the `SQLitePCLRaw.provider.e_sqlcipher`
package (a managed P/Invoke shim) still loads this DLL by name exactly as before.

The recipe reproduces SQLitePCL.raw's own build (github.com/ericsink/cb): the SQLCipher
amalgamation compiled with the LibTomCrypt crypto provider and AES-256-CBC, so the file
format, KDF (PBKDF2-HMAC-SHA512, 256000 iterations) and defaults are identical -
databases created under the old package open unchanged. KillerNotes and Killendar have
separate vendored copies. This upgrade changes KillerShot only; coordinate a family
upgrade separately.

## Current binary

| | |
|---|---|
| SQLCipher | 4.19.0 community (SQLite 3.53.4) |
| LibTomCrypt | 1.18.2 |
| Toolchain | llvm-mingw 20260616 (clang 22.1.8), ucrt |
| e_sqlcipher.dll sha256 | `ec67d36a1e01d9e68726319d88e6d3a9b294fa3ad52e71555c2a120f6c234a4a` |
| Signature | Unsigned development rebuild. Authenticode signing and timestamping remain a separately authorized release step. |

Source tarballs (verify before building):

| Tarball | URL | sha256 |
|---|---|---|
| sqlcipher v4.19.0 | github.com/sqlcipher/sqlcipher/archive/refs/tags/v4.19.0.tar.gz | `7075f96cbabe45b4ecfc2e6b1745a625f856f695b0827a5506ce9ed85b906aa0` |
| libtomcrypt 1.18.2 | github.com/libtom/libtomcrypt/releases/download/v1.18.2/crypt-1.18.2.tar.xz | `96ad4c3b8336050993c5bc2cf6c057484f2b0f9f763448151567fbab5e767b84` |
| llvm-mingw 20260616 | github.com/mstorsjo/llvm-mingw/releases/download/20260616/llvm-mingw-20260616-ucrt-ubuntu-22.04-x86_64.tar.xz | `534b92e067b22a6b4441f48ae9240a3341b17825d04d577eab0cf85c44b4deda` |

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

Signing, timestamping and final release review remain open. No release or family-wide
native update was performed.

## Build (Linux host, cross-compiles to Windows x64)

Extract the three tarballs into one folder. Generate the amalgamation first
(needs tclsh):

```sh
cd sqlcipher-4.19.0 && ./configure --with-tempstore=yes && make sqlite3.c && cd ..
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
