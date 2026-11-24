"""Collect notices from the locked Cargo dependency sources used by the math module."""
import json
from pathlib import Path
import subprocess
import sys


def write(manifest, destination):
    result = subprocess.check_output(['cargo', 'metadata', '--locked', '--offline', '--format-version', '1',
        '--filter-platform', 'wasm32-unknown-unknown', '--manifest-path', str(manifest)], text=True)
    sections = []
    for package in sorted(json.loads(result)['packages'], key=lambda item: (item['name'], item['version'])):
        if not package['source']:
            continue
        root = Path(package['manifest_path']).parent
        heading = '{} {} ({})'.format(package['name'], package['version'], package['license'] or 'see notice')
        sections.extend([heading, 'https://crates.io/crates/{}/{}'.format(package['name'], package['version'])])
        for file in sorted(root.iterdir(), key=lambda p: p.name):
            if file.is_file() and file.name.lower().startswith(('license', 'licence', 'copying', 'copyright', 'notice', 'unlicense')):
                sections.extend([file.name, file.read_text(encoding='utf-8', errors='replace').strip()])
        sections.append('')
    destination.write_bytes(('\n'.join(sections) + '\n').encode())


if __name__ == '__main__':
    write(Path(sys.argv[1]), Path(sys.argv[2]))
