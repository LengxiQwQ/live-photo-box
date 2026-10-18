"""Offline NtAllocate/NtFree ledger; does not add heap and VM totals together."""
import collections
import bisect
import json
import pathlib
import struct
import sys

RECORD = struct.Struct('<QIi4Q24Q520s')
RECORD_V2 = struct.Struct('<QIi6Q24Q520s')


def read(path):
    with path.open('rb') as stream:
        magic, size, count, dropped, suppressed, capacity = struct.unpack('<6Q', stream.read(48))
        assert magic == 0x5234564D54524143 and size in (RECORD.size, RECORD_V2.size)
        layout = RECORD_V2 if size == RECORD_V2.size else RECORD
        assert dropped == suppressed == 0, (dropped, suppressed)
        result = []
        for i in range(count):
            raw = layout.unpack(stream.read(size))
            sequence, kind, status, address, length, flags, allocation_base = raw[:7]
            assert sequence == i
            frame_start = 9 if layout is RECORD_V2 else 7
            result.append(dict(sequence=sequence, kind=kind, status=status, address=address,
                bytes=length, flags=flags, allocationBase=allocation_base,
                beginClock=raw[7] if layout is RECORD_V2 else sequence,
                endClock=raw[8] if layout is RECORD_V2 else sequence,
                frames=list(raw[frame_start:frame_start+12]), bases=list(raw[frame_start+12:frame_start+24]),
                module=raw[frame_start+24].decode('utf-16le').rstrip('\0')))
        assert not stream.read(1)
        return result, dict(records=count, capacity=capacity, dropped=dropped, suppressed=suppressed)


def run(directory):
    root = pathlib.Path(directory)
    records, header = read(root / 'vm-trace.bin')
    modules = {}
    module_paths = collections.defaultdict(set)
    for row in map(json.loads, (root / 'samples.jsonl').read_text().splitlines()):
        for module in row.get('modules') or []:
            modules[module['address']] = module['FileName']
            module_paths[module['address']].add(module['FileName'].lower())
    for row in records:
        if row['kind'] == 3:
            modules[row['address']] = row['module']
            module_paths[row['address']].add(row['module'].lower())
    live = []
    baseline_path = root / 'vm-trace.bin.baseline.bin'
    baseline_total = 0
    if baseline_path.exists():
        with baseline_path.open('rb') as source:
            count, = struct.unpack('<Q', source.read(8))
            for _ in range(count):
                address, size, allocation_base = struct.unpack('<3Q', source.read(24))
                baseline_total += size
                live.append((address, address+size, dict(baseline=True, allocationBase=allocation_base,
                             frames=[0]*12, bases=[0]*12, sequence=-1)))
            assert not source.read(1)
        live.sort(key=lambda item: item[0])
    ambiguous_operations = []
    by_base = collections.defaultdict(list)
    for row in records:
        if row['kind'] in (1, 2) and row['status'] >= 0:
            by_base[row['allocationBase']].append(row)
    for base, operations in by_base.items():
        active = []
        for row in sorted(operations, key=lambda item: item['beginClock']):
            active = [prior for prior in active if prior['endClock'] >= row['beginClock']]
            for prior in active:
                if {row['kind'], prior['kind']} != {1, 2}: continue
                release = any(r['kind'] == 2 and r['flags'] == 0x8000 for r in (row, prior))
                overlap = max(row['address'], prior['address']) < min(row['address']+row['bytes'], prior['address']+prior['bytes'])
                if release or overlap:
                    ambiguous_operations.append(dict(allocationBase=base, first=prior['sequence'], second=row['sequence']))
            active.append(row)
    checkpoints = []
    types = collections.Counter()
    control_start = None
    control_passed = False
    untracked_releases = 0

    def total(): return sum(b - a for a, b, _ in live)

    def remove(lo, hi, allocation_base=None):
        nonlocal live
        remaining = []
        removed = 0
        for a, b, origin in live:
            if allocation_base is not None:
                if origin['allocationBase'] == allocation_base:
                    removed += b - a
                else: remaining.append((a, b, origin))
            elif b <= lo or a >= hi: remaining.append((a, b, origin))
            else:
                removed += min(b, hi) - max(a, lo)
                if a < lo: remaining.append((a, lo, origin))
                if b > hi: remaining.append((hi, b, origin))
        live = remaining
        return removed

    def add(row):
        # Commit can cover pages already committed; count newly covered pages.
        cursor, end = row['address'], row['address'] + row['bytes']
        index = bisect.bisect_left(live, cursor, key=lambda item: item[0])
        if index and live[index - 1][1] > cursor: index -= 1
        fragments = []
        while index < len(live) and live[index][0] < end:
            a, b, _ = live[index]
            if a > cursor: fragments.append((cursor, min(a, end)))
            cursor = max(cursor, b)
            index += 1
        if cursor < end: fragments.append((cursor, end))
        for a, b in fragments:
            bisect.insort(live, (a, b, row), key=lambda item: item[0])

    for row in sorted(records, key=lambda item: item['endClock']):
        if row['kind'] == 1:
            types[hex(row['flags'])] += 1
            if row['status'] >= 0 and row['flags'] & 0x1000:
                assert row['allocationBase']
                add(row)
        elif row['kind'] == 2 and row['status'] >= 0:
            if row['flags'] == 0x4000: remove(row['address'], row['address'] + row['bytes'])
            elif row['flags'] == 0x8000:
                if not remove(0, 0, row['allocationBase']): untracked_releases += 1
            else: raise AssertionError(('Unknown free flags', row))
        elif row['kind'] == 4:
            round_number = row['address'] if row['address'] < 2**63 else row['address'] - 2**64
            if round_number == -2:
                if row['bytes'] == 0: control_start = total()
                else:
                    assert control_start == total(), (control_start, total())
                    control_passed = True
            checkpoints.append(dict(round=round_number, checkpoint=row['bytes'],
                newCommitStillPresent=sum(b-a for a,b,origin in live if not origin.get('baseline')),
                totalPrivateCommitEstimate=total(), deltaFromBaseline=total()-baseline_total))
    assert control_passed
    stacks = collections.defaultdict(lambda: dict(bytes=0, segments=0, firstSequence=None))
    for a, b, origin in live:
        if origin.get('baseline'): continue
        key = tuple(zip(origin['frames'], origin['bases']))
        value = stacks[key]
        value['bytes'] += b - a
        value['segments'] += 1
        value['firstSequence'] = min(value['firstSequence'] or origin['sequence'], origin['sequence'])
    groups = []
    for stack, value in stacks.items():
        frames = [dict(address=address, moduleBase=base, offset=address-base if base else None,
                       module=modules.get(base) if len(module_paths[base]) <= 1 else None,
                       moduleAmbiguous=len(module_paths[base]) > 1) for address, base in stack if address]
        groups.append(dict(**value, frames=frames))
    groups.sort(key=lambda item: item['bytes'], reverse=True)
    result = dict(diagnosticOnly=True, header=header, controlPassed=control_passed,
        scope='Estimated trace-attributed committed ranges, minus paired decommit/release. Candidate attribution only; not an unhooked resource acceptance run.',
        baselinePresent=baseline_path.exists(), baselineBytes=baseline_total,
        overlappingAllocationFreeOperations=ambiguous_operations,
        reusedModuleBases=[base for base, paths in module_paths.items() if len(paths)>1],
        precisionLimitations=['Live baseline walk is not stop-world; startup concurrency remains a diagnostic limitation.',
            'Overlapping allocation/free address generations are explicitly ambiguous, not a no-leak proof.',
            'Only selected NtAllocateVirtualMemory/NtFreeVirtualMemory layer is captured; other virtual-memory APIs are not yet counted.'],
        untrackedPreTraceReleases=untracked_releases, allocationFlags=dict(types),
        checkpoints=checkpoints, outstandingBytes=sum(g['bytes'] for g in groups),
        totalPrivateCommitEstimate=total(), deltaFromBaseline=total()-baseline_total, outstandingStacks=groups)
    with (root / 'trace-analysis.json').open('x', encoding='utf-8') as output:
        json.dump(result, output, indent=2)
    print(json.dumps(dict(records=header['records'], controlPassed=True, outstandingBytes=sum(g['bytes'] for g in groups),
                         largestGroups=[r['bytes'] for r in groups[:8]])))


if __name__ == '__main__': run(sys.argv[1])
