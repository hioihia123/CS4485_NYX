"""Run sample_request.json through the trained V16 scoring code."""

import copy
import json
from pathlib import Path

import score

BASE_DIR = Path(__file__).resolve().parent

score.init()
payload = json.loads((BASE_DIR / "sample_request.json").read_text(encoding="utf-8"))
result = json.loads(score.run(payload))
print(json.dumps(result, indent=2))

assert "error" not in result, result.get("error")
predictions = result["predictions"]
anomaly_detection = result["anomaly_detection"]

assert "stress_score" in predictions
assert "sleep_efficiency_next_night" in predictions
assert anomaly_detection["personal_window_days"] == 7

PERSONAL_KEYS = {
    "available", "window_start", "window_end", "days_used", "baseline", "std",
    "std_used", "deviation", "percent_change", "z_score", "is_anomaly", "direction",
}
POPULATION_KEYS = {
    "available", "baseline", "std", "deviation", "z_score", "percentile",
    "is_anomaly", "direction",
}
for target in ("stress_score", "sleep_efficiency_next_night"):
    target_result = anomaly_detection[target]
    assert target_result["predicted_value"] == predictions[target]
    assert set(target_result["personal_7day"]).issuperset(PERSONAL_KEYS)
    assert set(target_result["population"]).issuperset(POPULATION_KEYS)
    assert target_result["population"]["available"]
    assert 0.0 <= target_result["population"]["percentile"] <= 100.0

# Sample history: surveys 2.5/3.0/2.0/3.5 on the 4 days before 2026-11-16, and
# sleep 95/91/96/89 plus the current day's 93.
stress_personal = anomaly_detection["stress_score"]["personal_7day"]
sleep_personal = anomaly_detection["sleep_efficiency_next_night"]["personal_7day"]
assert stress_personal["days_used"] == 4
assert abs(stress_personal["baseline"] - 2.75) < 1e-9
assert stress_personal["window_end"] == "2026-11-15"
assert sleep_personal["days_used"] == 5
assert abs(sleep_personal["baseline"] - 92.8) < 1e-9
assert sleep_personal["window_end"] == "2026-11-16"


# --- Unit checks on the anomaly helpers (independent of model predictions) ---

def personal(value, request, target):
    return score._personal_comparison(value, request, target, score._anomaly_config)


payload = payload.get("input_data", payload)


# Fewer than 3 days in the window -> no personal baseline.
short = copy.deepcopy(payload)
short["history"] = short["history"][:2]
check = personal(3.0, short, "stress_score")
assert not check["available"] and check["baseline"] is None and check["direction"] == "unknown"

# Days older than 7 days before the predicted day are ignored.
stale = copy.deepcopy(payload)
for item in stale["history"]:
    item["date"] = "2026-10-01"
assert personal(3.0, stale, "stress_score")["days_used"] == 0

# Flat history -> std floor prevents a divide-by-zero / exploding z-score.
flat = copy.deepcopy(payload)
for item in flat["history"]:
    item["survey_stress_score"] = 2.0
check = personal(2.4, flat, "stress_score")
assert check["std"] == 0.0 and check["std_used"] == 0.25
assert abs(check["z_score"] - 1.6) < 1e-9 and not check["is_anomaly"]

# A clear spike above the weekly baseline is flagged high.
check = personal(4.5, flat, "stress_score")
assert check["is_anomaly"] and check["direction"] == "high"

# A poor night relative to both the week and the population is flagged low.
report = score._anomaly_report(70.0, payload, "sleep_efficiency_next_night")
assert report["is_anomaly"]
assert report["personal_7day"]["direction"] == "low"
assert report["population"]["direction"] == "low"
assert report["population"]["percentile"] < 5

print("\nAll V16 anomaly-detection checks passed.")
