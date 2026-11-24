"""Install verified MathCAT browser/Node modules, or build them from the locked source."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from urllib.request import urlopen
from math_notices import write as write_notices

ROOT = Path(__file__).resolve().parents[1]
CRATE = ROOT / 'web/math-speech'
LOCK = json.loads((CRATE / 'runtime-lock.json').read_text())


def install(destination, supplied=None):
    destination = Path(destination)
    destination.mkdir(parents=True, exist_ok=True)
    for name, digest in LOCK['files'].items():
        target = destination / name
        if target.is_file() and hashlib.sha256(target.read_bytes()).hexdigest() == digest:
            continue
        if supplied:
            data = (Path(supplied) / name).read_bytes()
        else:
            url = 'https://github.com/memoedu/KnowledgeClipper/releases/download/' + LOCK['tag'] + '/' + name
            with urlopen(url, timeout=90) as response:
                data = response.read(8 * 1024 * 1024)
        if hashlib.sha256(data).hexdigest() != digest:
            raise ValueError('Incorrect MathCAT module digest: ' + name)
        with tempfile.NamedTemporaryFile(dir=destination, delete=False) as tmp:
            tmp.write(data)
        os.replace(tmp.name, target)
    shutil.copyfile(ROOT / 'licenses/MathCAT.txt', destination / 'LICENSE.txt')
    (destination / 'package.json').write_text('{"type":"module"}\n', encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=Path)
    parser.add_argument('--from-dir', default=os.environ.get('KC_MATH_MODULE_DIR'))
    parser.add_argument('--build', metavar='TARGET_DIR', type=Path)
    args = parser.parse_args()
    if args.build:
        target = args.build.resolve()
        subprocess.run(['cargo', 'build', '--locked', '--manifest-path', str(CRATE / 'Cargo.toml'), '--target-dir',
                        str(target), '--release', '--target', 'wasm32-unknown-unknown'], check=True)
        if subprocess.check_output(['wasm-bindgen', '--version'], text=True).strip().split()[-1] != LOCK['wasm-bindgen']:
            raise SystemExit('Install the wasm-bindgen version from runtime-lock.json.')
        module = target / 'wasm32-unknown-unknown/release/knowledgeclipper_math_speech.wasm'
        with tempfile.TemporaryDirectory(dir=target) as out:
            subprocess.run(['wasm-bindgen', '--target', 'web', '--out-name', 'mathcat', '--out-dir', out, str(module)], check=True)
            write_notices(CRATE / 'Cargo.toml', Path(out) / 'mathcat-NOTICES.txt')
            install(args.destination, out)
    else:
        install(args.destination, args.from_dir)
