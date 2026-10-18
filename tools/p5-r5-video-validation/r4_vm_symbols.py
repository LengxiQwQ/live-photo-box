"""Local-PDB symbolization of offline diagnostic stack candidates."""
import ctypes as c
import json
import pathlib
import sys


def run(directory):
    root = pathlib.Path(directory)
    data = json.loads((root / 'trace-analysis.json').read_text())
    kernel = c.WinDLL('kernel32', use_last_error=True)
    dbg = c.WinDLL('dbghelp', use_last_error=True)
    kernel.GetCurrentProcess.restype = c.c_void_p
    process = kernel.GetCurrentProcess()
    dbg.SymSetOptions.argtypes = [c.c_ulong]
    # Eager local loading: deferred loading of absent OS PDBs can leave an
    # error on subsequent local Native PDB lookups in the inbox DbgHelp.
    dbg.SymSetOptions(2 | 0x80000)
    dbg.SymInitializeW.argtypes = [c.c_void_p, c.c_wchar_p, c.c_int]
    repository = pathlib.Path(__file__).resolve().parents[2]
    search = str(repository / 'artifacts/native/Release/win-x64')
    cache = repository / '.ai-tmp/cache/debugger-tools/symbols/identity.json'
    identities = json.loads(cache.read_text()) if cache.exists() else []
    if identities: search += ';' + ';'.join(str(pathlib.Path(item['pdb']).parent) for item in identities)
    assert dbg.SymInitializeW(process, search, 0), c.get_last_error()
    dbg.SymLoadModuleExW.argtypes = [c.c_void_p, c.c_void_p, c.c_wchar_p, c.c_wchar_p,
                                    c.c_ulonglong, c.c_ulong, c.c_void_p, c.c_ulong]
    dbg.SymLoadModuleExW.restype = c.c_ulonglong
    dbg.SymFromAddr.argtypes = [c.c_void_p, c.c_ulonglong, c.POINTER(c.c_ulonglong), c.c_void_p]
    dbg.SymCleanup.argtypes = [c.c_void_p]
    loaded = set()
    for group in data['outstandingStacks']:
        for frame in group['frames']:
            base, module = frame['moduleBase'], frame['module']
            if module and base not in loaded:
                dbg.SymLoadModuleExW(process, None, module, None, base, 0, None, 0)
                loaded.add(base)
            buffer = c.create_string_buffer(4096)
            c.c_ulong.from_buffer(buffer, 0).value = 88
            c.c_ulong.from_buffer(buffer, 80).value = 2047
            displacement = c.c_ulonglong()
            if dbg.SymFromAddr(process, frame['address'], c.byref(displacement), buffer):
                frame['symbol'] = buffer.raw[84:].split(b'\0', 1)[0].decode('utf-8', 'replace')
                frame['displacement'] = displacement.value
                frame['symbolConfidence'] = 'matching_local_pdb' if module and pathlib.PureWindowsPath(module).name.lower() in {
                    'ntdll.dll', 'coreclr.dll', 'livephotobox.native.dll', 'livephotobox.video.libav.dll'} else 'export_or_unverified'
    dbg.SymCleanup(process)
    data['symbolScope'] = 'Local files/PDBs only; candidate estimates retain original baseline/reuse limitations.'
    data['publicSymbolIdentities'] = identities
    with (root / 'trace-symbols-verified-pdb.json').open('x', encoding='utf-8') as output:
        json.dump(data, output, indent=2)
    print(json.dumps([{'bytes': row['bytes'], 'stack': [f.get('symbol', f.get('module')) for f in row['frames']]}
                      for row in data['outstandingStacks'][:12]]))


if __name__ == '__main__': run(sys.argv[1])
