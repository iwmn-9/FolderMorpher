"""Freeze an audited Filunest small OCR runtime into reproducible embedded chunks."""
import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path
import tarfile

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--source', type=Path, required=True, help='Audited release/ocr directory')
parser.add_argument('--notices', type=Path, required=True, help='Supplemental notices and source archives')
args = parser.parse_args()
source = args.source.resolve()
inventory = json.loads((source / 'bundle.json').read_text('utf-8'))
files = []
for row in inventory:
    path = (source / row['path']).resolve()
    if path.suffix in {'.pyc', '.pyo', '.pdb'} or '__pycache__' in path.parts:
        continue
    if not path.is_relative_to(source) or path.is_symlink():
        raise ValueError('Invalid source entry')
    if path.stat().st_size != row['bytes'] or hashlib.sha256(path.read_bytes()).hexdigest() != row['sha256']:
        raise ValueError('Audited OCR file changed: ' + row['path'])
    files.append((row['path'], path))
filtered_inventory = [row for row in inventory if any(name == row['path'] for name, _ in files)]
files.append(('bundle.json', json.dumps(filtered_inventory, indent=2).encode('utf-8')))
source_inventory_hash = hashlib.sha256((source / 'bundle.json').read_bytes()).hexdigest()
files.extend(('licenses/supplemental/' + p.relative_to(args.notices).as_posix(), p)
             for p in args.notices.rglob('*') if p.is_file() and '__pycache__' not in p.parts)
files.sort()
output = ROOT / 'assets/ocr'
output.mkdir(parents=True, exist_ok=True)
compressed = io.BytesIO()
with gzip.GzipFile(fileobj=compressed, mode='wb', filename='', mtime=0, compresslevel=9) as gz:
    with tarfile.open(fileobj=gz, mode='w|', format=tarfile.USTAR_FORMAT) as tar:
        for name, path in files:
            info = tarfile.TarInfo(name)
            info.size = len(path) if isinstance(path, bytes) else path.stat().st_size
            info.mode = 0o600
            with (io.BytesIO(path) if isinstance(path, bytes) else path.open('rb')) as f:
                tar.addfile(info, f)
data = compressed.getbuffer()
chunks = []
for index, offset in enumerate(range(0, len(data), 40 * 1024 * 1024)):
    name = f'runtime.part{index:02}'
    part = data[offset:offset + 40 * 1024 * 1024]
    (output / name).write_bytes(part)
    chunks.append(dict(path=name, bytes=len(part), sha256=hashlib.sha256(part).hexdigest()))
(output / 'manifest.json').write_text(json.dumps(dict(format='tar+gzip', files=len(files),
    expandedBytes=sum(len(p) if isinstance(p, bytes) else p.stat().st_size for _, p in files), sourceInventorySha256=source_inventory_hash, chunks=chunks), indent=2), 'utf-8')
print(f'Frozen OCR runtime: {len(files)} files, {len(data)/1024**2:.1f} MiB, {len(chunks)} chunks')
