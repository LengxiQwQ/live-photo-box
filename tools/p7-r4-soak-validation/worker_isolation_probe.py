"""Diagnostic parent/child feasibility only; no product worker is introduced."""
import ctypes
import json
import pathlib
import sys
from ctypes import wintypes
import validate as v


def run(tag, label):
    root = v.WORK / label
    root.mkdir(exist_ok=False)
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.GetProcessHandleCount.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    kernel.GetProcessHandleCount.restype = wintypes.BOOL
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]

    def handles():
        count = wintypes.DWORD()
        assert kernel.GetProcessHandleCount(kernel.GetCurrentProcess(), ctypes.byref(count))
        return count.value

    before = v.helper.samples()
    driver = v.WORK / ('driver-' + tag)
    runtime = [v.identity(driver / name) for name in ('Campaign.dll', 'LivePhotoBox.Core.dll', 'LivePhotoBox.Native.dll', 'LivePhotoBox.Video.Libav.dll')]
    for name in ('LivePhotoBox.Core.dll', 'LivePhotoBox.Native.dll', 'LivePhotoBox.Video.Libav.dll'):
        assert v.hashfile(driver / name) == v.hashfile(v.WORK / 'driver-v19' / name)
    initial_handles = handles()
    rows = []
    for index in range(8):
        case = root / ('child-' + str(index))
        temp = v.WORK / 't' / label / str(index)
        temp.mkdir(parents=True, exist_ok=False)
        process = v.command([v.helper.DOTNET, driver / 'Campaign.dll', 'mf-once',
                             v.ROOT / '.ai-tmp/cache/samples', case / 'case'], case / 'process',
                            {'TEMP': str(temp), 'TMP': str(temp)}, timeout=180)
        assert process['directChildExitCode'] == 0
        child = v.read(case / 'case/mf-repeat.json')
        assert child['passed'] and len(child['rows']) == 1 and child['rows'][0]['workspaceRemoved']
        query = kernel.OpenProcess(0x00100000 | 0x1000, False, process['childPid'])
        if query:
            try:
                retired = kernel.WaitForSingleObject(query, 0) == 0
            finally:
                kernel.CloseHandle(query)
        else:
            retired = ctypes.get_last_error() == 87
        assert retired
        leftovers = [v.identity(p) for p in temp.rglob('*') if p.is_file()]
        assert not leftovers
        rows.append(dict(index=index, process=process, childRetired=retired,
                         childFinalSemaphores=child['rows'][0]['semaphores'],
                         parentHandles=handles(), tempFiles=leftovers))
    assert before == v.helper.samples()
    for item in runtime:
        assert v.hashfile(v.ROOT / item['path']) == item['sha256']
    value = dict(diagnosticOnly=True, productWorkerImplemented=False, passedDiagnosticAssertions=True,
                 scope='Eight separate development harness processes each perform one actual public Core/Native MF conversion. This is not production cancellation/publication/packaging evidence.',
                 sourceSamples=before, runtime=runtime, parentInitialHandles=initial_handles,
                 parentFinalHandles=handles(), rows=rows,
                 limitations='No production worker IPC, authority propagation, Job cancellation, retained outputs or cross-process publication tested. No product/runtime/packaging boundary changed.')
    v.save(root / 'summary.json', value)
    print(json.dumps({k: value[k] for k in ('passedDiagnosticAssertions', 'parentInitialHandles', 'parentFinalHandles', 'rows')}, ensure_ascii=True))


if __name__ == '__main__':
    run(*sys.argv[1:3])
