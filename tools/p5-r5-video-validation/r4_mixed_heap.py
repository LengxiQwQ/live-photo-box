"""Offline full-mixed heap accounting on an unchanged existing product runtime."""
import pathlib
import sys
import r4_closeout as c

v = c.v


def run(tag='v62', minutes='5'):
    root = c.H / ('mixed-heap-' + tag)
    runtime = root / 'runtime'
    driver = v.WORK / ('driver-' + tag)
    source = v.ROOT / 'tools/p5-r5-video-validation/r4-mixed-heap-probe'
    sources = [v.identity(p) for p in sorted(source.glob('*')) if p.is_file()]
    sources += [v.identity(v.ROOT / ('tools/p5-r5-video-validation/r4-sampler-probe/' + n))
                for n in ('HeapStats.cs', 'VirtualStats.cs')]
    sources.append(v.identity(pathlib.Path(__file__)))
    product = [v.identity(p) for p in sorted(driver.rglob('*')) if p.is_file()]
    record = v.read(v.WORK / ('driver-' + tag + '.json'))
    assert product == record['runtime']
    campaign_sources = v.sources()
    assert campaign_sources == record['sources']
    v.save(root / 'identity-before.json', dict(productRuntime=product, probeSources=sources,
        campaignSources=campaign_sources, diagnosticOnly=True,
        limitations='Intrusive HeapWalk; existing Mixed contains labeled diagnostic GC outside measured interval. Not acceptance.'))
    build = v.command([v.helper.DOTNET, 'build', source / 'MixedHeapProbe.csproj',
        '-c', 'Release', '-p:OutDir=' + str(runtime), '--verbosity', 'minimal'], root / 'build', timeout=600)
    assert build['directChildExitCode'] == 0 and not build.get('timedOut')
    c.verify(sources + product)
    probe = [v.identity(p) for p in sorted(runtime.rglob('*')) if p.is_file()]
    v.save(root / 'runtime.json', probe)
    args = [v.helper.DOTNET, runtime / 'MixedHeapProbe.dll', driver / 'Campaign.dll',
            v.ROOT / '.ai-tmp/cache/samples']
    temp = root / 'temp'
    temp.mkdir(exist_ok=False)
    env = {'TEMP': str(temp), 'TMP': str(temp)}
    qualification = v.command(args + [root / 'qualification', '0', 'qualify-only'],
                              root / 'execute-qualification', env, timeout=120)
    assert qualification['directChildExitCode'] == 0 and not qualification.get('timedOut')
    assert v.read(root / 'qualification/qualification.json')['passed']
    result = v.command(args + [root / 'diagnosis', minutes], root / 'execute', env, timeout=3600)
    c.verify(sources + product + probe)
    assert campaign_sources == v.sources()
    v.save(root / 'identity-after.json', dict(unchanged=True, process=result,
        tempFiles=[v.identity(p) for p in temp.rglob('*') if p.is_file()], diagnosticOnly=True))
    assert result['directChildExitCode'] == 0 and not result.get('timedOut')
    print(result)


if __name__ == '__main__':
    run(*sys.argv[1:])
