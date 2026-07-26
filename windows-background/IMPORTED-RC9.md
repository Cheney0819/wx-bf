# Imported RC9 Baseline

Imported on 2026-07-25 from the immutable RC9 source snapshot:

- Source: `/Users/jiee/Documents/Codex/2026-07-19/she-2/outputs/wx411_recover/windows-easy/archive/rc9-frozen-20260725/Wx411Easy-v1.5-refactor-rc9-source.zip`
- Source ZIP SHA-256: `45bb644a0dce03331bc9c57901700d73e7976750b59ee03b20af50d7a3e40ebc`
- Frozen release ZIP SHA-256: `5d1d5e3b8159b439e044598fa9ae35c4d37a5d006bd2dd7c27e60f474da6713d`
- Imported source: `src/Wx411.Core/`
- Imported tests: `tests/Wx411.Core.Tests/`
- Test-only source-contract fixture: `tests/Rc9SourceFixture/windows-easy/` (111 files extracted from the same source ZIP; never compiled or published)
- Test-only `package_source.py` SHA-256: `4ad9d204553ab25a949ca2ff8954a300a3228541bc4a2d228c4a9784e2a7ef37`
- SQLCipher fixture SHA-256: `5b574c49e27eb3dcffcedebb0a122c3c7f735a8b8f6fb410c1ccc7477fcb271d`
- Non-default SQLCipher fixture SHA-256: `3fece48c3efdb1c0c7cfefbafffa72bd228be2d0dcac96856a05cbfe3f072922`

The WinForms application, evidence UI, build output, and release artifacts are not imported. The frozen archive remains unchanged. Integration-specific changes below must remain narrow and must preserve the original RC9 test suite.

## Integration Deltas

- `tests/Wx411.Core.Tests/TestSourceTree.cs`: also locates the imported, test-only source-contract fixture so source inspection tests remain self-contained outside the original repository layout.
- `tests/Wx411.Core.Tests/ReleaseContractTests.cs`: expects the frozen RC9 guide identity (`1.5 RC9`) instead of the stale pre-release documentation identity (`1.5-dev`). Production source behavior is unchanged.
- `src/Wx411.Core/RecoveryContracts.cs`: adds a synchronous validated-key sink contract that receives a read-only key span.
- `src/Wx411.Core/CallpointCaptureRecoveryService.cs`: calls the optional sink only after complete export and SQLite integrity validation, then clears its temporary key copy.
- `tests/Wx411.Core.Tests/CallpointCaptureRecoveryServiceTests.cs`: covers successful persistence and failed-export suppression of the sink.
- Cleanup and discovery catches in six imported Core files now contain intent comments; runtime behavior is unchanged and the integration audit no longer contains unexplained empty catches.
