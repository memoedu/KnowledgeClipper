"""Pack the published browser workflow and desktop chat build for the website."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile
import subprocess
from zipfile import ZipFile, ZipInfo, ZIP_DEFLATED

from math_runtime import install

STATIC = {'.js', '.mjs', '.json', '.wasm', '.dat', '.css', '.html', '.txt', '.woff', '.woff2', '.ttf'}


def build(browser, chat, output):
    entries = {}
    graph = json.loads((chat / '.vite/manifest.json').read_bytes())
    chat_assets = {'index.html', 'mermaid-runtime.js', 'math-reader.mjs', 'THIRD-PARTY-NOTICES.txt'}
    for chunk in graph.values():
        chat_assets.add(chunk['file'])
        chat_assets.update(chunk.get('assets', []))
        chat_assets.update(chunk.get('css', []))
    for base, prefix in ((browser, ''), (chat, 'chat/')):
        for file in sorted(base.rglob('*')):
            name = file.relative_to(base).as_posix()
            if not file.is_file() or 'math' in file.relative_to(base).parts or file.suffix not in STATIC:
                continue
            if base == chat and name not in chat_assets:
                continue
            if file.is_symlink() or name.startswith('.') or any(part.startswith('.') for part in file.relative_to(base).parts):
                raise ValueError('Unexpected browser artifact: ' + name)
            entries[prefix + name] = file.read_bytes()
    if any('chat/' + name not in entries for name in chat_assets):
        raise ValueError('Incomplete chat distribution graph.')
    for required in ('index.html', 'frame.js', '_framework/blazor.boot.json', 'chat/index.html', 'chat/material.js',
                     'chat/math-reader.mjs', 'chat/THIRD-PARTY-NOTICES.txt'):
        if required not in entries:
            raise ValueError('Missing browser artifact: ' + required)
    katex, = (Path(name) for name in chat_assets if name.startswith('assets/katex-') and name.endswith('.css'))
    shared_css = Path(__file__).resolve().parents[1] / 'web/chat/src/material.css'
    entries['material.css'] = ('@import "./chat/assets/' + katex.name + '";\n').encode() + shared_css.read_bytes().replace(b'\r\n', b'\n')
    nuget = os.environ.get('NUGET_PACKAGES')
    if not nuget:
        nuget = subprocess.check_output(['dotnet', 'nuget', 'locals', 'global-packages', '--list'], text=True).split(': ', 1)[1].strip()
    runtime = Path(nuget) / 'microsoft.netcore.app.runtime.mono.browser-wasm/8.0.30'
    notices = [p.read_bytes() for p in sorted(runtime.iterdir())
               if p.is_file() and p.name.lower() in ('license.txt', 'third-party-notices.txt')]
    if len(notices) != 2:
        raise ValueError('Missing .NET runtime redistribution notices.')
    entries['DOTNET-NOTICES.txt'] = b'\n\n'.join(notices)
    entries['MANAGED-NOTICES.txt'] = (Path(__file__).resolve().parents[1] / 'licenses/managed-NOTICES.txt').read_bytes().replace(b'\r\n', b'\n')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as tmp:
        install(tmp, os.environ.get('KC_MATH_MODULE_DIR'))
        for file in Path(tmp).iterdir():
            entries['math/' + file.name] = file.read_bytes()
    with ZipFile(output, 'w', compression=ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(entries.items()):
            info = ZipInfo(name)
            info.external_attr = 0o644 << 16
            archive.writestr(info, data, compress_type=ZIP_DEFLATED, compresslevel=9)
    files = {k: hashlib.sha256(v).hexdigest() for k, v in sorted(entries.items())}
    return {'sha256': hashlib.sha256(output.read_bytes()).hexdigest(), 'files': files}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('browser', type=Path, help='dotnet publish wwwroot')
    parser.add_argument('chat', type=Path, help='KC_CHAT_OUTPUT from npm run build')
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    manifest = build(args.browser, args.chat, args.output)
    args.output.with_suffix('.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
