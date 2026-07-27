# Weixin 4.1.12.26 Callpoint Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Accept the exact supplied `Weixin.dll 4.1.12.26` identity, capture its SQLCipher key through semantically verified callpoints, and release desktop pet `1.0.25`.

**Architecture:** Add one immutable `ModuleCallpointProfile` beside the two existing exact profiles. Runtime selection remains version plus SHA-256, and `ModuleInspectionCache` continues to verify every requested signature before arming at most four breakpoints.

**Tech Stack:** C# 12, .NET 8, xUnit, WPF, Inno Setup, GitHub Actions.

## Global Constraints

- Support only version `4.1.12.26` with SHA-256 `4914a621a810ecbc0a132b6ff8f612658cfce323d3989b3e5fe32d4ff343ba46`.
- Keep `4.1.11.55` and `4.1.12.24` identities and callpoints unchanged.
- Preserve exact fail-closed version, hash, and signature validation.
- Keep the supplied `Weixin.dll` and IDA database outside Git.
- Do not change Android, parser, upload, server, or database-discovery behavior.
- Publish desktop pet and installer version `1.0.25`.

---

### Task 1: Exact Module Profile

**Files:**
- Modify: `windows-background/tests/Wx411.Core.Tests/CallpointRuntimeTests.cs`
- Modify: `windows-background/src/Wx411.Core/CallpointProfile.cs`

**Interfaces:**
- Consumes: existing `ModuleCallpointProfile`, `CallpointDefinition`, and `FindByIdentity(string, string)`.
- Produces: public `CallpointProfiles.Weixin411226`; `Supported` contains three exact profiles; `Preferred` returns `.26`.

- [ ] **Step 1: Write the failing catalog test**

Change the catalog expectation to three profiles, assert `.26` exact identity selection, reject a zero hash, and assert `Preferred` is `Weixin411226`.

- [ ] **Step 2: Write the failing callpoint test**

Assert the six names in order and these exact tuples:

```text
codec_set_pass_equiv  sig=0x3485AE0 bp=0x3485AE0 semantics=Sqlite3KeySink
sqlite3_key_equiv     sig=0x55380B0 bp=0x55380B0 semantics=Sqlite3KeySink
sqlite3_key_v2_equiv  sig=0x5538160 bp=0x5538160 semantics=KeyInR8LengthInR9D
codec_attach_equiv    sig=0x5537EC0 bp=0x5537EC0 semantics=KeyInR8LengthInR9D
sqlite3_key_sink      sig=0x34B8A60 bp=0x34B8A6A semantics=Sqlite3KeySink
codec_init_equiv      sig=0x3486270 bp=0x3486270 semantics=KeyInR9LengthStack5
```

- [ ] **Step 3: Run the focused tests and verify RED**

Run:

```bash
dotnet test windows-background/tests/Wx411.Core.Tests/Wx411.Core.Tests.csproj --filter 'FullyQualifiedName~CallpointRuntimeTests'
```

Expected: catalog count/identity and missing `Weixin411226` assertions fail.

- [ ] **Step 4: Implement the minimal profile**

Add `.26` constants, six private definitions with the IDA-verified bytes, a `Weixin411226` `StructureOnly` profile, append it to `Supported`, and make it `Preferred`. Do not modify shared `.11` or `.24` definitions.

- [ ] **Step 5: Run the focused tests and verify GREEN**

Run the command from Step 3. Expected: all `CallpointRuntimeTests` pass.

### Task 2: Sample Signature Verification

**Files:**
- Verify only: `/Users/jiee/WorkBuddy/2026-07-27-17-15-58/wechat_re/payload/Weixin.dll`

**Interfaces:**
- Consumes: the six signature RVAs and byte arrays from Task 1.
- Produces: independent proof that committed signatures match the supplied PE bytes.

- [ ] **Step 1: Verify sample identity and signatures**

Run a read-only script that computes file size and SHA-256 and compares every profile signature against bytes at its RVA.

- [ ] **Step 2: Verify fail-closed locator behavior**

Run `CallpointRuntimeTests` and `ModuleInspectionCacheTests`; expected: exact `.26` identity selects the `.26` profile and wrong hashes remain rejected.

### Task 3: Release Version 1.0.25

**Files:**
- Modify: `windows-pet-wpf/DesktopPet.Wpf.csproj`
- Modify: `windows-pet-wpf/DesktopPetSetup.iss`

**Interfaces:**
- Consumes: existing workflow version extraction from `DesktopPet.Wpf.csproj`.
- Produces: WPF assembly/package and Inno Setup metadata version `1.0.25` / `1.0.25.0`.

- [ ] **Step 1: Change all desktop and installer version fields**

Replace `1.0.24` with `1.0.25` and `1.0.24.0` with `1.0.25.0` only in the two listed files.

- [ ] **Step 2: Verify version consistency**

Run:

```bash
rg -n '1\.0\.2[45]' windows-pet-wpf/DesktopPet.Wpf.csproj windows-pet-wpf/DesktopPetSetup.iss
```

Expected: all version metadata reports `1.0.25`; no `1.0.24` remains.

### Task 4: Review, Verification, and Release

**Files:**
- Review: all changed files from Tasks 1-3
- Verify: `.github/workflows/build-windows.yml`

**Interfaces:**
- Consumes: green focused tests and sample signature verification.
- Produces: reviewed `main`, pushed commit, and a Windows artifact workflow run.

- [ ] **Step 1: Run full .NET tests**

```bash
dotnet test windows-background/DesktopPet.Background.sln --configuration Release
```

Expected: zero failed tests.

- [ ] **Step 2: Build the WPF project**

```bash
dotnet build windows-pet-wpf/DesktopPet.Wpf.csproj --configuration Release -p:EnableWindowsTargeting=true
```

Expected: build exits zero.

- [ ] **Step 3: Review the diff and targeted runtime code**

Inspect `git diff --check`, `git diff`, breakpoint setup/restore logic, exact identity lookup, and release workflow. Fix only confirmed bugs with a failing regression test first.

- [ ] **Step 4: Commit and push main**

Stage only relevant files, commit with a scoped message, and run `git push origin main` after all verification is fresh.

- [ ] **Step 5: Trigger and verify Windows build**

Run `gh workflow run build-windows.yml --ref main`, watch the new run to completion, download `桌宠-1.0.25`, and compute the installer SHA-256.
