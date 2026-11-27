"""Clipper's per-request speech transport in the application's existing Python."""
import sys
sys.path.insert(0, r'D:\Projects\MimoTTS\src')
from mimo_tts.adapters import main
if __name__ == "__main__":
    raise SystemExit(main(require_provider=True))
