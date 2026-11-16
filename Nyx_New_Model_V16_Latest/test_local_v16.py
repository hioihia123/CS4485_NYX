"""Run sample_request.json through the trained V16 scoring code."""

import json
from pathlib import Path

import score

BASE_DIR = Path(__file__).resolve().parent

score.init()
payload = json.loads((BASE_DIR / "sample_request.json").read_text(encoding="utf-8"))
print(score.run(payload))
