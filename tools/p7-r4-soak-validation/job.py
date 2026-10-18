"""Real per-child Windows Job commit limit; no global system configuration."""
import ctypes as c
from ctypes import wintypes as w
import subprocess, os, datetime

class Basic(c.Structure):
    _fields_=[('processTime',c.c_longlong),('jobTime',c.c_longlong),('flags',w.DWORD),
              ('minimumWorkingSet',c.c_size_t),('maximumWorkingSet',c.c_size_t),('activeLimit',w.DWORD),
              ('affinity',c.c_size_t),('priority',w.DWORD),('scheduling',w.DWORD)]
class Io(c.Structure):
    _fields_=[(name,c.c_ulonglong) for name in ('readOps','writeOps','otherOps','readBytes','writeBytes','otherBytes')]
class Extended(c.Structure):
    _fields_=[('basic',Basic),('io',Io),('processMemory',c.c_size_t),('jobMemory',c.c_size_t),
              ('peakProcessMemory',c.c_size_t),('peakJobMemory',c.c_size_t)]

def run(v,args,destination,case,temp,limit=512*1024*1024):
    destination.mkdir(parents=True,exist_ok=False);temp.mkdir(parents=True,exist_ok=False)
    k=c.WinDLL('kernel32',use_last_error=True)
    k.CreateJobObjectW.argtypes=[c.c_void_p,w.LPCWSTR];k.CreateJobObjectW.restype=w.HANDLE
    k.SetInformationJobObject.argtypes=[w.HANDLE,c.c_int,c.c_void_p,w.DWORD];k.SetInformationJobObject.restype=w.BOOL
    k.QueryInformationJobObject.argtypes=[w.HANDLE,c.c_int,c.c_void_p,w.DWORD,c.c_void_p];k.QueryInformationJobObject.restype=w.BOOL
    k.AssignProcessToJobObject.argtypes=[w.HANDLE,w.HANDLE];k.AssignProcessToJobObject.restype=w.BOOL
    k.IsProcessInJob.argtypes=[w.HANDLE,w.HANDLE,c.POINTER(w.BOOL)];k.IsProcessInJob.restype=w.BOOL
    k.CloseHandle.argtypes=[w.HANDLE];k.CloseHandle.restype=w.BOOL
    handle=k.CreateJobObjectW(None,None);assert handle
    process=None
    record=dict(arguments=list(map(str,args)),workingDirectory=str(v.ROOT),processMemoryLimit=limit,
                startedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
    try:
        limits=Extended();limits.basic.flags=0x100|0x2000;limits.processMemory=limit
        assert k.SetInformationJobObject(handle,9,c.byref(limits),c.sizeof(limits)),c.get_last_error()
        queried=Extended();assert k.QueryInformationJobObject(handle,9,c.byref(queried),c.sizeof(queried),None)
        assert queried.processMemory==limit and queried.basic.flags&0x100
        env=os.environ.copy();env.update(TEMP=str(temp),TMP=str(temp))
        record['environmentOverrides']={'TEMP':str(temp),'TMP':str(temp)}
        with (destination/'stdout.txt').open('xb') as out,(destination/'stderr.txt').open('xb') as err:
            process=subprocess.Popen(list(map(str,args)),cwd=v.ROOT,env=env,stdout=out,stderr=err)
            record['childPid']=process.pid
            assert k.AssignProcessToJobObject(handle,int(process._handle)),c.get_last_error()
            assigned=w.BOOL();assert k.IsProcessInJob(int(process._handle),handle,c.byref(assigned)) and assigned.value
            record['assignmentConfirmed']=True
            case.mkdir(parents=True,exist_ok=True)
            with (case/'job-assigned.gate').open('x') as gate:gate.write('Real Windows Job assignment and exact process commit limit confirmed.\n')
            try:record['directChildExitCode']=process.wait(timeout=180)
            except subprocess.TimeoutExpired:
                process.kill();process.wait();record.update(timedOut=True,directChildExitCode=process.returncode)
        k.QueryInformationJobObject(handle,9,c.byref(queried),c.sizeof(queried),None)
        record['peakProcessCommit']=queried.peakProcessMemory
    finally:
        if process is not None and process.poll() is None:process.kill();process.wait()
        k.CloseHandle(handle)
    record['finishedUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
    v.save(destination/'process.json',record)
    return record
