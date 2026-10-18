"""Descriptive offline heap observations; deliberately no resource PASS verdict."""
import json
import pathlib
import statistics
import sys
import r4_closeout as c


def summarize(tag='v62'):
    root = c.H / ('mixed-heap-' + tag)
    after = c.v.read(root / 'identity-after.json')
    assert after['unchanged'] and after['process']['directChildExitCode'] == 0 and not after['process'].get('timedOut')
    mixed = c.v.read(root / 'diagnosis/case/mixed.json')
    measured = {r['round']: ('warmup' if r['warmup'] else 'measured') for r in mixed['measurements']}
    data = root / 'diagnosis/heap-samples.jsonl'
    rows = [json.loads(line) for line in data.read_text().splitlines()]
    assert all(h['complete'] for r in rows for h in r['heaps'])
    selected = []
    for row in rows:
        selected.append(dict(round=row['round'], stage=row['stage'],
            phase=('initial' if row['stage'] == 'initial' else 'after-quiet' if row['stage'] == 'after-existing-campaign-quiet'
                   else measured.get(row['round'], 'retained-output')),
            privateBytes=row['resources']['privateBytes'],
            managedBytes=row['resources']['managedBytes'],
            vmPrivateCommit=row['virtualMemory']['privateCommit'],
            busyBytes=sum(h['busy'] for h in row['heaps']),
            busyBlocks=sum(h['blocks'] for h in row['heaps']),
            gc=row['gc'], heaps=row['heaps']))
    per_stage = {}
    for stage in ('heic', 'jpeg', 'hevc-sdr', 'hdr', 'hevc8', 'mf-h264'):
        part = [row for row in selected if row['stage'] == stage and row['phase'] == 'measured']
        per_stage[stage] = {name: dict(first=part[0][name], last=part[-1][name],
            minimum=min(r[name] for r in part), maximum=max(r[name] for r in part),
            median=statistics.median(r[name] for r in part))
            for name in ('privateBytes', 'busyBytes', 'busyBlocks', 'vmPrivateCommit')}
    result = dict(diagnosticOnly=True, raw=c.v.identity(data), identityAfter=c.v.identity(root / 'identity-after.json'),
        points=selected, stageStatistics=per_stage,
        limitations=['HeapWalk is intrusive and races with heap lifetime outside each lock.',
            'Stage observations precede CodecRound workspace disposal; compare matching stages.',
            'Heap region/free fields are not reliable whole-process commitment totals.',
            'Stable busy bytes alone do not prove uninstrumented resource convergence.',
            'Existing Mixed diagnostic GC remains outside its measured workload.'])
    c.v.save(root / 'heap-summary.json', result)
    print(json.dumps(dict(stageStatistics=per_stage, last=selected[-1]), ensure_ascii=False))


if __name__ == '__main__':
    summarize(*sys.argv[1:])
