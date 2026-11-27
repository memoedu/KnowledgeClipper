"""Transport the existing Content Worker contract over host-key-pinned SSH."""
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import shlex
import subprocess
import sys
import tempfile
import zipfile

META = '__kc_transport.json'
MAX_BYTES = 64 * 1024 * 1024
WORKER = 'content.deepseek'
OPERATIONS = {'understand-and-card', 'question.answer', 'question.make-cards'}
MODELS = {'deepseek-chat', 'deepseek-reasoner'}
IDENTITY_KEYS = ('requestId', 'operationId', 'taskId', 'runId', 'stageId', 'attemptId', 'idempotencyKey')


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':')).encode()


def digest(value):
    return hashlib.sha256(value).hexdigest()


def relative(name):
    part = PurePosixPath(name)
    if (not part.parts or part.as_posix() != name or part.is_absolute() or '..' in part.parts
            or any('\x00' in item or '\\' in item or ':' in item or item.endswith(('.', ' ')) for item in part.parts)):
        raise ValueError('Nonportable package path')
    return part


def within(root, name):
    path = root.joinpath(*relative(name).parts).resolve()
    if not path.is_relative_to(root):
        raise ValueError('Package path escaped its root')
    return path


def read_bytes(path):
    if not path.is_file() or path.stat().st_size > MAX_BYTES:
        raise ValueError('Unavailable or oversized package file')
    return path.read_bytes()


def archive(files):
    result = io.BytesIO()
    if sum(map(len, files.values())) > MAX_BYTES:
        raise ValueError('Package exceeds the transport limit')
    with zipfile.ZipFile(result, 'w') as bundle:
        for name, content in sorted(files.items()):
            bundle.writestr(name, content)
    if result.tell() > MAX_BYTES:
        raise ValueError('Package exceeds the transport limit')
    return result.getvalue()


def unpack(data):
    if len(data) > MAX_BYTES:
        raise ValueError('Package exceeds the transport limit')
    with zipfile.ZipFile(io.BytesIO(data)) as bundle:
        entries = bundle.infolist()
        if len(entries) > 256 or sum(info.file_size for info in entries) > MAX_BYTES:
            raise ValueError('Oversized package')
        names = {info.filename.casefold() for info in entries}
        if len(names) != len(entries) or any(info.is_dir() for info in entries):
            raise ValueError('Duplicate or unsupported package entries')
        return {relative(info.filename).as_posix():bundle.read(info) for info in entries}


def request_for(load, entry):
    request = json.loads(load(entry))
    if (request['schemaVersion'] != '1.0.0' or request['workerId'] != WORKER
            or request['operation'] not in OPERATIONS or request['payload']['providerMode'] not in {'live', 'replay'}):
        raise ValueError('Not an admitted Content request')
    attempt = relative(request['attemptPath'])
    if (entry != (attempt / 'request.json').as_posix()
            or request['resultPath'] != (attempt / 'result.json').as_posix()
            or request['outputPath'] != (attempt / 'outputs').as_posix()
            or request['progressPath'] != (attempt / 'progress.ndjson').as_posix()
            or request['cancelSignal']['path'] != (attempt / 'cancel.json').as_posix()):
        raise ValueError('Content attempt paths do not match')
    return request


def collect_refs(load, references, admitted_kinds, required_prefix=None):
    files = {}
    for ref in references:
        path = ref['manifestPath']
        if required_prefix is not None and not relative(path).is_relative_to(required_prefix):
            raise ValueError('Artifact manifest escaped the attempt')
        raw = load(path)
        value = json.loads(raw)
        if (ref['kind'] not in admitted_kinds or ref['kind'] != value['kind']
                or ref['artifactRef'] != value['artifactId'] or ref['contractVersion'] != value['contractVersion']
                or digest(raw) != ref['expectedSha256'].lower() or value['dataClass'] == 'secret-prohibited'):
            raise ValueError('Artifact identity or digest mismatch')
        files[path] = raw
        for item in value['files']:
            name = item['path']
            if required_prefix is not None and not relative(name).is_relative_to(required_prefix):
                raise ValueError('Artifact file escaped the attempt')
            item_bytes = load(name)
            if len(item_bytes) != item['bytes'] or digest(item_bytes) != item['sha256'].lower():
                raise ValueError('Artifact file digest mismatch')
            files[name] = item_bytes
            if len(files) >= 256 or sum(map(len, files.values())) > MAX_BYTES:
                raise ValueError('Oversized declared artifact files')
    return files


def input_files(load, request, entry):
    # The existing Core Content adapter emits one content-input. Additional
    # configuration/artifact kinds require an explicit review of their data boundary.
    refs = request['inputRefs']
    if len(refs) != 1 or refs[0]['kind'] != 'content-input':
        raise ValueError('Expected the frozen Core Content input')
    files = collect_refs(load, refs, {'content-input'})
    if any(name in files for name in (META, entry, request['resultPath'], request['cancelSignal']['path'])):
        raise ValueError('Input collides with the attempt envelope')
    return {entry:load(entry), **files}


def result_files(load, request, version):
    root = relative(request['outputPath'])
    result_path = request['resultPath']
    raw = load(result_path)
    result = json.loads(raw)
    if (result['schemaVersion'] != '1.0.0' or result['worker'] != {'id':WORKER, 'version':version}
            or any(result[key] != request[key] for key in IDENTITY_KEYS)
            or result['status'] not in {'completed','failed','cancelled'}):
        raise ValueError('Content Worker result identity mismatch')
    outputs = collect_refs(load, result['outputs'],
        {'learning-bundle','card-bundle','question.answer','language-practice-decision',
         'ocr-proofread-bundle','ocr-human-resolution-bundle'}, root)
    return {result_path:raw, **outputs}


def publish_once(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix='.ssh-content-', dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as destination:
            destination.write(content)
            destination.flush()
            os.fsync(destination.fileno())
        try:
            os.link(temporary, path)
            return True
        except FileExistsError:
            if read_bytes(path) != content:
                raise ValueError('Existing attempt bytes differ')
            return False
    finally:
        os.unlink(temporary)


def submit(config_file, job, request_file, version, timeout, recover_only):
    config = json.loads(read_bytes(Path(config_file)))
    root = Path(job).resolve()
    entry = Path(request_file).resolve().relative_to(root).as_posix()
    load = lambda name: read_bytes(within(root, name))
    request = request_for(load, entry)
    files = input_files(load, request, entry)
    host = config['host']
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,127}', host) or config['model'] not in MODELS:
        raise ValueError('Invalid SSH target or provider model')
    remote = [config[key] for key in ('remotePython','remoteHelper','remoteConfig')]
    if not all(re.fullmatch(r'/(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+', filename) for filename in remote):
        raise ValueError('Invalid remote executable or configuration path')
    if within(root, request['cancelSignal']['path']).exists():
        raise ValueError('Already cancelled')
    result_path = within(root, request['resultPath'])
    if result_path.is_file():
        result_files(load, request, version)
        return
    reservation = within(root, (relative(request['attemptPath']) / 'content-ssh-reservation.json').as_posix())
    identity = canonical({'request':digest(files[entry]), 'workerVersion':version, 'model':config['model'], 'host':host})
    if recover_only and not reservation.is_file():
        raise OutcomeUnknown()
    if reservation.is_file() and read_bytes(reservation) != identity:
        raise OutcomeUnknown()
    # Even when SSH cannot tell whether it connected, this attempt may subsequently
    # be read back only. A local retry can never open a second remote writer.
    recover_only = not publish_once(reservation, identity) or recover_only
    metadata = {'wire':1, 'entry':entry, 'version':version, 'model':config['model'],
        'timeout':int(timeout), 'recoverOnly':recover_only}
    try:
        wire = archive({**files, META:canonical(metadata)})
        command = 'exec ' + shlex.join([remote[0], remote[1], 'serve', remote[2]])
        response = subprocess.run(['ssh','-T','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes',
            '-o','ClearAllForwardings=yes','-o','ForwardAgent=no','-o','ForwardX11=no','-o','SendEnv=-*',
            '-o','ConnectTimeout=5','-o','ServerAliveInterval=5','-o','ServerAliveCountMax=2',host,command],
            input=wire, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=int(timeout)+12, check=False)
        if response.returncode:
            raise OutcomeUnknown()
        returned = unpack(response.stdout)
        checked = result_files(returned.__getitem__, request, version)
        if returned != checked:
            raise ValueError('Returned package has undeclared members')
        for path, content in checked.items():
            if path != request['resultPath']:
                publish_once(within(root, path), content)
        publish_once(within(root, request['resultPath']), checked[request['resultPath']])
    except (ValueError, OSError, KeyError, zipfile.BadZipFile) as error:
        raise OutcomeUnknown() from error


def serve(config_file):
    os.umask(0o077)
    config = json.loads(read_bytes(Path(config_file)))
    received = unpack(sys.stdin.buffer.read(MAX_BYTES+1))
    meta = json.loads(received.pop(META))
    version, model, timeout, entry = (meta[key] for key in ('version','model','timeout','entry'))
    if (meta['wire'] != 1 or model not in MODELS or model != config['model'] or type(timeout) is not int
            or not 5 <= timeout <= 1800 or not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', version)):
        raise ValueError('Unsupported transport or provider configuration')
    request = request_for(received.__getitem__, entry)
    if received != input_files(received.__getitem__, request, entry):
        raise ValueError('Undeclared Content input file')
    root = Path(config['spool']).resolve()
    root.mkdir(mode=0o700, parents=True, exist_ok=True)
    identity = digest(canonical([request['attemptId'],request['idempotencyKey']]))
    reservation = canonical({'entry':entry,'version':version,'model':model,
        'files':{name:digest(data) for name,data in received.items()}})
    job = within(root, identity)
    marker = job / 'reservation.json'
    load = lambda name: read_bytes(within(job, name))
    if job.exists() or meta['recoverOnly']:
        if not marker.is_file() or read_bytes(marker) != reservation:
            raise OutcomeUnknown()
    else:
        command = [config['node'], config['worker']]
        descriptor = subprocess.run([*command, 'describe', '--json'], stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, timeout=8, check=True)
        info = json.loads(descriptor.stdout)
        if info['workerId'] != WORKER or info['workerVersion'] != version:
            raise ValueError('Remote Content Worker version differs from Core admission')
        live = request['payload']['providerMode'] == 'live'
        if live and not os.environ.get('DEEPSEEK_KNOWLEDGECLIPPER_API_KEY', '').strip():
            raise ValueError('This host lacks a dedicated Content provider credential')
        try:
            job.mkdir(mode=0o700)
        except FileExistsError as error:
            raise OutcomeUnknown() from error
        publish_once(marker, reservation)
        for name, data in received.items():
            publish_once(within(job, name), data)
        env = {**os.environ, 'AI_PRODUCER_MODEL':model, 'AI_PRODUCER_MAX_RETRIES':'1',
            'AI_PRODUCER_BASE_URL':'https://api.deepseek.com'}
        subprocess.run([*command, 'run', '--request', str(within(job, entry))], env=env,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=timeout, check=False)
    if not within(job, request['resultPath']).is_file():
        raise OutcomeUnknown()
    sys.stdout.buffer.write(archive(result_files(load, request, version)))


class OutcomeUnknown(RuntimeError):
    pass


if __name__ == '__main__':
    mode, *args = sys.argv[1:]
    try:
        if mode == 'serve':
            serve(*args)
        elif mode in ('submit','recover'):
            submit(*args, recover_only=mode=='recover')
        else:
            raise ValueError('Unsupported Content transport operation')
    except (OutcomeUnknown, subprocess.TimeoutExpired, subprocess.CalledProcessError):
        print('CONTENT_REMOTE_OUTCOME_UNKNOWN', file=sys.stderr)
        sys.exit(75)
    except (ValueError, OSError, KeyError, TypeError, zipfile.BadZipFile):
        print('CONTENT_PRECHECK_REJECTED', file=sys.stderr)
        sys.exit(2)
