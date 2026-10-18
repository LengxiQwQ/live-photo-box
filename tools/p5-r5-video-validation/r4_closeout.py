"""Create-only R4 HEVC reopen bindings and durable evidence. Offline only.

This collector does not decide acceptance or modify an existing formal run.
Resource and fresh independent review verdicts remain separate requirements.
"""
import datetime
import importlib.util
import pathlib
import shutil
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/p7-r4-soak-validation'))
spec = importlib.util.spec_from_file_location('r4_capture', ROOT / 'tools/p7-r4-soak-validation/validate.py')
v = importlib.util.module_from_spec(spec)
spec.loader.exec_module(v)
W = v.WORK
H = W / 'hevc-migration'


def verify(items):
    for item in items:
        assert v.identity(ROOT / item['path']) == item, item['path']


def inputs():
    names = v.helper.git('ls-files', '-c', '-o', '--exclude-standard').splitlines()
    prefixes = ('LivePhotoBox.Core/', 'LivePhotoBox.Native/', 'LivePhotoBox.CLI/',
                'tests/LivePhotoBox.Core.Tests/', 'tests/LivePhotoBox.CLI.Tests/',
                'tools/p7-r3-backend-validation/', 'tools/p7-r4-soak-validation/',
                'tools/p5-r5-video-validation/', 'scripts/native/', 'cmake/', 'vcpkg-overlay-ports/')
    exact = {'CMakeLists.txt', 'vcpkg.json', 'vcpkg-configuration.json', 'Directory.Build.props',
             'Directory.Build.targets', 'global.json', 'NuGet.config', 'NuGet.Config',
             'LivePhotoBox/ViewModels/EditViewModel.cs', 'LivePhotoBox/Services/EditExportService.cs',
             'tests/fixtures/realsamples-manifest.json'}
    return [v.identity(ROOT / n) for n in sorted(set(names))
            if (n.startswith(prefixes) or n in exact) and (ROOT / n).is_file()
            and not any(part in pathlib.PurePosixPath(n).parts for part in ('obj', 'bin', '__pycache__'))]


def bind(tag, revision='', native_record='release-candidate-chroma-v16.json'):
    driver = v.read(W / ('driver-' + tag + '.json'))
    tests = v.read(W / ('tests-' + tag + '.json'))
    assert driver['sources'] == tests['sources'] == v.sources()
    verify(driver['runtime']); verify(tests['runtime'])
    for name in ('LivePhotoBox.Core.dll', 'LivePhotoBox.Native.dll', 'LivePhotoBox.Video.Libav.dll'):
        a = v.identity(W / ('driver-' + tag) / name)
        b = v.identity(W / ('tests-' + tag) / name)
        assert (a['bytes'], a['sha256']) == (b['bytes'], b['sha256'])
    native_path = H / native_record
    native = v.read(native_path)
    verify([s for s in native['sources'] if s['path'].startswith('LivePhotoBox.Native/')])
    verify(native['artifacts'])
    changed_build_inputs = []
    for item in native['buildInputs']:
        actual = v.identity(ROOT / item['path'])
        if actual != item:
            assert item['path'] == 'scripts/native/build-p7-r4-hevc-prerequisites.ps1'
            changed_build_inputs.append({'before': item, 'after': actual})
    package = v.read(H / ('release-package-' + tag + '.json'))
    assert package['passed'] and package['productionBuildEntry']
    verify(package['files'])
    assert v.read(H / ('release-package-' + tag + '-build/process.json'))['directChildExitCode'] == 0
    for name in ('LivePhotoBox.Native.dll', 'LivePhotoBox.Video.Libav.dll'):
        assert v.hashfile(H / ('release-package-' + tag) / name) == v.hashfile(W / ('driver-' + tag) / name)
    v.save(H / ('candidate-' + tag + revision + '.json'), {
        'head': v.helper.git('rev-parse', 'HEAD'), 'branch': v.helper.git('branch', '--show-current'),
        'sources': inputs(), 'campaignSources': driver['sources'], 'driverRuntime': driver['runtime'],
        'testRuntime': tests['runtime'], 'nativeBuild': v.identity(native_path),
        'nativeBuildEvidenceDirectories': native.get('buildEvidenceDirectories',
            [str((H / name).relative_to(ROOT)).replace('\\', '/') for name in ('release-chroma-v16', 'release-harness-chroma-v16')]),
        'historicalNativeBuildInputs': native['buildInputs'], 'buildEntryCompatibilityDelta': changed_build_inputs,
        'currentNativeBuildInputs': [v.identity(ROOT / item['path']) for item in native['buildInputs']],
        'buildEntryCompatibilityReason': 'No drift is allowed except the explicitly recorded PS5 module-free SHA256/same-host JSON compatibility bridge; a new Native build with current inputs has an empty delta. Normal Release verifies actual prerequisite, CMake/MSBuild and Native/sidecar bytes.',
        'normalReleaseVerification': v.identity(H / ('release-package-' + tag + '.json')),
        'samples': v.helper.samples(), 'gitStatus': v.helper.git('status', '--porcelain=v1'),
        'packagingScope': 'Normal Release build verifies the production build entry, shipped Native/sidecar and notices; its independently compiled Core is not relabeled as the byte-identical soak Core.',
        'stop': 'R4 only; no R5/P8/publication/full-product release conclusion.'})


def seal(tag, revision='', dry='', failure=False):
    candidate_path = H / ('candidate-' + tag + revision + '.json')
    candidate = v.read(candidate_path)
    verify(candidate['sources']); verify(candidate['driverRuntime']); verify(candidate['testRuntime'])
    verify([candidate['nativeBuild'], candidate['normalReleaseVerification']])
    verify(candidate['currentNativeBuildInputs'])
    assert candidate['head'] == v.helper.git('rev-parse', 'HEAD')
    assert candidate['branch'] == v.helper.git('branch', '--show-current')
    assert candidate['campaignSources'] == v.sources()
    pipeline = H / ('pipeline-' + tag)
    capture_recovery = None
    if (pipeline / 'completed.json').exists():
        assert v.read(pipeline / 'completed.json')['passedOperationalAssertions']
        outer = v.read(H / ('pipeline-' + tag + '-process/process.json'))
        assert outer['directChildExitCode'] == 0 and not outer.get('timedOut')
        consumer = v.read(W / ('soak-' + tag) / 'process/process.json')
        assert consumer['directChildExitCode'] == 0 and not consumer.get('timedOut')
    else:
        # Archive an interrupted *failed* campaign honestly. This branch must
        # never authorize a success snapshot or invent an unobserved child exit.
        assert failure, 'Incomplete capture cannot be accepted as a success'
        capture_recovery = H / ('capture-recovery-' + tag + '.json')
        recovery = v.read(capture_recovery)
        assert recovery['acceptanceAllowed'] is False
        assert recovery['nativeChildDirectExitCode'] is None
        assert recovery['sourceSet'] == candidate['campaignSources']
        verify([recovery['outerCapture']] + recovery['completionFiles'])
        outer = v.read(ROOT / recovery['outerCapture']['path'])
        assert outer.get('timedOut') is True
        assert type(outer['directChildExitCode']) is int and outer['directChildExitCode'] != 0
        assert v.read(pipeline / 'qualified.json')['passed']
    assert v.read(W / ('targeted-' + tag) / 'summary.json')['passed']
    matrix = ROOT / ('.ai-tmp/workspace/P5-R5/p7-r4-hevc-v2-matrix-' + tag)
    report = v.read(matrix / 'report.json')
    assert report['state'] == 'matrix-pass' and report['passedRouteCount'] == 11
    assert report['passedFailureCount'] == 5 and report['skipCount'] == 0
    for p in (W / ('independent-soak-' + tag) / 'summary.json',
              H / ('output-checks-soak-' + tag) / 'summary.json',
              H / ('release-package-' + tag + '.json')):
        assert v.read(p)['passed']
    analysis = W / ('analysis-soak-' + tag + '.json')
    assert v.read(analysis)['passedWorkloadAssertions']
    pixel = H / ('pixel-layout-' + tag + '-r6')
    if tag == 'v63' and not failure:
        pixel_summary = v.read(pixel / 'summary.json')
        assert pixel_summary['passed'] and len(pixel_summary['rows']) == 14
        assert all(row['passed'] for row in pixel_summary['rows'])
        assert sum(row['expectedSuccess'] for row in pixel_summary['rows']) == 12
        pixel_capture = v.read(H / ('pixel-layout-' + tag + '-r6-process/process.json'))
        assert pixel_capture['directChildExitCode'] == 0 and not pixel_capture.get('timedOut')
        verify(pixel_summary['runtime'])
    verdict = H / ('resource-verdict-' + tag + '.json')
    assert v.read(verdict)['passed'] is (not failure) and v.read(verdict)['soak'] == 'soak-' + tag
    assert v.read(verdict)['candidate'] == v.identity(candidate_path)
    assert v.read(verdict)['analysis'] == v.identity(analysis)
    assert v.read(verdict)['resourceSeries'] == v.identity(W / ('soak-' + tag) / 'case/resources.jsonl')
    assert v.read(verdict)['mixed'] == v.identity(W / ('soak-' + tag) / 'case/mixed.json')
    parent = ROOT / '.ai-tmp/evidence/P7/P7-R4-large-batch-soak'
    number = max(int(p.name[4:]) for p in parent.glob('run-*') if p.name[4:].isdigit()) + 1
    run = parent / ('run-' + str(number))
    payloads = []
    copied = set()
    planned = []
    expected = {str((ROOT / item['path']).resolve()): item
                for category in ('sources', 'driverRuntime', 'testRuntime') for item in candidate[category]}

    def copy(source, relative):
        source = pathlib.Path(source)
        relative = pathlib.Path(relative)
        key = relative.as_posix()
        assert key not in copied, key
        copied.add(key)
        original = v.identity(source)
        if str(source.resolve()) in expected:
            assert original == expected[str(source.resolve())], str(source)
        planned.append((source, relative, original))
        payloads.append({'path': key, 'bytes': original['bytes'], 'sha256': original['sha256'],
                         'temporarySource': original})

    def tree(source, relative):
        source = pathlib.Path(source)
        assert source.is_dir(), str(source)
        for path in sorted(source.rglob('*')):
            if path.is_file() and '__pycache__' not in path.parts:
                copy(path, pathlib.Path(relative) / path.relative_to(source))

    # Only final candidate raw evidence; failed engineering attempts are separately
    # indexed and never relabeled as final PASS. Historical formal run1-3 stay put.
    for name in ('build-driver-' + tag, 'build-tests-' + tag, 'targeted-' + tag,
                 'large-supported-' + tag, 'large-memory-' + tag, 'qualification-' + tag,
                 'soak-' + tag, 'independent-soak-' + tag):
        tree(W / name, pathlib.Path('raw') / name)
    for name in ('pipeline-' + tag, 'build-route-driver-' + tag, 'matrix-' + tag + '-process',
                 'output-checks-soak-' + tag, 'release-package-' + tag + '-build', 'chroma-fixed-' + tag,
                 'imports-' + tag + '-LivePhotoBox.Native.dll', 'imports-' + tag + '-LivePhotoBox.Video.Libav.dll'):
        tree(H / name, pathlib.Path('raw/hevc-migration') / name)
    if tag == 'v63' and not failure:
        tree(pixel, 'raw/additional-pixel-layouts')
        tree(H / ('pixel-layout-' + tag + '-r6-process'), 'raw/additional-pixel-layout-capture')
        for suffix in ('', '-r2', '-r3', '-r4', '-r5'):
            label = 'pixel-layout-' + tag + suffix
            # Qualification failures retain their original logs, inputs,
            # outputs, source snapshots and runtime hashes, not duplicate DLLs.
            for path in sorted((H / label).rglob('*')):
                if path.is_file() and 'runtime' not in path.relative_to(H / label).parts:
                    copy(path, pathlib.Path('engineering-pixel-qualification') / label / path.relative_to(H / label))
            if (H / (label + '-process')).exists():
                tree(H / (label + '-process'), pathlib.Path('engineering-pixel-qualification') / (label + '-process'))
    for relative in candidate['nativeBuildEvidenceDirectories']:
        directory = ROOT / relative
        assert directory.resolve().is_relative_to(H.resolve())
        tree(directory, pathlib.Path('raw/hevc-migration') / directory.relative_to(H))
    tree(H / 'r3-regression' / ('probes-' + tag), 'raw/backend-truth')
    for label in ('build-probe-' + tag, 'build-tests-' + tag):
        tree(H / 'r3-regression' / label, pathlib.Path('raw/backend-truth-build') / label)
    copy(H / 'r3-regression' / ('candidate-' + tag + '.json'), 'bindings/r3-candidate.json')
    tree(matrix, 'raw/p5-matrix')
    copy(ROOT / candidate['nativeBuild']['path'], pathlib.Path('bindings') / pathlib.Path(candidate['nativeBuild']['path']).name)
    for name in (candidate_path.name,
                 'multidepth-codec-identity.json', 'r4-hevc-v2-libav-prerequisites.json',
                 'release-package-' + tag + '.json', 'resource-verdict-' + tag + '.json'):
        copy(H / name, pathlib.Path('bindings') / name)
    if capture_recovery is not None:
        copy(capture_recovery, pathlib.Path('bindings') / capture_recovery.name)
        tree(H / ('pipeline-' + tag + '-process'), 'raw/outer-capture-timeout')
    else:
        tree(H / ('pipeline-' + tag + '-process'), 'raw/outer-capture')
    for p in (analysis, W / ('driver-' + tag + '.json'), W / ('tests-' + tag + '.json')):
        copy(p, pathlib.Path('bindings') / p.name)
    for p in sorted((W / 'fixtures').glob('*.json')):
        copy(p, pathlib.Path('fixtures') / p.name)
    for item in candidate['sources']:
        copy(ROOT / item['path'], pathlib.Path('source') / item['path'])
    for category in ('driverRuntime', 'testRuntime'):
        for item in candidate[category]:
            source = ROOT / item['path']
            base = W / (('driver-' if category == 'driverRuntime' else 'tests-') + tag)
            copy(source, pathlib.Path('runtime') / category / source.relative_to(base))
    for record, base in ((pipeline / 'route-runtime.json', H / ('route-driver-' + tag)),
                         (pipeline / 'cli-runtime.json', H / ('cli-' + tag))):
        runtime = v.read(record); verify(runtime)
        for name in ('LivePhotoBox.Core.dll', 'LivePhotoBox.Native.dll', 'LivePhotoBox.Video.Libav.dll'):
            assert v.hashfile(base / name) == v.hashfile(W / ('driver-' + tag) / name)
        for item in runtime:
            path = ROOT / item['path']
            copy(path, pathlib.Path('runtime') / base.name / path.relative_to(base))
    regression = v.read(pipeline / 'regression-runtime.json'); verify([regression])
    copy(ROOT / regression['path'], 'runtime/HevcNativeRegressions.exe')
    tree(H / ('release-package-' + tag), 'runtime/normal-release-package')
    # Build recipes and configuration are collected independently of product
    # source selection. Large sparse media are represented by recipe+hash and
    # actual independent packet/decode checks, not duplicated as multi-GB blobs.
    for path in (ROOT / '.ai-tmp/workspace/P5-R5/cmake-build/CMakeCache.txt',
                 H / 'x265-8/CMakeCache.txt', H / 'x265-10/CMakeCache.txt',
                 H / 'base-prerequisites/r4-hevc-v2-base-libav-prerequisites.json',
                 H / 'base-prerequisites/minimal-libav-r4-hevc-v2-base/minimal-libav-build-record.json'):
        copy(path, pathlib.Path('build-inputs') / path.relative_to(ROOT / '.ai-tmp/workspace'))
    ffsource = H / 'base-prerequisites/minimal-libav-r4-hevc-v2-base/src'
    for name in ('config.h', 'config_components.h', 'config.asm', 'config_components.asm',
                 'ffbuild/config.mak', 'ffbuild/config.log'):
        copy(ffsource / name, pathlib.Path('build-inputs/ffmpeg-config') / name)
    for folder in ('configure-x265-8', 'configure-x265-10', 'build-x265-8', 'build-x265-10', 'combine-x265'):
        tree(H / folder, pathlib.Path('build-inputs') / folder)
    for path in sorted((H / 'codecs').rglob('*')):
        if path.is_file(): copy(path, pathlib.Path('build-inputs/codecs') / path.relative_to(H / 'codecs'))
    codec = v.read(H / 'multidepth-codec-identity.json')
    verify([codec['source']])
    copy(pathlib.Path(codec['source']['path']), 'build-inputs/Multicorewareinc-x265-4.3.tar.gz')
    prefix = H / 'base-prerequisites/minimal-libav-r4-hevc-v2-base/prefix'
    tree(prefix, 'build-inputs/minimal-libav-prefix')
    # Keep the relevant failed attempts visible; no failed row is reclassified.
    for label in ('release-package-v59-build', 'release-package-v59-build-fix1', 'chroma-before-v59', 'chroma-before2-v59'):
        tree(H / label, pathlib.Path('engineering-failures') / label)
    resource_experiment = H / 'resource-thread-ab-v61'
    if resource_experiment.exists():
        # Historical resource A/B is distinct from the final candidate proof.
        for name in ('baseline-identity.json', 'nomf-identity.json', 'product-runtime.json',
                     'comparison.json', 'read-only-review.json', 'candidate-video_sidecar.cpp'):
            copy(resource_experiment / name, pathlib.Path('engineering-resource-ab-v61') / name)
        copy(resource_experiment / 'baseline/video_sidecar.cpp', 'engineering-resource-ab-v61/baseline-video_sidecar.cpp')
        for name in ('execute-nomf', 'nomf'):
            tree(resource_experiment / name, pathlib.Path('engineering-resource-ab-v61') / name)
        copy(ROOT / '.ai-tmp/evidence/P7/P7-R4-large-batch-soak/run-4/evidence-index.json',
             'engineering-resource-ab-v61/prior-failed-run-4-index.json')
    lifetime = H / 'cleanup-lifetime-v63'
    if lifetime.exists():
        for name in ('production-before.json', 'NeutralMediaService.before.cs', 'before-runtime.json',
                     'after-runtime.json', 'expected-red.json', 'red-green-summary.json',
                     'independent-lifecycle-review.json'):
            copy(lifetime / name, pathlib.Path('engineering-cleanup-lifetime-v63') / name)
        for name in ('build-before', 'build-after', 'execute-before', 'execute-after',
                     'results-before', 'results-after'):
            tree(lifetime / name, pathlib.Path('engineering-cleanup-lifetime-v63') / name)
        for phase in ('before', 'after'):
            for name in ('LivePhotoBox.Core.dll', 'LivePhotoBox.Core.pdb',
                         'LivePhotoBox.Core.Tests.dll', 'LivePhotoBox.Core.Tests.pdb'):
                copy(lifetime / ('tests-' + phase) / name,
                     pathlib.Path('engineering-cleanup-lifetime-v63') / ('tests-' + phase) / name)
        for name in ('evidence-index.json', 'recovery-independent-review.json'):
            copy(ROOT / '.ai-tmp/evidence/P7/P7-R4-large-batch-soak/run-5' / name,
                 pathlib.Path('engineering-cleanup-lifetime-v63/prior-failed-run-5') / name)
    heap_diagnosis = H / 'mixed-heap-v62'
    if (heap_diagnosis / 'identity-after.json').exists():
        tree(heap_diagnosis, 'engineering-mixed-heap-v62')
    chroma = v.read(H / ('chroma-fixed-' + tag) / 'summary.json')
    assert chroma['passed'] and len(chroma['rows']) == 4
    verify([chroma['original']])
    copy(ROOT / chroma['original']['path'], 'fixtures/chroma-derived-ancestor.mp4')
    for name in ('summary.json', 'results/r4.trx', 'execute/process.json', 'execute/stdout.txt', 'execute/stderr.txt'):
        copy(W / 'targeted-v57' / name, pathlib.Path('engineering-failures/targeted-v57') / name)
    context = ROOT / 'docs/LivePhotoBox-Current-Roadmap/LivePhotoBox-P7-细分路线图'
    for name in ('P7-R4-Local-Execution-Plan.md', 'P7-R4-coverage-matrix.md',
                 'P7-R4-HEVC-backend-reopen-plan.md', 'P7-R4-HEVC-implementation-status.md',
                 'P7-R4-resource-diagnosis-followup.md'):
        copy(context / name, pathlib.Path('context') / name)
    for name in ('README.md',): copy(ROOT / '.ai-tmp/evidence' / name, pathlib.Path('context') / name)
    # All predictable missing-file, identity and duplicate-destination errors
    # are diagnosed before allocating a formal run number.
    verify(candidate['sources']); verify(candidate['driverRuntime']); verify(candidate['testRuntime'])
    assert candidate['head'] == v.helper.git('rev-parse', 'HEAD')
    assert candidate['branch'] == v.helper.git('branch', '--show-current')
    if dry:
        assert dry == 'dry'
        print({'preflightPassed': True, 'payloads': len(planned),
               'bytes': sum(row[2]['bytes'] for row in planned), 'wouldAllocate': str(run)})
        return
    run.mkdir(exist_ok=False)
    for source, relative, original in planned:
        target = run / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with source.open('rb') as src, target.open('xb') as dst:
            shutil.copyfileobj(src, dst)
        actual = v.identity(target)
        assert actual['bytes'] == original['bytes'] and actual['sha256'] == original['sha256']
    verify(candidate['sources']); verify(candidate['driverRuntime']); verify(candidate['testRuntime'])
    index = {'schema': 2, 'status': ('FAIL_RESOURCE_STABILITY_NOT_PROVEN_CAPTURE_INCOMPLETE' if capture_recovery is not None else 'FAIL_RESOURCE_STABILITY_NOT_PROVEN') if failure else 'COMPLETE_PENDING_FRESH_INDEPENDENT_REVIEW',
             'createdUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
             'phase': 'P7', 'task': 'P7-R4-large-batch-soak', 'run': number,
             'head': candidate['head'], 'branch': candidate['branch'],
             'roadmapRevision': '317465937A08A908F318ED0F1005AFF7D5B41E637DFF75276F7C08732BB859A7',
             'candidate': v.identity(candidate_path), 'payloads': payloads,
             'captureRecovery': v.identity(capture_recovery) if capture_recovery is not None else None,
             'stop': candidate['stop']}
    v.save(run / 'evidence-index.json', index)
    for item in payloads:
        actual = v.identity(run / item['path'])
        assert actual['bytes'] == item['bytes'] and actual['sha256'] == item['sha256']
    print(v.identity(run / 'evidence-index.json'))


if __name__ == '__main__':
    if sys.argv[1] == 'seal-failure': seal(*sys.argv[2:], failure=True)
    else: {'bind': bind, 'seal': seal}[sys.argv[1]](*sys.argv[2:])
