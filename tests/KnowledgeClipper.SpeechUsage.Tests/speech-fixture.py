"""Transport-only fixture: output is text, not synthesized audio."""
import json
import pathlib
import sys

if "--prepare" in sys.argv:
    print(json.dumps({"event": "helperReady"}), flush=True)
request = json.load(sys.stdin)
audio = pathlib.Path(request["audio"])
if not request["replay"]:
    assert request["style"], "new synthesis must supply reading instructions separately from text"
    if any("\u4e00" <= char <= "\u9fff" for char in request["text"]):
        assert request["provider"] == "mimo"
        assert "标准普通话" in request["style"]
    else:
        assert request["provider"] == "openai"
        assert "American English" in request["style"]
    audio.write_text(request["text"], encoding="utf-8")
    print(json.dumps({"event": "audioComplete"}), flush=True)
else:
    assert audio.exists()
    assert request.get("provider") is None, "replay must not select a synthesis provider"
print(json.dumps({"event": "playbackComplete"}), flush=True)
