"""Run or recover one KnowledgeClipper desktop content request over SSH."""

import base64
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import urllib.error
import urllib.request


LIMIT = 4 * 1024 * 1024
PROVIDERS = {
    "openai-responses": ("https://api.openai.com/v1/responses", "OPENAI_KNOWLEDGECLIPPER_API_KEY"),
    "deepseek-chat": ("https://api.deepseek.com/chat/completions", "DEEPSEEK_KNOWLEDGECLIPPER_API_KEY"),
}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def send_provider(endpoint, body, key):
    url, _ = PROVIDERS[endpoint]
    request = urllib.request.Request(url, body, {
        "Content-Type": "application/json",
        "Authorization": "Bearer " + key,
    })
    try:
        response = urllib.request.build_opener(NoRedirect()).open(request, timeout=120)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.code, response.read(LIMIT + 1)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def publish_once(path, data):
    with tempfile.NamedTemporaryFile(dir=path.parent) as temporary:
        temporary.write(data)
        temporary.flush()
        os.fsync(temporary.fileno())
        try:
            os.link(temporary.name, path)
        except FileExistsError:
            return False
        directory = os.open(path.parent, os.O_RDONLY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    return True


def receipt(identity, status, body):
    if not 200 <= status <= 599 or len(body) > LIMIT:
        raise ValueError("Invalid provider response")
    return json.dumps({
        "identity": identity, "status": status, "sha256": digest(body),
        "body": base64.b64encode(body).decode("ascii"),
    }, separators=(",", ":")).encode("ascii")


def serve(config_path, source, output, provider=send_provider, environ=os.environ):
    wire = json.loads(source.read(2 * LIMIT + 4096))
    envelope = base64.b64decode(wire["envelope"], validate=True)
    identity = digest(envelope)
    if identity != wire["identity"]:
        raise ValueError("Invalid request identity")
    request = json.loads(envelope)
    endpoint = request["endpoint"]
    body = base64.b64decode(request["body"], validate=True)
    if len(body) > LIMIT or digest(body) != request["hash"]:
        raise ValueError("Invalid request content")
    payload = json.loads(body)
    if (not re.fullmatch(r"[0-9a-f]{32}", request["draft"]) or
            type(request["attempt"]) is not int or request["attempt"] < 0 or
            not isinstance(payload["model"], str) or not payload["model"] or
            payload.get("stream", False) is not False or
            (endpoint == "openai-responses" and payload.get("store") is not False)):
        raise ValueError("Invalid content contract")

    config = json.loads(Path(config_path).read_text(encoding="utf-8"))
    spool = Path(config["spool"])
    authority = config["authority"]
    if (not spool.is_absolute() or not re.fullmatch(r"[0-9a-f]{32}", authority) or
            spool.stat().st_uid != os.getuid() or spool.stat().st_mode & 0o077 or
            request["authority"] != authority or (spool / "authority").read_text().strip() != authority):
        raise ValueError("Invalid spool authority")
    result = spool / (identity + ".response.json")
    claim = spool / (identity + ".claim")
    _, key_name = PROVIDERS[endpoint]
    key = environ.get(key_name)
    if claim.exists():
        if not result.exists():
            return 75
    elif not key:
        return 78
    elif publish_once(claim, identity.encode("ascii")):
        status, response = provider(endpoint, body, key)
        publish_once(result, receipt(identity, status, response))
    elif not result.exists():
        return 75

    with result.open("rb") as saved:
        output.write(saved.read(2 * LIMIT))
    return 0


def main():
    if len(sys.argv) != 2:
        return 64
    os.umask(0o077)
    try:
        return serve(sys.argv[1], sys.stdin.buffer, sys.stdout.buffer)
    except (OSError, ValueError, KeyError, TypeError):
        # Do not echo provider responses, request content or credential material in SSH diagnostics.
        return 75


if __name__ == "__main__":
    sys.exit(main())
