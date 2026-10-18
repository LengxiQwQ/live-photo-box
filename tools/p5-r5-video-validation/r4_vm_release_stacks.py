"""Attribute captured decommit/release near natural drops, not live heap objects."""
import collections
import json
import pathlib
import struct
import sys
from r4_vm_trace import read


def run(directory):
    root = pathlib.Path(directory)
    events, header = read(root / 'vm-trace.bin')
    samples = [json.loads(line) for line in (root / 'samples.jsonl').read_text().splitlines()]
    modules = {m['address']: m['FileName'] for row in samples for m in row.get('modules') or []}
    modules.update({r['address']: r['module'] for r in events if r['kind'] == 3})
    markers = {r['bytes']: r['endClock'] for r in events if r['kind'] == 4 and r['address'] != 2**64-2}
    windows = []
    for index, (before, after) in enumerate(zip(samples, samples[1:])):
        drop = before['resources']['privateBytes'] - after['resources']['privateBytes']
        if drop > 32 * 2**20:
            start = samples[max(0, index-1)]
            windows.append(dict(before=before['stage'], after=after['stage'],
                iteration=after['iteration'], privateDrop=drop,
                begin=markers[start['checkpoint']], end=markers[after['checkpoint']]))
    live = []
    with (root / 'vm-trace.bin.baseline.bin').open('rb') as source:
        count, = struct.unpack('<Q', source.read(8))
        for _ in range(count):
            address, size, base = struct.unpack('<3Q', source.read(24))
            live.append((address, address+size, base))
    groups = collections.defaultdict(lambda: dict(bytes=0, segments=0, firstSequence=0))
    selected = []
    for row in sorted(events, key=lambda r: r['endClock']):
        if row['status'] < 0 or row['kind'] not in (1, 2):
            continue
        lo, hi, base = row['address'], row['address']+row['bytes'], row['allocationBase']
        if row['kind'] == 1 and row['flags'] & 0x1000:
            cursor = lo
            fragments = []
            for a, b, _ in sorted(live):
                if b <= cursor: continue
                if a >= hi: break
                if a > cursor: fragments.append((cursor, a, base))
                cursor = max(cursor, b)
            if cursor < hi: fragments.append((cursor, hi, base))
            live.extend(fragments)
        elif row['kind'] == 2:
            remaining, removed = [], 0
            for a, b, origin in live:
                if row['flags'] == 0x8000:
                    if origin == base: removed += b-a
                    else: remaining.append((a, b, origin))
                elif row['flags'] == 0x4000:
                    if b <= lo or a >= hi: remaining.append((a, b, origin))
                    else:
                        removed += min(b, hi)-max(a, lo)
                        if a < lo: remaining.append((a, lo, origin))
                        if b > hi: remaining.append((hi, b, origin))
                else: raise AssertionError(row)
            live = remaining
            if any(w['begin'] <= row['endClock'] <= w['end'] for w in windows):
                key = tuple(zip(row['frames'], row['bases']))
                value = groups[key]
                value['bytes'] += removed
                value['segments'] += 1
                value['firstSequence'] = row['sequence']
                selected.append(dict(sequence=row['sequence'], removedEstimate=removed, clock=row['endClock']))
    stacks = [dict(**value, frames=[dict(address=a, moduleBase=b, offset=a-b if b else None,
               module=modules.get(b)) for a, b in stack if a]) for stack, value in groups.items()]
    expected = json.loads((root / 'trace-analysis.json').read_text())
    assert sum(b-a for a,b,_ in live) == expected['totalPrivateCommitEstimate'], 'Release ledger does not match primary ledger'
    stacks.sort(key=lambda r: r['bytes'], reverse=True)
    output = root / 'natural-release-stacks'
    output.mkdir(exist_ok=False)
    with (output / 'trace-analysis.json').open('x', encoding='utf-8') as target:
        json.dump(dict(diagnosticOnly=True, header=header, windows=windows, selected=selected, ledgerCrossCheck=True,
            overlappingAllocationFreeOperations=expected['overlappingAllocationFreeOperations'],
            reusedModuleBases=expected['reusedModuleBases'],
            scope='Release stack estimates around observed >32MiB natural/diagnostic drops; selected API/baseline/reuse limitations apply.',
            outstandingStacks=stacks), target, indent=2)
    print(json.dumps(dict(windows=windows, releaseGroups=len(stacks))))


if __name__ == '__main__': run(sys.argv[1])
