"""Create-only recovery archive for observed R4 resource failures, never a PASS gate."""
import datetime
import json
import pathlib
import shutil
import validate as v


def archive():
    parent = v.ROOT / '.ai-tmp/evidence/P7/P7-R4-large-batch-soak'
    number = max([int(p.name[4:]) for p in parent.glob('run-*') if p.name[4:].isdigit()] + [0]) + 1
    run = parent / ('run-' + str(number))
    run.mkdir(parents=True, exist_ok=False)
    payloads = []

    def copy(source, relative):
        before = v.identity(source)
        target = run / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with source.open('rb') as src, target.open('xb') as dst:
            shutil.copyfileobj(src, dst)
        after, original_after = v.identity(target), v.identity(source)
        assert before == original_after and before['sha256'] == after['sha256']
        payloads.append(dict(path=target.relative_to(run).as_posix(), bytes=after['bytes'],
                             sha256=after['sha256'], temporarySource=before))

    prefixes = ('soak-', 'mf-', 'stage-isolation-', 'large-', 'pause-', 'qualification-',
                'targeted-', 'independent-', 'build-driver-', 'build-tests-', 'configure-')
    exact = {'environment', 'diagnostics', 'source-before-R4', 'source-v2',
             'source-v3-repair1', 'source-v3-repair3', 'prepare-v3', 'q3',
             'LivePhotoBoxNative-v1', 'LivePhotoBoxNativeTestHarness-v1'}
    roots = [p for p in v.WORK.iterdir() if p.is_dir() and (p.name in exact or p.name.startswith(prefixes))]
    for name in ('native-v2-build-work', 'native-v3-build-work'):
        roots.extend(p for p in (v.WORK / name).iterdir() if p.is_dir() and p.name.startswith(
            ('LivePhotoBoxNative-', 'LivePhotoBoxNativeTestHarness-', 'configure-')))
    for root in sorted(roots):
        for source in sorted(root.rglob('*')):
            if source.is_file():
                copy(source, pathlib.Path('raw') / source.relative_to(v.WORK))
    for source in sorted(v.WORK.glob('*.json')):
        copy(source, pathlib.Path('bindings') / source.name)
    for source in sorted((v.WORK / 'fixtures').glob('*.json')):
        copy(source, pathlib.Path('fixture-provenance') / source.name)
    # Preserve exact failed-soak runtime and source identity. Current diagnostic
    # source is explicitly separate and cannot be represented as this candidate.
    for tag in ('v6', 'v11', 'v22', 'v27', 'v28', 'v29', 'v31', 'v32', 'v34'):
        manifest = v.read(v.WORK / ('driver-' + tag + '.json'))
        for item in manifest['runtime']:
            source = v.ROOT / item['path']
            assert v.hashfile(source) == item['sha256']
            copy(source, pathlib.Path('runtime') / ('driver-' + tag) / source.relative_to(v.WORK / ('driver-' + tag)))
    for source in sorted((v.ROOT / 'tools/p7-r4-soak-validation').glob('*')):
        if source.is_file():
            copy(source, pathlib.Path('recovery-tools') / source.name)
    for relative in ('LivePhotoBox.Core/Services/LivePhotoMergeRunnerService.cs',
                     'LivePhotoBox.Native/src/media/media_inspector.cpp',
                     'LivePhotoBox.Native/src/media/video_converter.cpp'):
        copy(v.ROOT / relative, pathlib.Path('recovery-source') / relative)
    for source in (v.ROOT / 'docs/LivePhotoBox-Current-Roadmap/LivePhotoBox-P7-细分路线图').glob('P7-R4-*.md'):
        copy(source, pathlib.Path('context') / source.name)
    for label in ('soak-v6', 'soak-v11'):
        assert v.read(v.WORK / ('analysis-' + label + '.json'))['passedWorkloadAssertions']
    index = dict(schema=1, phase='P7', task='P7-R4-large-batch-soak', run=number,
                 status='FAIL_RESOURCE_RETENTION_RECOVERY_SNAPSHOT',
                 createdUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                 head=v.helper.git('rev-parse', 'HEAD'), branch=v.helper.git('branch', '--show-current'),
                 roadmapRevision='317465937A08A908F318ED0F1005AFF7D5B41E637DFF75276F7C08732BB859A7',
                 conclusion='Two complete >=30 minute mixed soaks passed workload/cleanup/source assertions but failed resource retention. Repeated MF SDR retains one Semaphore per call. No R4 acceptance.',
                 identityBoundary='Each historical driver manifest binds its own source/runtime. raw/diagnostics/source-diag*.cpp are temporary probe source only; recovery-source has all diagnostic Native changes removed. Neither is an accepted final candidate. R3 source baseline remains in sealed predecessor.',
                 payloads=payloads, stop='R4 remains open; no R5/P8 or publication.')
    v.save(run / 'evidence-index.json', index)
    print(json.dumps(v.identity(run / 'evidence-index.json'), ensure_ascii=True))


if __name__ == '__main__':
    archive()
