"""Run on the isolated Mac host against the production SSH helper with no provider calls."""

import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys


os.umask(0o077)
root = Path(__file__).parent
spec = importlib.util.spec_from_file_location("desktop_content_ssh", root / "desktop_content_ssh.py")
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)
invocations = root / "invocations"
sequence = int(invocations.read_text()) + 1 if invocations.exists() else 1
invocations.write_text(str(sequence))
if sequence == 1:
    sys.exit(75)


def provider(endpoint, body, key):
    payload = json.loads(body)
    captured = json.loads(payload["input"][-1]["content"])
    assert endpoint == "openai-responses" and payload["model"] == "gpt-6-luna"
    assert payload["text"]["format"]["type"] == "json_object" and payload["reasoning"]["effort"] == "low"
    assert captured["selectedText"] == "lead" and "lead" in captured["contextText"]
    with (root / "provider-calls.jsonl").open("a", encoding="utf-8") as log:
        log.write(json.dumps({"model": payload["model"], "requestSha256": hashlib.sha256(body).hexdigest()}) + "\n")
    fields = dict(status="ready", pronunciation="/liːd/", explanationEn="to guide or direct",
                  sentenceTranslationZh="我们每天练习 lead。", normalizedSentence=None, generatedSentence=None,
                  generatedTargetStart=None, unresolvedReasons=[], lemma="lead")
    response = dict(status="completed", output=[dict(type="message", content=[
        dict(type="output_text", text=json.dumps(fields, ensure_ascii=False))])])
    return 200, json.dumps(response, ensure_ascii=False).encode("utf-8")


output = io.BytesIO()
status = worker.serve(sys.argv[1], sys.stdin.buffer, output, provider, {"OPENAI_KNOWLEDGECLIPPER_API_KEY": "fixture"})
if sequence == 2:
    assert status == 0
    sys.exit(75)
sys.stdout.buffer.write(output.getvalue())
sys.exit(status)
