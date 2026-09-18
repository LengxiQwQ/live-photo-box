# RealSample corpus provisioning

## Purpose

`Category=RealSamples` verifies real-device media and must never silently fall back to synthetic fixtures. The binary media is excluded from source control because no public redistribution authorization is recorded for these captures. `tests/fixtures/realsamples-manifest.json` is the sole canonical authority for the P4-v1 corpus identity, provenance status, and R5 HEIC expectations.

Synthetic media remains separate: `tests/fixtures/synthetic/谷歌自己合成的.heic` is tracked and is only used by `Category=SyntheticFixture`.

## Corpus authority

- Version: `P4-v1`
- Required files: 17
- Transport: verifier-supplied archive; it is not publicly downloadable.
- Archive filename: `LivePhotoBox-RealSamples-P4-v1.zip`
- Archive size: `113797235` bytes
- Archive SHA-256: `E6AC9A75B34A0EA7CBF0C882596118140A0D040D2AA090875B341981095CE7E6`
- Layout: the archive root contains exactly the 17 manifest filenames, with no nested directories or extra files.

The verifier must receive this exact archive through an authorized evidence hand-off. Do not substitute a same-named file, upload the corpus without recorded permission, or treat a developer's `designs/` directory as verification authority.

## Provision

From a clean checkout, place the supplied archive at a local path and run:

```powershell
$env:LIVEPHOTOBOX_SAMPLES_ARCHIVE = '<path-to-LivePhotoBox-RealSamples-P4-v1.zip>'
.\scripts\testing\setup-test-fixtures.ps1
```

The script is the canonical and only provisioning entry point. Its source priority is explicit `-SourceDirectory`, `LIVEPHOTOBOX_SAMPLES_ARCHIVE` (or `-ArchivePath`), then an existing verified target cache. It does not read `designs/` and it never downloads media during `dotnet test`.

For a verifier-owned non-default corpus location:

```powershell
.\scripts\testing\setup-test-fixtures.ps1 -ArchivePath $env:LIVEPHOTOBOX_SAMPLES_ARCHIVE -TargetDirectory '<verified-corpus-root>'
$env:LIVEPHOTOBOX_TEST_SAMPLES_DIR = '<verified-corpus-root>'
```

## Verify and test

```powershell
.\scripts\testing\setup-test-fixtures.ps1 -VerifyOnly
dotnet test .\tests\LivePhotoBox.Core.Tests\LivePhotoBox.Core.Tests.csproj -c Release -p:Platform=x64 -p:SkipNativeBuild=true --filter 'Category=RealSamples'
dotnet test .\tests\LivePhotoBox.Core.Tests\LivePhotoBox.Core.Tests.csproj -c Release -p:Platform=x64 -p:SkipNativeBuild=true --filter 'Category=SyntheticFixture'
```

Provisioning verifies archive filename, size, SHA-256, a flat exact archive allow-list, and then every required file's filename, size, and SHA-256. Missing files, duplicate/nested archive entries, size mismatches, and hash mismatches are terminating failures. Extra files may remain in a provisioned directory, but they are not part of the formal corpus.

The expected `-VerifyOnly` summary is 17 required and verified, with zero missing, hash mismatches, and size mismatches. The test resolver only resolves already-provisioned roots; it does not download, synthesize, or search a developer-specific source directory.

## Fresh-clone procedure

1. Start from a clone/worktree with no `tests/fixtures/realsamples/`, no `designs/` dependency, and no `LIVEPHOTOBOX_TEST_SAMPLES_DIR` value.
2. Supply the exact authorized P4-v1 archive and execute the provision command above.
3. Run `-VerifyOnly`, then set `LIVEPHOTOBOX_TEST_SAMPLES_DIR` only if a non-default target was chosen.
4. Run the RealSamples and SyntheticFixture commands above. Record the resolved corpus root printed by setup and the test counts.

This establishes reproducibility with a supplied verifier corpus archive. It is not a claim that the corpus is publicly downloadable.
