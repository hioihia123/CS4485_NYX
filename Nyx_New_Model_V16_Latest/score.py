"""
Nyx Insights V16 - Scoring / API Entry Point

Training creates the feature definitions and model files. This file handles the
other side of the project: loading those saved files, rebuilding the same
features from an API request, and returning the stress and next-night sleep
predictions as JSON.
"""

from __future__ import annotations

import json
import os
from pathlib import Path

import joblib
import numpy as np
import pandas as pd

from nyx_v16_features import (
    HOURLY_RAW_FEATURES,
    RAW_FEATURES,
    request_to_feature_row,
)

# These variables are loaded once when the scoring service starts. Keeping them
# in memory avoids reloading the model files for every prediction request.
_stress_model = None
_sleep_model = None
_metadata = None
_stress_features = None
_sleep_features = None
_sleep_fallback = None


# ============================================================
# SECTION 1: LOAD THE TRAINED MODEL BUNDLE
# ============================================================
# Azure may place the model files in a deployment directory, while local tests
# keep them beside this script. init() supports both locations and reads the
# metadata so scoring uses the exact same feature lists as training.

def init():
    global _stress_model, _sleep_model, _metadata
    global _stress_features, _sleep_features, _sleep_fallback

    default_dir = Path(__file__).resolve().parent / "model_bundle_v16"
    model_dir = Path(os.environ.get("AZUREML_MODEL_DIR", default_dir))

    if not (model_dir / "stress_model.pkl").exists():
        candidates = list(model_dir.rglob("stress_model.pkl"))
        if candidates:
            model_dir = candidates[0].parent

    _stress_model = joblib.load(model_dir / "stress_model.pkl")
    _sleep_model = joblib.load(model_dir / "sleep_model.pkl")

    metadata_path = model_dir / "metadata.json"
    _metadata = json.loads(metadata_path.read_text(encoding="utf-8")) if metadata_path.exists() else {}

    _stress_features = _metadata.get("stress_feature_set")
    _sleep_features = _metadata.get("sleep_feature_set_before_selection")
    _sleep_fallback = float(_metadata.get("sleep_fallback_mean", 90.0))

    if not _stress_features or not _sleep_features:
        raise ValueError("V16 metadata.json is missing the stress/sleep feature sets")


# ============================================================
# SECTION 2: SMALL SCORING HELPERS
# ============================================================

def _ensure_loaded():
    if _stress_model is None:
        init()


def _sleep_baseline_for_row(row):
    # Use the engineered personal baseline when enough history exists. For a new
    # user, fall back to current sleep efficiency and finally the training mean.
    personal = pd.to_numeric(row.get("sleep_personal_baseline"), errors="coerce")
    if isinstance(personal, pd.Series):
        personal = personal.iloc[0]
    if pd.notna(personal):
        return float(personal)

    current = pd.to_numeric(row.get("sleep_efficiency"), errors="coerce")
    if isinstance(current, pd.Series):
        current = current.iloc[0]
    if pd.notna(current):
        return float(current)

    return float(_sleep_fallback)


# ============================================================
# SECTION 3: BUILD FEATURES AND RETURN A PREDICTION
# ============================================================
# run() is the function Azure calls. It rebuilds one feature row from the JSON
# request, predicts stress directly, predicts the sleep change from baseline,
# then packages the results and a small input summary back into JSON.

def run(raw_data):
    try:
        _ensure_loaded()

        payload = json.loads(raw_data) if isinstance(raw_data, str) else raw_data
        input_data = payload.get("input_data", payload)
        row = request_to_feature_row(input_data)

        # Stress is predicted directly on the 1-5 survey scale.
        stress = float(np.clip(
            _stress_model.predict(row[_stress_features])[0],
            1.0,
            5.0,
        ))

        # Sleep is different: the model predicts a change from the user's normal
        # sleep level, so we add that change back to the personal baseline.
        sleep_base = _sleep_baseline_for_row(row)
        sleep_delta = float(_sleep_model.predict(row[_sleep_features])[0])
        sleep = float(np.clip(sleep_base + sleep_delta, 0.0, 100.0))

        current = input_data.get("current") or {}
        current_daily = current.get("daily") or {}
        current_hourly = current.get("hourly") or []
        history = input_data.get("history") or []

        # The remaining code does not affect the prediction. It records what the
        # request contained so the API response is easier to debug and explain.
        core_provided = [
            name for name in RAW_FEATURES
            if current_daily.get(name) is not None
        ]

        hourly_types_seen = sorted({
            name
            for record in current_hourly
            if isinstance(record, dict)
            for name in HOURLY_RAW_FEATURES
            if record.get(name) is not None
        })

        prior_surveys = 0
        for item in history:
            if not isinstance(item, dict):
                continue
            if item.get("survey_stress_score") is not None:
                prior_surveys += 1
            elif any(item.get(k) is not None for k in [
                "RESTED/RELAXED", "HAPPY", "NEUTRAL", "ALERT",
                "TIRED", "SAD", "TENSE/ANXIOUS"
            ]):
                prior_surveys += 1

        return json.dumps(
            {
                "model_version": _metadata.get("model_version", "16.0"),
                "predictions": {
                    "stress_score": stress,
                    "sleep_efficiency_next_night": sleep,
                },
                "sleep_prediction_details": {
                    "personal_sleep_baseline": sleep_base,
                    "predicted_change_from_baseline": sleep_delta,
                    "prediction_rule": "baseline + predicted delta",
                },
                "selected_inputs": {
                    "core_v1_fitbit_inputs": RAW_FEATURES,
                    "added_fitbit_metrics": ["sleep_efficiency"],
                    "prior_survey_history_used": True,
                    "hourly_data_uses_only_v1_measurement_types": True,
                },
                "input_summary": {
                    "provided_current_core_features": core_provided,
                    "current_sleep_efficiency_provided": current_daily.get("sleep_efficiency") is not None,
                    "current_hourly_records_received": len(current_hourly),
                    "hourly_measurement_types_seen": hourly_types_seen,
                    "history_days_received": len(history),
                    "prior_survey_scores_received": prior_surveys,
                },
            },
            indent=2,
        )

    except Exception as exc:
        # Return an error as JSON so local tests and the API receive a readable
        # message instead of the scoring process crashing without context.
        return json.dumps({"error": str(exc)}, indent=2)
