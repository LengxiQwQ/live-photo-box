"""Windows-only offline R3 capture. No production policy/environment switches."""
import datetime, hashlib, json, os, pathlib, shutil, subprocess, sys
import xml.etree.ElementTree as ET
import crosscheck
ROOT = pathlib.Path(__file__).resolve().parents[2]
WORK = ROOT / '.ai-tmp/workspace/P7/R3-backend-truth'
NATIVE = WORK / 'native-artifacts'
DOTNET = shutil.which('dotnet')
def read(p): return json.loads(pathlib.Path(p).read_text(encoding='utf-8-sig'))
def hashfile(p):
    with open(p,'rb') as f: return hashlib.file_digest(f,'sha256').hexdigest().upper()
def identity(p):
    p=pathlib.Path(p); return dict(path=p.relative_to(ROOT).as_posix() if p.is_relative_to(ROOT) else str(p), bytes=p.stat().st_size,sha256=hashfile(p))
def save(p,v):
    p=pathlib.Path(p);p.parent.mkdir(parents=True,exist_ok=True)
    with p.open('x',encoding='utf-8') as f:json.dump(v,f,ensure_ascii=False,indent=2);f.write('\n')
def git(*args): return subprocess.check_output(['git',*args],cwd=ROOT).decode('utf-8').strip()
def sources():
    names=git('ls-files','-c','-o','--exclude-standard').splitlines()
    return [identity(ROOT/n) for n in sorted(set(names)) if n.startswith(('LivePhotoBox.Core/','LivePhotoBox.Native/','tests/LivePhotoBox.Core.Tests/','tools/p7-r3-backend-validation/')) and (ROOT/n).is_file() and not '/obj/' in n and not '/bin/' in n and not '__pycache__' in n]
def samples():
    rows=[dict(expected=s,actual=identity(ROOT/'.ai-tmp/cache/samples'/s['filename'])) for s in read(ROOT/'tests/fixtures/realsamples-manifest.json')['samples']]
    assert all(r['actual']['bytes']==r['expected']['byteSize'] and r['actual']['sha256']==r['expected']['sha256'].upper() for r in rows)
    return rows
def command(args,dest,env=None,timeout=1800):
    dest=pathlib.Path(dest);dest.mkdir(parents=True,exist_ok=False)
    overrides=env or {}; childenv=os.environ.copy();childenv.update(overrides)
    record=dict(arguments=list(map(str,args)),workingDirectory=str(ROOT),environmentOverrides=overrides,startedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),directChildExitCode=None)
    with (dest/'stdout.txt').open('xb') as out,(dest/'stderr.txt').open('xb') as err:
        try:
            p=subprocess.Popen(list(map(str,args)),cwd=ROOT,env=childenv,stdout=out,stderr=err)
            record['childPid']=p.pid
            try:record['directChildExitCode']=p.wait(timeout=timeout)
            except subprocess.TimeoutExpired:p.kill();p.wait();record.update(timedOut=True,directChildExitCode=p.returncode)
        except OSError as e:record['launchError']=str(e)
    record['finishedUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
    save(dest/'process.json',record)
    return record
def run_checked(args,dest,**kw):
    r=command(args,dest,**kw);assert r['directChildExitCode']==0, str(dest);return r
def native(tag):
    config=read(ROOT/'.ai-tmp/workspace/P7/R1-malformed-mutation-collision/plan135-r1d-msys2-runtime-qualified/22b-plan135-native-configure-result.json')
    cmake=config['executable'];assert hashfile(cmake)==config['executableSha256']
    args=config['arguments'][:];args[args.index('-B')+1]=str(WORK/'native-build')
    args=[('-DLPB_NATIVE_OUTPUT_ROOT='+str(NATIVE)) if x.startswith('-DLPB_NATIVE_OUTPUT_ROOT=') else x for x in args]
    before=sources()
    if not (WORK/'native-build/CMakeCache.txt').exists():run_checked([cmake]+args,WORK/('configure-'+tag),env=config['environment'])
    for target in ('LivePhotoBoxNative','LivePhotoBoxNativeTestHarness'):
        run_checked([cmake,'--build',WORK/'native-build','--config','Release','--target',target,'--','/m:1','/p:CL_MPCount=1','/p:UseMultiToolTask=false','/v:minimal'],WORK/(target+'-'+tag))
    assert sources()==before
    save(WORK/('native-'+tag+'.json'),dict(sources=before,cache=identity(WORK/'native-build/CMakeCache.txt'),artifacts=[identity(p) for p in sorted(NATIVE.rglob('*')) if p.is_file()]))
def build(tag, core_runtime=None):
    before=sources();out=WORK/('probe-'+tag)
    run_checked([DOTNET,'build','tools/p7-r3-backend-validation/BackendProbe.csproj','-c','Release','-p:SkipNativeBuild=true','-p:NativeArtifactsDir='+str(NATIVE/'Release/win-x64'),'-p:OutDir='+str(out),'--verbosity','minimal'],WORK/('build-probe-'+tag))
    shutil.copyfile(NATIVE/'Release/win-x64/LivePhotoBox.Video.Libav.dll',out/'LivePhotoBox.Video.Libav.dll')
    tests=WORK/('tests-'+tag)
    run_checked([DOTNET,'build','tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj','-c','Release','-p:Platform=x64','-p:RuntimeIdentifier=win-x64','-p:SkipNativeBuild=true','-p:NativeArtifactsDir='+str(NATIVE/'Release/win-x64'),'-p:OutDir='+str(tests),'--verbosity','minimal'],WORK/('build-tests-'+tag))
    for name in ('LivePhotoBox.Video.Libav.dll','LivePhotoBox.Native.TestHarness.dll'):
        src=NATIVE/('TestHarness/Release/win-x64' if 'TestHarness' in name else 'Release/win-x64')/name
        shutil.copyfile(src,tests/name)
    if core_runtime is not None:
        for destination in (out, tests):
            for name in ('LivePhotoBox.Core.dll','LivePhotoBox.Core.pdb','LivePhotoBox.Native.dll','LivePhotoBox.Video.Libav.dll'):
                shutil.copyfile(pathlib.Path(core_runtime)/name,destination/name)
    assert sources()==before
    save(WORK/('candidate-'+tag+'.json'),dict(sources=before,probeRuntime=[identity(p) for p in sorted(out.rglob('*')) if p.is_file()],testRuntime=[identity(p) for p in sorted(tests.rglob('*')) if p.is_file()]))
def qualify(tag):
    c=read(WORK/('candidate-'+tag+'.json'));assert c['sources']==sources(), 'Candidate source drift'
    for item in c['probeRuntime']+c['testRuntime']:assert identity(ROOT/item['path'])==item
    samples()
    return c
def selector():
    cls=['VideoConverterTests','ImageConverterTests','ImageColorHighBitDepthTests','ConverterContractTests']
    return '|'.join('FullyQualifiedName~LivePhotoBox.Core.Tests.Media.'+c for c in cls)+'|FullyQualifiedName~LivePhotoBox.Core.Tests.NativeRuntimeTests.Probe_ReportsTruthfulCapabilityIdentity|FullyQualifiedName~LivePhotoBox.Core.Tests.NativeRuntimeTests.Probe_LoadsMatchingNativeRuntime'
def tests(tag,label):
    qualify(tag)
    dest=WORK/label; assembly=WORK/('tests-'+tag)/'LivePhotoBox.Core.Tests.dll'
    d=run_checked([DOTNET,'vstest',assembly,'--ListTests','--TestCaseFilter:'+selector()],dest/'discovery')
    names=[line.strip() for line in (dest/'discovery/stdout.txt').read_text(encoding='utf-8-sig').splitlines() if line.strip().startswith('LivePhotoBox.Core.Tests.')]
    assert names and len(names)==len(set(names));save(dest/'discovery.json',dict(selector=selector(),names=names,total=len(names)))
    temp=WORK/'t'/label;temp.mkdir(parents=True,exist_ok=False)
    env={'LIVEPHOTOBOX_TEST_SAMPLES_DIR':str(ROOT/'.ai-tmp/cache/samples'),'TEMP':str(temp),'TMP':str(temp)}
    before=sources();r=command([DOTNET,'vstest',assembly,'--TestCaseFilter:'+selector(),'--logger:trx;LogFileName=r3.trx','--ResultsDirectory:'+str(dest/'results')],dest/'execute',env)
    tree=ET.parse(dest/'results/r3.trx').getroot();ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    rows=[dict(name=x.attrib['testName'],outcome=x.attrib['outcome']) for x in tree.findall('t:Results/t:UnitTestResult',ns)]
    actual=[x['name'] for x in rows];c=tree.find('t:ResultSummary/t:Counters',ns).attrib
    v=dict(counters=c,rows=rows,missing=sorted(set(names)-set(actual)),unexpected=sorted(set(actual)-set(names)),sourcesUnchanged=before==sources(),directChildExitCode=r['directChildExitCode'],tempFiles=[str(p) for p in temp.rglob('*') if p.is_file()])
    v['passed']=not v['missing'] and not v['unexpected'] and all(x['outcome']=='Passed' for x in rows) and len(actual)==len(names) and r['directChildExitCode']==0 and v['sourcesUnchanged'] and not v['tempFiles']
    save(dest/'summary.json',v);print(json.dumps({k:v for k,v in v.items() if k not in ('rows','tempFiles')},ensure_ascii=True));assert v['passed']
    qualify(tag)
def probes(tag,label):
    candidate=qualify(tag)
    base=WORK/('probe-'+tag);dest=WORK/label
    sample=ROOT/'.ai-tmp/cache/samples'
    hdr=ROOT/'.ai-tmp/workspace/P5-R5/huawei-extraction-pass-c/motion.mp4'
    assert hashfile(hdr)=='664EF6DBA25D7B04228C79B874A0EDA742B1211E17EE770D0E82E765BD454CE6'
    cases=[('remux','remux',sample/'苹果双文件.MOV','full',True),('hevc-to-h264','sdr',sample/'苹果双文件.MOV','full',True),('h264-to-hevc','sdr-hevc',sample/'vivo双文件.mp4','full',True),('hdr','hdr',hdr,'full',True),('jpeg-copy','image-copy',sample/'苹果-双文件.JPG','full',True),('jpeg-heic','image-heic',sample/'苹果-双文件.JPG','full',True)]
    for variant in ('absent-sidecar','invalid-sidecar','absent-native','invalid-native'):
        cases.append((variant,'hdr' if 'sidecar' in variant else 'sdr',hdr if 'sidecar' in variant else sample/'苹果双文件.MOV',variant,False))
        if 'sidecar' in variant: cases.append((variant+'-sdr','sdr',sample/'苹果双文件.MOV',variant,False))
        else:
            cases.append((variant+'-image','image-copy',sample/'苹果-双文件.JPG',variant,False))
            cases.append((variant+'-image-trusted','image-trusted',sample/'苹果-双文件.JPG',variant,False))
    for variant in sorted(set(c[3] for c in cases)):
        deploy=dest/'deploy'/variant
        shutil.copytree(base,deploy,ignore=shutil.ignore_patterns('LivePhotoBox.Native.dll' if 'native' in variant else 'LivePhotoBox.Video.Libav.dll') if variant!='full' else None)
        if variant.startswith('invalid-'):
            name='LivePhotoBox.Native.dll' if 'native' in variant else 'LivePhotoBox.Video.Libav.dll'
            (deploy/name).write_bytes(b'R3 deliberately invalid PE deployment fixture\n')
    expected_truth={
        'remux':('ProjectIsoBmffRemux','VideoRemux','ContainerRemux','NotApplicable'),
        'sdr':('minimal-libav','VideoTranscodeSdr','LossyReencode','SoftwareForced'),
        'sdr-hevc':('minimal-libav','VideoTranscodeSdr','LossyReencode','SoftwareForced'),
        'hdr':('minimal-libav','VideoTranscodeHdr10Bit','LossyReencode','SoftwareForced'),
        'image-copy':('LivePhotoBox.Native','ImageCodec','Passthrough','NotApplicable'),
        'image-heic':('LivePhotoBox.Native','ImageCodec','LossyReencode','NotApplicable')}
    summary=[]
    for name,mode,src,variant,expected in cases:
        case=dest/'cases'/name;temp=WORK/'p'/label/name;temp.mkdir(parents=True,exist_ok=False)
        run_checked([DOTNET,dest/'deploy'/variant/'BackendProbe.dll',mode,src,case],dest/'processes'/name,env={'TEMP':str(temp),'TMP':str(temp)})
        p=read(case/'probe.json');r=p['Result'];truth=r['ExecutionRecord']['Truth']
        assert r['Success']==expected,(name,r)
        assert p['SourceBefore']==p['SourceAfter'] and p['WorkspaceRemoved']
        residue=[identity(x) for x in temp.rglob('*') if x.is_file()]
        save(case/'temp-postflight.json',dict(files=residue));assert not residue
        assert not truth['FallbackOccurred'] and not truth['FallbackAllowed']
        if not expected:
            assert r['OutputArtifact'] is None and not p['Outputs']
            assert truth['FailureCategory']=='BackendUnavailable' and truth['FailureStage']=='BackendUnavailable',(name,truth)
            assert truth['ActualCapability']=='Unknown'
            if 'sidecar' in variant:
                cap=next(c for c in p['Runtime']['CapabilityIdentities'] if c['Operation']=='VideoTranscodeHdr10Bit')
                assert not cap['IsAvailable'] and truth['BackendName']=='minimal-libav'
                assert not any(m['ModuleName']=='LivePhotoBox.Video.Libav.dll' for m in p['LoadedModules'])
        else:
            assert r['OutputArtifact'] is not None and len(p['Outputs'])==1
            assert truth['FailureCategory']=='None' and truth['FailureStage']=='None'
            assert truth['ActualCapability']==truth['SelectedCapability']
            backend,capability,operation,hardware=expected_truth[mode]
            assert (truth['BackendName'],truth['ActualCapability'],truth['ActualOperationKind'],truth['HardwareMode'])==(backend,capability,operation,hardware),(name,truth)
            if mode=='hdr':
                assert truth['BackendVersion']=='9.0.1'
                assert truth['InputProfile']==truth['OutputProfile']=='HEVC;lumaDepth=10;chromaDepth=10;CICP=9/18/9;range=full'
                assert r['ExecutionRecord']['SelectedEncoder'].startswith('minimal-libav/P7-R4-HEVC-v2/')
            if mode in ('sdr','sdr-hevc'):
                assert r['ExecutionRecord']['SelectedEncoder'].startswith('minimal-libav/P7-R4-HEVC-v2/')
                assert r['ExecutionRecord']['HardwareMode']=='SoftwareForced'
            for out in p['Outputs']:
                path=pathlib.Path(out['Path']);info=dest/'independent'/name
                assert out['Bytes']>0 and out['Bytes']==r['OutputArtifact']['ByteLength']
                assert out['Sha256']==r['OutputArtifact']['Sha256']==hashfile(path)
                crosscheck.verify(sys.modules[__name__],name,mode,src,path,info,truth)
        summary.append(dict(name=name,expectedSuccess=expected,passed=True,probe=identity(case/'probe.json')))
    save(dest/'summary.json',dict(passed=True,cases=summary))
    qualify(tag)
if __name__=='__main__':
    mode=sys.argv[1]
    if mode=='baseline':save(WORK/'baseline.json',dict(head=git('rev-parse','HEAD'),branch=git('branch','--show-current'),status=git('status','--porcelain=v1'),sources=sources(),samples=samples()))
    elif mode=='native':native(sys.argv[2])
    elif mode=='build':build(sys.argv[2])
    elif mode=='tests':tests(*sys.argv[2:4])
    elif mode=='probes':probes(*sys.argv[2:4])
