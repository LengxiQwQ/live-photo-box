"""R4 local Windows campaign capture; shared R3 helpers are offline only."""
import pathlib, sys, shutil, json, importlib.util, xml.etree.ElementTree as ET
ROOT=pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'tools/p7-r3-backend-validation'))
spec=importlib.util.spec_from_file_location('r3_offline_capture',ROOT/'tools/p7-r3-backend-validation/validate.py')
helper=importlib.util.module_from_spec(spec);sys.modules[spec.name]=helper;spec.loader.exec_module(helper)
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parent))
import job
WORK=ROOT/'.ai-tmp/workspace/P7/R4-large-batch-soak'
NATIVE=ROOT/'artifacts/native'
# HEVC reopen candidate; acceptance requires the bound aggregate and soak.
FINAL_DRIVER='v60'
read,save,identity,hashfile,command=helper.read,helper.save,helper.identity,helper.hashfile,helper.command
def checked(args,dest,env=None):
    r=command(args,dest,env);assert r['directChildExitCode']==0,str(dest);return r
def sources():
    names=helper.git('ls-files','-c','-o','--exclude-standard').splitlines()
    return [identity(ROOT/n) for n in sorted(set(names)) if n.startswith(('LivePhotoBox.Core/','LivePhotoBox.Native/','tests/LivePhotoBox.Core.Tests/','tools/p7-r3-backend-validation/','tools/p7-r4-soak-validation/')) and (ROOT/n).is_file() and not any(s in n for s in ('/obj/','/bin/','__pycache__'))]
def pause(tag,label):
    dest=WORK/label;temp=WORK/'t'/label;temp.mkdir(parents=True,exist_ok=False)
    r=command([helper.DOTNET,WORK/('driver-'+tag)/'Campaign.dll','pause',dest/'case'],dest/'process',{'TEMP':str(temp),'TMP':str(temp)})
    save(dest/'summary.json',dict(process=r,case=read(dest/'case/pause.json'),tempFiles=[identity(p) for p in temp.rglob('*') if p.is_file()]))
    print(json.dumps(read(dest/'case/pause.json'),ensure_ascii=True))
    return r['directChildExitCode']
def driver(tag,mode,label,extra=None):
    dest=WORK/label;temp=WORK/'t'/label;temp.mkdir(parents=True,exist_ok=False)
    samples=ROOT/'.ai-tmp/cache/samples'
    args=[helper.DOTNET,WORK/('driver-'+tag)/'Campaign.dll',mode,samples]
    if mode=='prepare':args.append(WORK/'fixtures')
    elif mode=='large':args.extend([WORK/'fixtures/supported.mov',dest/'case'])
    else:args.append(dest/'case')
    if extra:args.extend(extra)
    r=command(args,dest/'process',{'TEMP':str(temp),'TMP':str(temp)},timeout=5400)
    print(json.dumps(r,ensure_ascii=True));return r['directChildExitCode']
def constrained(tag,label):
    dest=WORK/label;case=dest/'case';temp=WORK/'t'/label
    args=[helper.DOTNET,WORK/('driver-'+tag)/'Campaign.dll','inspect',ROOT/'.ai-tmp/cache/samples',WORK/'fixtures/over4g.mov',case]
    r=job.run(sys.modules[__name__],args,dest/'process',case,temp)
    result=dict(process=r,inspection=read(case/'inspection.json') if (case/'inspection.json').exists() else None,
        tempFiles=[identity(p) for p in temp.rglob('*') if p.is_file()])
    save(dest/'summary.json',result);print(json.dumps(result,ensure_ascii=True));return r['directChildExitCode']
def native(tag,attempt=None):
    helper.WORK=WORK if tag=='v1' else WORK/('native-'+tag+'-build-work')
    helper.NATIVE=WORK/('native-artifacts' if tag=='v1' else 'native-artifacts-'+tag)
    helper.sources=lambda: [identity(p) for p in sorted((ROOT/'LivePhotoBox.Native').rglob('*'))
        if p.is_file() and p.suffix in ('.cpp','.h','.txt','.json','.cmake','.vcxproj','.filters')]+[identity(ROOT/'CMakeLists.txt')]
    build_tag=tag+('-'+attempt if attempt else '')
    helper.native(build_tag)
    if tag!='v1':save(WORK/('native-'+build_tag+'.json'),read(helper.WORK/('native-'+build_tag+'.json')))
def build(tag):
    before=sources();out=WORK/('driver-'+tag);native=NATIVE/'Release/win-x64'
    checked([helper.DOTNET,'build','tools/p7-r4-soak-validation/Campaign.csproj','-c','Release','-p:SkipNativeBuild=true','-p:NativeArtifactsDir='+str(native),'-p:OutDir='+str(out),'--verbosity','minimal'],WORK/('build-driver-'+tag))
    shutil.copyfile(native/'LivePhotoBox.Video.Libav.dll',out/'LivePhotoBox.Video.Libav.dll')
    assert before==sources()
    save(WORK/('driver-'+tag+'.json'),dict(sources=before,runtime=[identity(p) for p in sorted(out.rglob('*')) if p.is_file()]))
def tests(tag,label,deploy_from=None):
    before=sources();out=WORK/('tests-'+tag);native=NATIVE
    if deploy_from:
        shutil.copytree(WORK/('tests-'+deploy_from),out)
    else:
        checked([helper.DOTNET,'build','tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj','-c','Release','-p:Platform=x64','-p:RuntimeIdentifier=win-x64','-p:SkipNativeBuild=true','-p:NativeArtifactsDir='+str(native/'Release/win-x64'),'-p:OutDir='+str(out),'--verbosity','minimal'],WORK/('build-tests-'+tag))
    for name in ('LivePhotoBox.Video.Libav.dll','LivePhotoBox.Native.TestHarness.dll'):
        shutil.copyfile(native/('TestHarness/Release/win-x64' if 'TestHarness' in name else 'Release/win-x64')/name,out/name)
    for name in ('LivePhotoBox.Core.dll','LivePhotoBox.Core.pdb','LivePhotoBox.Native.dll'):
        shutil.copyfile(WORK/('driver-'+FINAL_DRIVER)/name,out/name)
    save(WORK/('tests-'+tag+'.json'),dict(sources=before,runtime=[identity(p) for p in sorted(out.rglob('*')) if p.is_file()],
        deploymentFrom=deploy_from,exactCoreFromDriver=FINAL_DRIVER))
    classes=['SourceInspectorTests','SourceInspectorRoutingRegressionTests','ExtractorScaleTests','MediaWorkspaceTests','P7R2MergeTransactionTests','P7R2WorkspaceOwnershipTests','P7R2WorkspaceHandoffTests','P6NeutralPipelineTests','P5R6NeutralConsumptionIntegrationTests','P5R6ConverterIntegrationTests','VideoConverterTests','ImageConverterTests','ImageColorHighBitDepthTests','ConverterContractTests']
    selector='|'.join('FullyQualifiedName~LivePhotoBox.Core.Tests.Media.'+c for c in classes)
    selector+='|FullyQualifiedName~LivePhotoBox.Core.Tests.NativeRuntimeTests.Probe_ReportsTruthfulCapabilityIdentity|FullyQualifiedName~LivePhotoBox.Core.Tests.NativeRuntimeTests.Probe_LoadsMatchingNativeRuntime'
    dest=WORK/label;assembly=out/'LivePhotoBox.Core.Tests.dll'
    checked([helper.DOTNET,'vstest',assembly,'--ListTests','--TestCaseFilter:'+selector],dest/'discovery')
    names=[s.strip() for s in (dest/'discovery/stdout.txt').read_text(encoding='utf-8-sig').splitlines() if s.strip().startswith('LivePhotoBox.Core.Tests.')]
    assert names and len(names)==len(set(names))
    save(dest/'discovery.json',dict(selector=selector,names=names,total=len(names)))
    temp=WORK/'t'/label;temp.mkdir(parents=True,exist_ok=False)
    evidence=dest/'case-evidence';evidence.mkdir(parents=True,exist_ok=False)
    env={'LIVEPHOTOBOX_TEST_SAMPLES_DIR':str(ROOT/'.ai-tmp/cache/samples'),'LPB_R2_EVIDENCE':str(evidence),'TEMP':str(temp),'TMP':str(temp)}
    result=command([helper.DOTNET,'vstest',assembly,'--TestCaseFilter:'+selector,'--logger:trx;LogFileName=r4.trx','--ResultsDirectory:'+str(dest/'results')],dest/'execute',env)
    tree=ET.parse(dest/'results/r4.trx').getroot();ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    rows=[dict(name=x.attrib['testName'],outcome=x.attrib['outcome']) for x in tree.findall('t:Results/t:UnitTestResult',ns)]
    actual=[x['name'] for x in rows];counts=tree.find('t:ResultSummary/t:Counters',ns).attrib
    value=dict(process=result,counters=counts,rows=rows,missing=sorted(set(names)-set(actual)),unexpected=sorted(set(actual)-set(names)),sourcesUnchanged=before==sources(),tempFiles=[identity(p) for p in temp.rglob('*') if p.is_file()])
    value['passed']=result['directChildExitCode']==0 and len(rows)==len(names) and not value['missing'] and not value['unexpected'] and all(x['outcome']=='Passed' for x in rows) and value['sourcesUnchanged'] and not value['tempFiles']
    save(dest/'summary.json',value);print(json.dumps({k:v for k,v in value.items() if k not in ('rows','tempFiles')},ensure_ascii=True));assert value['passed']
if __name__=='__main__':
    if sys.argv[1]=='pause':raise SystemExit(pause(*sys.argv[2:4]))
    elif sys.argv[1]=='constrained':raise SystemExit(constrained(*sys.argv[2:4]))
    elif sys.argv[1]=='driver':raise SystemExit(driver(*sys.argv[2:5],extra=sys.argv[5:]))
    elif sys.argv[1]=='native':native(*sys.argv[2:4])
    elif sys.argv[1]=='build':build(sys.argv[2])
    elif sys.argv[1]=='tests':tests(*sys.argv[2:4])
    elif sys.argv[1]=='deploytests':tests(*sys.argv[2:4],deploy_from='v8')
