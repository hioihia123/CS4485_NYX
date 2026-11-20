"""Run sample_request.json through the trained V16 scoring code."""

import json
from pathlib import Path

import score

BASE_DIR = Path(__file__).resolve().parent

score.init()
payload = json.loads((BASE_DIR / "sample_request.json").read_text(encoding="utf-8"))
result = json.loads(score.run(payload))
print(json.dumps(result, indent=2))

predictions = result.get("predictions", {})
anomaly_detection = result.get("anomaly_detection", {})

assert "stress_score" in predictions
assert "sleep_efficiency_next_night" in predictions
assert "stress_score" in anomaly_detection
assert "sleep_efficiency_next_night" in anomaly_detection
for target in ("stress_score", "sleep_efficiency_next_night"):
    target_result = anomaly_detection[target]
    assert set(target_result["personal"]).issuperset({"baseline", "deviation", "z_score", "is_anomaly"})
    assert set(target_result["population"]).issuperset({"baseline", "deviation", "z_score", "is_anomaly"})
