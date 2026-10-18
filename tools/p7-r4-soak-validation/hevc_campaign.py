"""Finite HEVC-reopen qualification followed by the unchanged R4 mixed soak.

No automatic repair, fallback, acceptance change, formal-run allocation or
production CLI codec use. Each stage captures its actual child exit code.
"""
import json
import pathlib
import shutil
import sys
import validate as v


def run(tag, native_record='release-candidate-chroma-v16.json'):
    v.FINAL_DRIVER = tag
    work = v.WORK / 'hevc-migration'
    destination = work / ('pipeline-' + tag)
    destination.mkdir(parents=True, exist_ok=False)
    before = v.sources()
    stages = []

    def stage(name, operation):
        print('BEGIN ' + name, flush=True)
        operation()
        assert before == v.sources(), 'Source drift during ' + name
        stages.append(name)
        v.save(destination / (name + '.json'), {'passed': True, 'scope': name})
        print('PASS ' + name, flush=True)

    build = v.read(work / native_record)
    for item in build['sources']:
        if item['path'].startswith('LivePhotoBox.Native/'):
            assert item == v.identity(v.ROOT / item['path']), item['path']
    for item in build['artifacts']:
        assert item == v.identity(v.ROOT / item['path']), item['path']
    stage('driver', lambda: v.build(tag))

    route = work / ('route-driver-' + tag)
    def route_build():
        v.checked([v.helper.DOTNET, 'build', 'tools/p5-r5-video-validation/route-driver/P5R5.RouteDriver.csproj',
            '-c', 'Release', '-p:Platform=x64', '-p:SkipNativeBuild=true',
            '-p:NativeArtifactsDir=' + str(v.NATIVE / 'Release/win-x64'), '-p:OutDir=' + str(route),
            '--verbosity', 'minimal'], work / ('build-route-driver-' + tag))
        shutil.copyfile(v.NATIVE / 'Release/win-x64/LivePhotoBox.Video.Libav.dll', route / 'LivePhotoBox.Video.Libav.dll')
        for name in ('LivePhotoBox.Core.dll','LivePhotoBox.Core.pdb','LivePhotoBox.Native.dll'):
            shutil.copyfile(v.WORK / ('driver-' + tag) / name, route / name)
        v.save(destination / 'route-runtime.json', [v.identity(p) for p in sorted(route.rglob('*')) if p.is_file()])
    stage('route-build', route_build)
    stage('chroma-location', lambda: v.checked([sys.executable,
        v.ROOT / 'tools/p5-r5-video-validation/r4_chroma_checks.py', tag],
        destination / 'chroma-location'))
    stage('aggregate', lambda: v.tests(tag, 'targeted-' + tag))

    def matrix():
        v.checked([sys.executable, '-X', 'utf8', v.ROOT / 'tools/p5-r5-video-validation/validate.py',
            '--record-id', 'p7-r4-hevc-v2-matrix-' + tag, '--profile', 'P7-R4-HEVC-v2',
            '--driver-dll', route / 'P5R5.RouteDriver.dll'], work / ('matrix-' + tag + '-process'))
        report = v.read(v.ROOT / ('.ai-tmp/workspace/P5-R5/p7-r4-hevc-v2-matrix-' + tag) / 'report.json')
        assert report['state'] == 'matrix-pass' and report['passedRouteCount'] == 11
        assert report['passedFailureCount'] == 5 and report['skipCount'] == 0
    stage('matrix', matrix)

    def backend_truth():
        r = v.helper
        r.WORK = work / 'r3-regression'
        r.NATIVE = v.NATIVE
        r.build(tag, core_runtime=v.WORK / ('driver-' + tag))
        r.probes(tag, 'probes-' + tag)
        # The final R4 aggregate is now a superset of the R3 selector. Prove
        # that using discovery, rather than redundantly running it twice.
        v.checked([r.DOTNET, 'vstest', v.WORK / ('tests-' + tag) / 'LivePhotoBox.Core.Tests.dll',
            '--ListTests', '--TestCaseFilter:' + r.selector()], destination / 'r3-discovery')
        names = [s.strip() for s in (destination / 'r3-discovery/stdout.txt').read_text(encoding='utf-8-sig').splitlines()
            if s.strip().startswith('LivePhotoBox.Core.Tests.')]
        aggregate_names = v.read(v.WORK / ('targeted-' + tag) / 'discovery.json')['names']
        assert names and set(names) <= set(aggregate_names)
        v.save(destination / 'r3-aggregate-coverage.json', {'r3Tests': names, 'coveredByFinalR4Aggregate': True})
    stage('backend-truth', backend_truth)

    def regressions():
        cmake = pathlib.Path('C:/Program Files/Microsoft Visual Studio/18/Community/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe')
        native = work / 'native-regressions'
        v.checked([cmake, '--build', native, '--config', 'Release', '--', '/m:1', '/v:minimal'], destination / 'regression-build')
        exe = native / 'Release/HevcNativeRegressions.exe'
        v.checked([exe, 'order-regress'], destination / 'coded-order')
        v.checked([exe, 'regress', v.ROOT / '.ai-tmp/cache/samples/苹果双文件.MOV',
            v.WORK / 'backend-comparison/qualify-libav-exact-h264-mp4/outputs/3.mp4'], destination / 'mebx-static-sei')
        v.save(destination / 'regression-runtime.json', v.identity(exe))
    stage('native-regressions', regressions)

    def cli():
        out = work / ('cli-' + tag)
        v.checked([v.helper.DOTNET, 'build', 'tests/LivePhotoBox.CLI.Tests/LivePhotoBox.CLI.Tests.csproj', '-c', 'Release',
            '-p:Platform=x64', '-p:SkipNativeBuild=true', '-p:NativeArtifactsDir=' + str(v.NATIVE / 'Release/win-x64'),
            '-p:OutDir=' + str(out), '--verbosity', 'minimal'], destination / 'cli-build')
        for name in ('LivePhotoBox.Core.dll', 'LivePhotoBox.Core.pdb', 'LivePhotoBox.Native.dll', 'LivePhotoBox.Video.Libav.dll'):
            shutil.copyfile(v.WORK / ('driver-' + tag) / name, out / name)
        temp = v.WORK / 't' / ('cli-' + tag)
        temp.mkdir(parents=True, exist_ok=False)
        v.checked([v.helper.DOTNET, 'vstest', out / 'LivePhotoBox.CLI.Tests.dll',
            '--TestCaseFilter:FullyQualifiedName~ConvertCommandTests|FullyQualifiedName~P7R2ConvertPublicationTests',
            '--logger:trx;LogFileName=cli.trx', '--ResultsDirectory:' + str(destination / 'cli-results')], destination / 'cli-execute',
            {'LIVEPHOTOBOX_TEST_SAMPLES_DIR': str(v.ROOT / '.ai-tmp/cache/samples'), 'LPB_R2_EVIDENCE': str(destination / 'cli-evidence'),
             'TEMP': str(temp), 'TMP': str(temp)})
        assert not any(p.is_file() for p in temp.rglob('*'))
        v.save(destination / 'cli-runtime.json', [v.identity(p) for p in sorted(out.rglob('*')) if p.is_file()])
    stage('cli', cli)
    stage('large-supported', lambda: _success(v.driver(tag, 'large', 'large-supported-' + tag)))
    stage('large-constrained', lambda: _success(v.constrained(tag, 'large-memory-' + tag)))
    stage('qualification', lambda: _success(v.driver(tag, 'qualify', 'qualification-' + tag)))
    v.save(destination / 'qualified.json', {'passed': True, 'stages': stages, 'sources': before,
        'driver': v.identity(v.WORK / ('driver-' + tag + '.json'))})
    stage('soak', lambda: _success(v.driver(tag, 'soak', 'soak-' + tag)))
    v.save(destination / 'completed.json', {'passedOperationalAssertions': True, 'stages': stages,
        'notAcceptance': 'Independent output/resource assessment, formal create-only evidence and fresh review are still required.'})


def _success(code):
    assert code == 0, 'Direct child failed: ' + str(code)


if __name__ == '__main__':
    run(*sys.argv[1:])
