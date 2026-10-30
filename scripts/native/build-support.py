"""Standard-library-only helpers for reproducible Native dependency builds."""
import datetime
import hashlib
import json
import os
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[2]

def read(path):
    return json.loads(pathlib.Path(path).read_text(encoding='utf-8-sig'))

def hashfile(path):
    h = hashlib.sha256()
    with pathlib.Path(path).open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest().upper()

def identity(path):
    p = pathlib.Path(path).resolve()
    return dict(path=p.relative_to(ROOT).as_posix() if p.is_relative_to(ROOT) else str(p),
                bytes=p.stat().st_size, sha256=hashfile(p))

def save(path, value):
    p = pathlib.Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open('x', encoding='utf-8') as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write('\n')

def command(args, destination, timeout=3600):
    # Preserve unsuccessful attempts; a later build can retry without deleting logs.
    base = pathlib.Path(destination)
    dest = base
    attempt = 0
    while dest.exists():
        attempt += 1
        dest = base.with_name(base.name + '-attempt-' + str(attempt))
    dest.mkdir(parents=True)
    row = dict(arguments=list(map(str, args)), workingDirectory=str(ROOT),
               startedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
               directChildExitCode=None)
    try:
        with (dest/'stdout.txt').open('xb') as out, (dest/'stderr.txt').open('xb') as err:
            result = subprocess.run(row['arguments'], cwd=ROOT, stdout=out, stderr=err, timeout=timeout)
            row['directChildExitCode'] = result.returncode
    except (OSError, subprocess.TimeoutExpired) as error:
        row['failure'] = str(error)
        raise
    finally:
        row['finishedUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        save(dest/'process.json', row)
    return row

def checked(args, destination):
    row = command(args, destination)
    if row['directChildExitCode'] != 0:
        raise RuntimeError('Dependency build failed; see ' + str(destination))
    return row
