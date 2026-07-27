# Weixin 4.1.12.26 Callpoint Support Design

## Problem

The Windows client discovers 18 database candidates but stops before live key
capture with `unsupported_module`. The supplied production sample is
`Weixin.dll` version `4.1.12.26`, SHA-256
`4914a621a810ecbc0a132b6ff8f612658cfce323d3989b3e5fe32d4ff343ba46`.
The current catalog accepts only exact identities `4.1.11.55` and
`4.1.12.24`.

## IDA Evidence

The supplied loose IDA database was analyzed in the already-open IDA Pro 9.2
process after `ida_auto.auto_wait()`. Hex-Rays successfully decompiled every
candidate. The read-only evidence exports are kept beside the supplied sample:

- `weixin_4.1.12.26_ida_evidence.json`
- `weixin_4.1.12.26_ida_evidence.md`
- `analyze_weixin_411226_key_callpoints.py`

The script made no names, types, comments, byte patches, or other IDB changes.

The recovered call chain is:

```text
sqlite3_key(a1, pKey, nKey)
  -> codec_attach(a1, mainDbIndex, pKey, nKey)
     -> codec_init(holder, db, codec, pKey, nKey)
        -> codec_set_pass(codec, pKey, nKey, mode=0)

sqlite3_key_v2(a1, dbName, pKey, nKey)
  -> codec_attach(a1, selectedDbIndex, pKey, nKey)
```

At `sqlite3_key_sink + 0x2A`, the four instructions are `mov rcx,[rdi+38h]`,
`mov rdx,rax`, `mov r8d,ebx`, and `call sqlite3_key`, proving that the chosen
breakpoint sees `RDX=pKey` and `R8D=nKey`. The entry-point prototypes recovered
by Hex-Rays independently prove the Windows x64 register assignments for the
other five points.

The `DB_KEY_STING` string xref is not a key source. Its only enclosing function,
`sub_180568FB0`, copies an input `std::string` and appends the literal text
`DB_KEY_STING`; it does not read or forward key material. The two historical
business-key fallback definitions are also not required for this profile:
runtime arms at most four points and the first four verified SQLCipher points
cover both `sqlite3_key` entry paths and the shared codec path.

## Sample Evidence

The supplied 191,480,360-byte PE sample contains the following verified
callpoints. The first three signatures occur exactly once in the complete
image. `codec_attach_equiv` is identified by its function signature, its
callers from both key APIs, its call to `codec_init`, and the unchanged relative
layout of the adjacent SQLCipher functions.

| Callpoint | Signature RVA | Breakpoint RVA | Register semantics |
|---|---:|---:|---|
| `codec_set_pass_equiv` | `0x3485AE0` | `0x3485AE0` | `RDX=pKey`, `R8D=nKey` |
| `sqlite3_key_equiv` | `0x55380B0` | `0x55380B0` | `RDX=pKey`, `R8D=nKey` |
| `sqlite3_key_v2_equiv` | `0x5538160` | `0x5538160` | `R8=pKey`, `R9D=nKey` |
| `codec_attach_equiv` | `0x5537EC0` | `0x5537EC0` | `R8=pKey`, `R9D=nKey` |
| `sqlite3_key_sink` | `0x34B8A60` | `0x34B8A6A` | `RDX=pKey`, `R8D=nKey` |
| `codec_init_equiv` | `0x3486270` | `0x3486270` | `R9=pKey`, stack argument `nKey` |

The `.26` SQLCipher cluster is shifted by `0x3EC0` from `.24`; the
codec/sink cluster is shifted by `0x3640`. Stable signatures and unchanged
register setup, rather than the deltas alone, establish the new locations.

## Design

Add a separate `Weixin411226` `ModuleCallpointProfile` with the exact version,
exact SHA-256, `StructureOnly` holder strategy, and the six verified
callpoints above. Keep the four primary points first because the debugger arms
at most four breakpoints per attach. Do not add unproven `.26` business-key
fallbacks. Keep `.24` and `.11` profiles unchanged.

Make `.26` the preferred profile so the requested callpoint-name set includes
all six verified names. This preserves the existing first four selections for
`.24` and `.11`: profile-specific RVAs and signatures still come from the
identity selected after inspecting the actual loaded module.

Do not accept a version prefix, reuse `.24` RVAs, or weaken the SHA-256 check.
`ModuleInspectionCache` must continue verifying every signature against the
loaded PE image before writing a breakpoint.

## Failure Behavior

- A `.26` file with any other hash remains `unsupported_module`.
- A correct identity with a mismatched callpoint signature fails closed with
  no breakpoint armed.
- Older supported identities select their existing profiles and addresses.
- Database discovery, key validation, SQLCipher export, parser, upload, Android,
  and server behavior remain unchanged.

## Testing And Release

- Prove the catalog selects all three exact supported identities and rejects a
  wrong `.26` hash.
- Prove `.26` callpoint order, RVAs, register semantics, and exact signatures.
- Prove `.24` remains unchanged and `.26` is preferred.
- Verify the six signatures directly against the supplied sample without
  adding the sample to Git.
- Run all parser, background, and WPF Release checks.
- Publish Windows desktop version `1.0.25` after independent review.
