"""Fetch exact PE CodeView identities from Microsoft's public symbol server.

Local offline diagnosis only; no project samples or private symbols are sent.
"""
import hashlib
import json
import pathlib
import struct
import urllib.request
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[2]
CACHE = ROOT / '.ai-tmp/cache/debugger-tools/symbols'


def codeview(path):
    data = path.read_bytes()
    pe, = struct.unpack_from('<I', data, 0x3c)
    assert data[pe:pe+4] == b'PE\0\0'
    sections, = struct.unpack_from('<H', data, pe+6)
    optional_size, = struct.unpack_from('<H', data, pe+20)
    optional = pe+24
    assert struct.unpack_from('<H', data, optional)[0] == 0x20b
    rva, length = struct.unpack_from('<II', data, optional+112+6*8)
    def offset(rva):
        for i in range(sections):
            entry = optional+optional_size+i*40
            virtual_size, address, raw_size, raw_offset = struct.unpack_from('<4I', data, entry+8)
            if address <= rva < address+max(virtual_size,raw_size): return raw_offset+rva-address
        raise ValueError('Unmapped debug RVA')
    debug = offset(rva)
    for entry in range(debug, debug+length, 28):
        kind, size, _, raw = struct.unpack_from('<4I', data, entry+12)
        if kind == 2 and data[raw:raw+4] == b'RSDS':
            guid = uuid.UUID(bytes_le=data[raw+4:raw+20]).hex.upper()
            age, = struct.unpack_from('<I',data,raw+20)
            name = data[raw+24:raw+size].split(b'\0',1)[0].decode().replace('\\','/').rsplit('/',1)[-1]
            return name, guid+format(age,'X')
    raise ValueError('No RSDS identity')


def run():
    rows = []
    for path in (pathlib.Path('C:/Windows/System32/ntdll.dll'),
                 pathlib.Path('C:/Program Files/dotnet/shared/Microsoft.NETCore.App/9.0.20/coreclr.dll')):
        name, key = codeview(path)
        target = CACHE/name/key/name
        url = 'https://msdl.microsoft.com/download/symbols/'+name+'/'+key+'/'+name
        target.parent.mkdir(parents=True,exist_ok=True)
        if not target.exists():
            with urllib.request.urlopen(url) as source, target.open('xb') as output:
                while data := source.read(1048576): output.write(data)
        rows.append({'binary':str(path),'binarySha256':hashlib.sha256(path.read_bytes()).hexdigest().upper(),
                     'pdb':str(target),'codeViewKey':key,'url':url,'bytes':target.stat().st_size,
                     'sha256':hashlib.sha256(target.read_bytes()).hexdigest().upper()})
        print(name,target.stat().st_size,flush=True)
    with (CACHE/'identity.json').open('x',encoding='utf-8') as output: json.dump(rows,output,indent=2)


if __name__ == '__main__': run()
