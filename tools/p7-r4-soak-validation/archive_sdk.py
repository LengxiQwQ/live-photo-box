"""Seal SDK diagnostic and clean-candidate regressions, explicitly not R4 PASS."""
import datetime
import json
import pathlib
import shutil
import validate as v


def archive():
    parent = v.ROOT / '.ai-tmp/evidence/P7/P7-R4-large-batch-soak'
    number = max(int(p.name[4:]) for p in parent.glob('run-*') if p.name[4:].isdigit()) + 1
    run = parent / ('run-' + str(number))
    run.mkdir(exist_ok=False)
    payloads = []

    def copy(source, relative):
        original = v.identity(source)
        target = run / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with source.open('rb') as src, target.open('xb') as dst:
            shutil.copyfileobj(src, dst)
        saved = v.identity(target)
        assert original == v.identity(source) and original['sha256'] == saved['sha256']
        payloads.append(dict(path=target.relative_to(run).as_posix(), bytes=saved['bytes'],
                             sha256=saved['sha256'], temporarySource=original))

    for label in ('sdk-repro', 'sdk-repro-ro-init', 'worker-isolation-feasibility-v36', 'targeted-v35', 'build-driver-v36'):
        root = v.WORK / label
        for source in sorted(root.rglob('*')):
            if not source.is_file():
                continue
            parts = source.relative_to(root).parts
            if parts[0] == 'build' and source.name not in ('MfSdkLifecycleRepro.exe', 'MfSdkLifecycleRepro.pdb', 'CMakeCache.txt'):
                continue
            copy(source, pathlib.Path('raw') / source.relative_to(v.WORK))
    for name in ('functional-candidate-v35.json', 'tests-v35.json', 'driver-v19.json', 'driver-v36.json', 'native-v3-repair3.json'):
        copy(v.WORK / name, pathlib.Path('bindings') / name)
    candidate = v.read(v.WORK / 'functional-candidate-v35.json')
    assert v.read(v.WORK / 'targeted-v35/summary.json')['passed']
    for item in candidate['productSources']:
        assert v.hashfile(v.ROOT / item['path']) == item['sha256']
    # Current production/test source is unchanged; post-aggregate edits only add
    # diagnostic modes and archival tools. Preserve both source inventories.
    current = v.sources()
    compiled_tests = {x['path']: x for x in v.read(v.WORK / 'tests-v35.json')['sources']}
    for item in current:
        if item['path'].startswith(('LivePhotoBox.Core/', 'LivePhotoBox.Native/', 'tests/LivePhotoBox.Core.Tests/')):
            assert item == compiled_tests[item['path']]
        copy(v.ROOT / item['path'], pathlib.Path('source') / item['path'])
    for category in ('testRuntime', 'driverRuntime', 'nativeArtifacts'):
        for item in candidate[category]:
            source = v.ROOT / item['path']
            assert v.hashfile(source) == item['sha256']
            copy(source, pathlib.Path('runtime') / category / source.relative_to(v.WORK))
    for name in ('P7-R4-resource-blocker.md', 'P7-R4-MF-isolation-proposal.md'):
        copy(v.ROOT / '.ai-tmp/evidence/P7/P7-R4-large-batch-soak/documentation' / name, pathlib.Path('context') / name)
    index = dict(schema=1, phase='P7', task='P7-R4-large-batch-soak', run=number,
                 status='SDK_DIAGNOSTIC_CONFIRMED_RESOURCE_BLOCKER_R4_OPEN', r4Accepted=False,
                 createdUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                 head=v.helper.git('rev-parse', 'HEAD'), branch=v.helper.git('branch', '--show-current'),
                 predecessor=v.identity(parent / 'run-1/evidence-index.json'),
                 currentSources=current, candidate=v.identity(v.WORK / 'functional-candidate-v35.json'),
                 conclusion='Project code not necessary to reproduce observed HEVC/MF Semaphore accumulation. Clean actual product candidate bounded aggregate223/223 passes; resource gate remains FAIL. Isolation is development-harness feasibility only.',
                 applicability='Core/Native/test source and test/runtime identities verified. Post-aggregate source changes add only diagnostic mf-once mode and archive/probe tools; no production or compiled test changes. No final R4 candidate claimed.',
                 userBoundary='No installed component update authorized or applied. General-user correctness; no per-machine special case. Production worker/packaging/authority changes are proposal-only and require a decision.',
                 payloads=payloads, stop='R4 remains open; no R5/P8 or Git publication.')
    v.save(run / 'evidence-index.json', index)
    for item in payloads:
        target = run / item['path']
        assert target.stat().st_size == item['bytes'] and v.hashfile(target) == item['sha256']
    result = dict(verified=True, index=v.identity(run / 'evidence-index.json'), payloadsVerified=len(payloads), mismatches=[])
    v.save(run / 'copy-verification.json', result)
    print(json.dumps(result, ensure_ascii=True))


if __name__ == '__main__':
    archive()
