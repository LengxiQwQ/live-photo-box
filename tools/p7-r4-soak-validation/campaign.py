"""Bind, analyze and archive the finite R4 campaign without changing acceptance."""
import datetime, json, pathlib, shutil, statistics, sys
import validate as v
DRIVER='v19'  # Clean repair3 recovery runtime, not an accepted final candidate.
NATIVE='v3-repair3'
SOAK='UNSET_FINAL_RESOURCE_CANDIDATE'

def verify_items(items,base=None):
    for item in items:
        p=(base/item['path']) if base else v.ROOT/item['path']
        assert p.stat().st_size==item['bytes'] and v.hashfile(p)==item['sha256'],str(p)

def history():
    rows=[]
    for name,run in [('P7-R1-malformed-mutation-collision',28),('P7-R2-transaction-filesystem',3),('P7-R3-backend-truth',1)]:
        path=v.ROOT/'.ai-tmp/evidence/P7'/name/('run-'+str(run))/'evidence-index.json'
        data=v.read(path)
        for item in data['payloads']:
            p=v.ROOT/item['path'] if item['path'].startswith('docs/') else path.parent/item['path']
            assert p.stat().st_size==item['bytes'] and v.hashfile(p)==item['sha256'],str(p)
        rows.append(dict(index=v.identity(path),payloadsVerified=len(data['payloads']),mismatches=[]))
    v.save(v.WORK/'history-verified.json',dict(passed=True,predecessors=rows))

def bind(tag):
    driver=v.read(v.WORK/('driver-'+DRIVER+'.json'));tests=v.read(v.WORK/('tests-'+tag+'.json'));native=v.read(v.WORK/('native-'+NATIVE+'.json'))
    verify_items(driver['runtime']);verify_items(tests['runtime']);verify_items(native['artifacts']);verify_items(native['sources'])
    product=[s for s in driver['sources'] if s['path'].startswith(('LivePhotoBox.Core/','LivePhotoBox.Native/'))]
    verify_items(product)
    baseline=v.read(v.WORK/'baseline.json');current=v.sources();old={x['path']:x for x in baseline['sources']}
    delta=[dict(before=old.get(x['path']),after=x) for x in current if x!=old.get(x['path'])]
    references=[v.identity(v.ROOT/p) for p in ['LivePhotoBox/ViewModels/EditViewModel.cs','LivePhotoBox/Services/EditExportService.cs','tests/fixtures/realsamples-manifest.json','CMakeLists.txt']]
    v.save(v.WORK/('candidate-'+tag+'.json'),dict(head=v.helper.git('rev-parse','HEAD'),branch=v.helper.git('branch','--show-current'),
        sources=current,readOnlySourceReferences=references,driverRuntime=driver['runtime'],testRuntime=tests['runtime'],nativeArtifacts=native['artifacts'],
        productSourcesUsedBySoak=product,sourceDeltaFromR4Baseline=delta,samples=v.helper.samples(),
        applicability='Final soak driver '+DRIVER+' product source/runtime unchanged. Final aggregate deploys exact soak Core DLL/PDB and identical Native/sidecar; preserved older campaign is diagnostic history, not final resource acceptance.'))

def metrics(label):
    root=v.WORK/label/'case';mixed=v.read(root/'mixed.json')
    rounds=[v.read(p) for p in sorted(root.glob('r[0-9]*.json'))]
    measured=[r for r in rounds if not r['warmup']]
    assert mixed['passed'] and mixed['measuredSeconds']>=1800 and len(measured)==mixed['measuredEpochs']>=12
    assert len([r for r in rounds if r['warmup']])==3
    assert all(len(r['neutral'])==14 for r in rounds)
    metrics=['privateBytes','workingSet','managedBytes','handles','threads','semaphores','poolThreads','activeWorkers','pendingWorkItems']
    series=[json.loads(line) for line in (root/'resources.jsonl').read_text().splitlines()]
    windows=[]
    for n in range(4):
        part=measured[len(measured)*n//4:len(measured)*(n+1)//4]
        windows.append(dict(quarter=n+1,rounds=[r['round'] for r in part],statistics={key:dict(
            minimum=min(r['natural'][key] for r in part),median=statistics.median(r['natural'][key] for r in part),maximum=max(r['natural'][key] for r in part)) for key in metrics}))
    temp=v.WORK/'t'/label;files=[v.identity(p) for p in temp.rglob('*') if p.is_file()]
    directories=[str(p.relative_to(temp)) for p in temp.rglob('*') if p.is_dir()]
    assert not files and not any(pathlib.Path(d).name.startswith('ws-') for d in directories)
    assert mixed['cancellation']['cancelled'] and mixed['cancellation']['workspaceRemoved']
    pause=v.read(root/'post-load-pause/pause.json');assert pause['passed'] and pause['cancelled']==12 and pause['eventsStillReset']
    value=dict(passedWorkloadAssertions=True,measuredEpochs=len(measured),warmupEpochs=3,measuredSeconds=mixed['measuredSeconds'],
        explicitMeasuredRequests=dict(neutral=14*len(measured),mergePairs=4*len(measured),splitInputs=3*len(measured),imageConversions=2*len(measured),
            videoConversions=sum(sum(name in row['codecs'] for name in ('sdr','hdr','hevc8','mf')) for row in measured)),
        resourceSampleCount=len(series),samplePeaks={key:max(row[key] for row in series) for key in metrics},
        naturalQuarterWindows=windows,finalNatural=mixed['finalNatural'],finalDiagnosticGc=mixed['finalDiagnosticGc'],
        firstMeasured=measured[0]['natural'],lastMeasured=measured[-1]['natural'],
        tempFiles=files,tempDirectories=directories,
        resourceInterpretation='Statistics are observations; independent review must assess retention/runaway. No arbitrary performance SLA or forced-GC-only acceptance.')
    v.save(v.WORK/('analysis-'+label+'.json'),value)
    print(json.dumps({k:value[k] for k in ['measuredEpochs','measuredSeconds','resourceSampleCount','samplePeaks','naturalQuarterWindows']}))

def seal(tag):
    candidate=v.read(v.WORK/('candidate-'+tag+'.json'))
    verify_items(candidate['sources']);verify_items(candidate['driverRuntime']);verify_items(candidate['testRuntime'])
    aggregate=v.WORK/('targeted-'+tag)/'summary.json'
    assert v.read(aggregate)['passed']
    assert v.read(v.WORK/('independent-'+SOAK)/'summary.json')['passed']
    assert v.read(v.WORK/('analysis-'+SOAK+'.json'))['passedWorkloadAssertions']
    # Workload correctness alone did pass in two resource-failing campaigns.
    # An explicit resource assessment is required before a success snapshot.
    resource_verdict = v.read(v.WORK/('resource-verdict-'+SOAK+'.json'))
    assert resource_verdict['passed'] is True and resource_verdict['soak'] == SOAK
    parent=v.ROOT/'.ai-tmp/evidence/P7/P7-R4-large-batch-soak'
    number=max([int(p.name[4:]) for p in parent.glob('run-*') if p.name[4:].isdigit()]+[0])+1
    run=parent/('run-'+str(number));run.mkdir(parents=True,exist_ok=False)
    payloads=[]
    def copy(source,relative):
        target=run/relative;target.parent.mkdir(parents=True,exist_ok=True)
        with source.open('rb') as src,target.open('xb') as dst:shutil.copyfileobj(src,dst)
        identity=v.identity(target);original=v.identity(source);assert identity['sha256']==original['sha256']
        payloads.append(dict(path=target.relative_to(run).as_posix(),bytes=identity['bytes'],sha256=identity['sha256'],temporarySource=original))
    directories=['environment','configure-v1','LivePhotoBoxNative-v1','LivePhotoBoxNativeTestHarness-v1','source-before-R4',
        'pause-old','pause-fixed','prepare-v3','q3','qualification-v4','qualification-v5','soak-v6','large-memory-old','large-memory-fixed','large-memory-repeat-v6','large-supported-v5',
        'build-driver-v4','build-driver-v5','build-driver-v6','build-tests-v6','build-tests-v7','build-tests-v8',
        'targeted-v6','targeted-v7','targeted-v8','targeted-v9','targeted-v12','targeted-v13','independent-qualification-v5','independent-qualification-v5b','independent-qualification-v5c',
        'mf-isolation-old','mf-isolation-fixed','diagnostics','soak-v11','independent-soak-v11','large-supported-v11','large-memory-repeat-v11','build-driver-v10','build-driver-v11',
        'native-v2-build-work/configure-v2','native-v2-build-work/LivePhotoBoxNative-v2','native-v2-build-work/LivePhotoBoxNativeTestHarness-v2']
    directories=list(dict.fromkeys(directories+['targeted-'+tag]))
    for name in directories:
        for path in sorted((v.WORK/name).rglob('*')):
            if path.is_file():copy(path,pathlib.Path('raw')/path.relative_to(v.WORK))
    for path in sorted(v.WORK.glob('*.json')):copy(path,pathlib.Path('bindings')/path.name)
    for path in sorted((v.WORK/'fixtures').glob('*.json')):copy(path,pathlib.Path('fixtures')/path.name)
    for item in candidate['sources']+candidate['readOnlySourceReferences']:copy(v.ROOT/item['path'],pathlib.Path('source')/item['path'])
    for category in ['driverRuntime','testRuntime']:
        for item in candidate[category]:copy(v.ROOT/item['path'],pathlib.Path('runtime')/pathlib.Path(item['path']).relative_to(v.WORK.relative_to(v.ROOT)))
    for tag in ('v6','v10'):
        for item in v.read(v.WORK/('driver-'+tag+'.json'))['runtime']:
            copy(v.ROOT/item['path'],pathlib.Path('diagnostic-runtime')/pathlib.Path(item['path']).relative_to(v.WORK.relative_to(v.ROOT)))
    for item in v.read(v.WORK/'native-v1.json')['artifacts']:
        copy(v.ROOT/item['path'],pathlib.Path('diagnostic-native-v1')/pathlib.Path(item['path']).relative_to((v.WORK/'native-artifacts').relative_to(v.ROOT)))
    for item in candidate['nativeArtifacts']:
        copy(v.ROOT/item['path'],pathlib.Path('native-build-artifacts')/pathlib.Path(item['path']).relative_to(v.WORK.relative_to(v.ROOT)))
    for tag in ('v1','v2'):
        cache=v.read(v.WORK/('native-'+tag+'.json'))['cache']
        copy(v.ROOT/cache['path'],pathlib.Path('configuration')/('native-'+tag+'-CMakeCache.txt'))
    context=v.ROOT/'docs/LivePhotoBox-Current-Roadmap/LivePhotoBox-P7-细分路线图'
    for name in ('03-P7-剩余执行路线图与执行规则-v2.md','P7-R4-Local-Execution-Plan.md','P7-R4-coverage-matrix.md'):
        directory = (v.ROOT/'.ai-tmp/evidence/P7/P7-R4-large-batch-soak/documentation'
                     if name.startswith('P7-R4-') else context)
        copy(directory/name,pathlib.Path('context')/name)
    for relative in ['LivePhotoBox.Core/Services/LivePhotoMergeRunnerService.cs','LivePhotoBox.Native/src/media/media_inspector.cpp','LivePhotoBox.Native/src/media/video_converter.cpp']:
        source=v.ROOT/'.ai-tmp/evidence/P7/P7-R3-backend-truth/run-1/source'/relative
        copy(source,pathlib.Path('source-before-R4')/relative)
    index=dict(schema=1,status='COMPLETE_PENDING_FRESH_INDEPENDENT_REVIEW',createdUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
        phase='P7',task='P7-R4-large-batch-soak',run=number,head=candidate['head'],branch=candidate['branch'],
        roadmapRevision='317465937A08A908F318ED0F1005AFF7D5B41E637DFF75276F7C08732BB859A7',candidate=v.identity(v.WORK/('candidate-'+tag+'.json')),
        aggregate=v.identity(aggregate),soak=v.identity(v.WORK/('analysis-'+SOAK+'.json')),independent=v.identity(v.WORK/('independent-'+SOAK)/'summary.json'),
        payloads=payloads,stop='After R4 before R5/P8; no publication or whole-product release conclusion.')
    v.save(run/'evidence-index.json',index);print(json.dumps(dict(index=v.identity(run/'evidence-index.json'),payloads=len(payloads)),ensure_ascii=True))

if __name__=='__main__':
    mode=sys.argv[1]
    if mode=='history':history()
    elif mode=='bind':bind(sys.argv[2])
    elif mode=='metrics':metrics(sys.argv[2])
    elif mode=='seal':seal(sys.argv[2])
