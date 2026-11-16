"""
Nyx Insights - V16 Feature Engineering

This file turns the cleaned Fitbit and survey data into the features used by
V16. The main rules are:
- keep the original 12 Fitbit measurement types from Version 1
- use hourly versions only when they are the same measurement type
- add sleep_efficiency because V15 showed that it improved both targets
- use only earlier surveys and earlier days when creating history features
- create extra sleep-history features for the personalized sleep model

The goal is to give the models useful history without leaking future data into
training or requiring a large number of extra Fitbit sensors.
"""

from __future__ import annotations

import warnings

import numpy as np
import pandas as pd
from pandas.errors import PerformanceWarning

warnings.filterwarnings("ignore", category=PerformanceWarning)


# ============================================================
# SECTION 1: CORE FITBIT INPUTS
# ============================================================
# These are the same 12 Fitbit measurement types used by Version 1. I keep this
# list separate because it defines the basic deployment contract for the model.

RAW_FEATURES = [
    "bpm",
    "mindfulness_session",
    "exertion_points_percentage",
    "very_active_minutes",
    "moderately_active_minutes",
    "lightly_active_minutes",
    "sedentary_minutes",
    "minutes_in_default_zone_1",
    "minutes_in_default_zone_2",
    "minutes_in_default_zone_3",
    "steps",
    "calories",
]

# Only these core measurements are also available in the hourly dataset. Hourly
# temperature, SCL, distance, and below-zone-1 minutes are intentionally left
# out because they would introduce measurement types outside the V1 contract.
HOURLY_RAW_FEATURES = [
    "bpm",
    "mindfulness_session",
    "steps",
    "calories",
    "minutes_in_default_zone_1",
    "minutes_in_default_zone_2",
    "minutes_in_default_zone_3",
]


# ============================================================
# SECTION 1B: ONE ADDITIONAL FITBIT METRIC KEPT FROM V15
# ============================================================
# V15 tested several possible extra measurements. sleep_efficiency was the only
# one selected for both stress and sleep, so V16 keeps it as the single added
# Fitbit measurement instead of repeating the metric-selection experiment.

ADDED_METRICS = [
    "sleep_efficiency",
]

ADDED_METRIC_UNITS = {
    "sleep_efficiency": "percent",
}


# ============================================================
# SECTION 2: TURN THE SURVEY INTO THE STRESS TARGET
# ============================================================
# The participant's mood survey is the ground truth for stress. Each selected
# mood is mapped to a value from 1 to 5 and multiple selected moods are averaged.
# The current survey is only the answer we train against; it is never an input.

SURVEY_WEIGHTS = {
    "RESTED/RELAXED": 1,
    "HAPPY": 2,
    "NEUTRAL": 3,
    "ALERT": 3,
    "TIRED": 4,
    "SAD": 4,
    "TENSE/ANXIOUS": 5,
}


def calculate_survey_stress(record) -> float:
    """Convert one completed mood survey into the Nyx continuous 1-5 target."""
    selected = []

    for field, score in SURVEY_WEIGHTS.items():
        value = record.get(field)
        try:
            numeric = float(value)
        except (TypeError, ValueError):
            continue

        if np.isnan(numeric) or numeric <= 0:
            continue

        selected.extend([float(score)] * max(1, int(round(numeric))))

    if not selected:
        return np.nan

    return float(np.mean(selected))


# ============================================================
# SECTION 3: CLEAN THE RAW VALUES
# ============================================================
# Most Fitbit columns should already be numeric, but converting them here keeps
# the feature-building code consistent. mindfulness_session can arrive as a
# boolean, so it is converted to 0/1 before the model sees it.


def _numeric_series(series: pd.Series, feature: str) -> pd.Series:
    if feature == "mindfulness_session":
        text = series.astype("string").str.strip().str.lower()
        mapped = text.map({"true": 1.0, "false": 0.0, "1": 1.0, "0": 0.0})
        numeric = pd.to_numeric(series, errors="coerce")
        return mapped.fillna(numeric).astype(float)

    return pd.to_numeric(series, errors="coerce")


def normalize_daily_features(frame: pd.DataFrame) -> pd.DataFrame:
    out = frame.copy()
    for feature in RAW_FEATURES + ADDED_METRICS:
        if feature not in out.columns:
            out[feature] = np.nan
        out[feature] = _numeric_series(out[feature], feature)
    return out


def normalize_hourly_features(frame: pd.DataFrame) -> pd.DataFrame:
    out = frame.copy()
    if "hour" not in out.columns:
        out["hour"] = np.nan
    out["hour"] = pd.to_numeric(out["hour"], errors="coerce")

    for feature in HOURLY_RAW_FEATURES:
        if feature not in out.columns:
            out[feature] = np.nan
        out[feature] = _numeric_series(out[feature], feature)
    return out


# ============================================================
# SECTION 4: CREATE THE TWO TRAINING TARGETS
# ============================================================
# Stress uses today's completed survey as the target. Sleep uses the NEXT
# calendar day's sleep_efficiency as the target. Today's sleep_efficiency may
# still be used as a predictor because it is already known before the next night
# happens; the future sleep value itself is never used as an input.


def add_training_targets(frame: pd.DataFrame) -> pd.DataFrame:
    out = frame.copy()
    out["survey_stress_target"] = out.apply(calculate_survey_stress, axis=1)

    next_date = out.groupby("id", sort=False)["date"].shift(-1)
    next_sleep = out.groupby("id", sort=False)["sleep_efficiency"].shift(-1)
    is_next_calendar_day = next_date.eq(out["date"] + pd.Timedelta(days=1))

    out["sleep_target"] = pd.to_numeric(next_sleep, errors="coerce").where(
        is_next_calendar_day
    )
    return out


# ============================================================
# SECTION 5: BUILD DAILY HISTORY FEATURES
# ============================================================
# For each core Fitbit measurement, I calculate recent averages, variability,
# and a personal baseline. shift(1) is used before rolling calculations so the
# history for a row contains only earlier days, not the current or future day.

ROLLING_WINDOWS = (3, 7, 14, 30)
STD_WINDOWS = (7, 14, 30)


def daily_history_feature_names() -> list[str]:
    names = list(RAW_FEATURES)
    for feature in RAW_FEATURES:
        for window in ROLLING_WINDOWS:
            names.append(f"{feature}_previous_{window}day_mean")
        for window in STD_WINDOWS:
            names.append(f"{feature}_previous_{window}day_std")
        names.extend(
            [
                f"{feature}_vs_previous_7day_mean",
                f"{feature}_previous_7day_vs_30day_mean",
                f"{feature}_personal_baseline",
                f"{feature}_vs_personal_baseline",
            ]
        )
    return names


DAILY_HISTORY_FEATURES = daily_history_feature_names()


def add_daily_historical_features(frame: pd.DataFrame) -> pd.DataFrame:
    out = normalize_daily_features(frame)
    out = out.sort_values(["id", "date"]).copy()

    for feature in RAW_FEATURES:
        grouped = out.groupby("id", sort=False)[feature]

        for window in ROLLING_WINDOWS:
            out[f"{feature}_previous_{window}day_mean"] = grouped.transform(
                lambda s, w=window: s.shift(1).rolling(w, min_periods=1).mean()
            )

        for window in STD_WINDOWS:
            out[f"{feature}_previous_{window}day_std"] = grouped.transform(
                lambda s, w=window: s.shift(1).rolling(w, min_periods=2).std(ddof=0)
            )

        mean7 = out[f"{feature}_previous_7day_mean"]
        mean30 = out[f"{feature}_previous_30day_mean"]
        out[f"{feature}_vs_previous_7day_mean"] = out[feature] - mean7
        out[f"{feature}_previous_7day_vs_30day_mean"] = mean7 - mean30

        out[f"{feature}_personal_baseline"] = grouped.transform(
            lambda s: s.shift(1).expanding(min_periods=1).mean()
        )
        out[f"{feature}_vs_personal_baseline"] = (
            out[feature] - out[f"{feature}_personal_baseline"]
        )

    return out


# ============================================================
# SECTION 6: SUMMARIZE THE HOURLY DATA
# ============================================================
# The hourly file contains the same measurements at a finer time resolution.
# This section turns those rows into useful daily patterns such as heart-rate
# variability, morning/evening BPM, active hours, and steps by time of day.

TIME_BUCKETS = {
    "night": (0, 6),
    "morning": (6, 12),
    "afternoon": (12, 18),
    "evening": (18, 24),
}


def _bucket_mask(hours: pd.Series, start: int, end: int) -> pd.Series:
    return hours.ge(start) & hours.lt(end)


def summarize_hourly_day(day: pd.DataFrame) -> dict:
    """Turn one participant-day of hourly rows into V16 time-of-day features."""
    if day is None or day.empty:
        return {}

    d = normalize_hourly_features(day)
    hours = d["hour"]
    result = {"hourly_hours_observed": float(hours.dropna().nunique())}

    # Heart rate distribution and time-of-day pattern.
    bpm = d["bpm"]
    valid_bpm = bpm.dropna()
    result.update(
        {
            "hourly_bpm_mean": valid_bpm.mean(),
            "hourly_bpm_std": valid_bpm.std(ddof=0),
            "hourly_bpm_min": valid_bpm.min(),
            "hourly_bpm_max": valid_bpm.max(),
            "hourly_bpm_median": valid_bpm.median(),
            "hourly_bpm_p10": valid_bpm.quantile(0.10) if len(valid_bpm) else np.nan,
            "hourly_bpm_p90": valid_bpm.quantile(0.90) if len(valid_bpm) else np.nan,
            "hourly_bpm_range": (valid_bpm.max() - valid_bpm.min()) if len(valid_bpm) else np.nan,
            "hourly_bpm_coverage_hours": float(bpm.notna().sum()),
        }
    )

    for label, (start, end) in TIME_BUCKETS.items():
        mask = _bucket_mask(hours, start, end)
        result[f"hourly_bpm_{label}_mean"] = d.loc[mask, "bpm"].mean()

    # Steps and calories are additive, so time-of-day totals are useful.
    for feature in ("steps", "calories"):
        values = d[feature]
        valid = values.dropna()
        result[f"hourly_{feature}_sum"] = valid.sum(min_count=1)
        result[f"hourly_{feature}_mean"] = valid.mean()
        result[f"hourly_{feature}_std"] = valid.std(ddof=0)
        result[f"hourly_{feature}_max"] = valid.max()
        result[f"hourly_{feature}_coverage_hours"] = float(values.notna().sum())

        for label, (start, end) in TIME_BUCKETS.items():
            mask = _bucket_mask(hours, start, end)
            result[f"hourly_{feature}_{label}_sum"] = d.loc[mask, feature].sum(min_count=1)

    # Mindfulness is the same V1 boolean/session input, only timestamped.
    mindfulness = d["mindfulness_session"]
    result["hourly_mindfulness_sum"] = mindfulness.sum(min_count=1)
    result["hourly_mindfulness_any"] = (
        float((mindfulness.fillna(0) > 0).any()) if mindfulness.notna().any() else np.nan
    )
    result["hourly_mindfulness_coverage_hours"] = float(mindfulness.notna().sum())

    # Only V1 zones 1/2/3 are used. Below-zone-1 is intentionally excluded.
    for zone in (1, 2, 3):
        feature = f"minutes_in_default_zone_{zone}"
        values = d[feature]
        valid = values.dropna()
        prefix = f"hourly_{feature}"
        result[f"{prefix}_sum"] = valid.sum(min_count=1)
        result[f"{prefix}_mean"] = valid.mean()
        result[f"{prefix}_std"] = valid.std(ddof=0)
        result[f"{prefix}_max"] = valid.max()
        result[f"{prefix}_coverage_hours"] = float(values.notna().sum())
        for label, (start, end) in TIME_BUCKETS.items():
            mask = _bucket_mask(hours, start, end)
            result[f"{prefix}_{label}_sum"] = d.loc[mask, feature].sum(min_count=1)

    # Derived only from steps and V1 zones.
    result["hourly_active_hours"] = float((d["steps"].fillna(0) > 0).sum())
    result["hourly_zero_step_hours"] = float((d["steps"].fillna(0) == 0).sum())
    high_zone = (
        d["minutes_in_default_zone_2"].fillna(0)
        + d["minutes_in_default_zone_3"].fillna(0)
    )
    result["hourly_high_zone_minutes_sum"] = float(high_zone.sum())
    result["hourly_high_zone_hours"] = float(high_zone.gt(0).sum())

    return result


def hourly_feature_names() -> list[str]:
    # Generate names from a dummy day so training and scoring stay synchronized.
    dummy = pd.DataFrame(
        {
            "hour": [0, 8, 14, 20],
            "bpm": [60, 70, 80, 75],
            "mindfulness_session": [0, 0, 1, 0],
            "steps": [0, 100, 500, 200],
            "calories": [60, 75, 110, 90],
            "minutes_in_default_zone_1": [0, 1, 2, 1],
            "minutes_in_default_zone_2": [0, 0, 1, 0],
            "minutes_in_default_zone_3": [0, 0, 0, 0],
        }
    )
    return list(summarize_hourly_day(dummy).keys())


HOURLY_FEATURES = hourly_feature_names()


def aggregate_hourly_dataframe(hourly: pd.DataFrame) -> pd.DataFrame:
    """Summarize hourly_clean.csv to one row per participant/date efficiently."""
    if hourly is None or hourly.empty:
        return pd.DataFrame(columns=["id", "date", *HOURLY_FEATURES])

    required = {"id", "date", "hour"}
    missing = required.difference(hourly.columns)
    if missing:
        raise ValueError(f"hourly_clean.csv is missing required columns: {sorted(missing)}")

    h = hourly.copy()
    h["date"] = pd.to_datetime(h["date"], errors="coerce")
    h = h[h["date"].notna()].copy()
    h = normalize_hourly_features(h)
    keys = ["id", "date"]

    # Calculate the day-level hourly summaries for each participant and date.
    grouped = h.groupby(keys, sort=False)
    result = grouped["hour"].nunique().rename("hourly_hours_observed").to_frame()

    bpm = grouped["bpm"].agg(["mean", "std", "min", "max", "median", "count"])
    bpm.columns = [
        "hourly_bpm_mean",
        "hourly_bpm_std",
        "hourly_bpm_min",
        "hourly_bpm_max",
        "hourly_bpm_median",
        "hourly_bpm_coverage_hours",
    ]
    result = result.join(bpm)
    result["hourly_bpm_std"] = result["hourly_bpm_std"].fillna(0.0)
    result["hourly_bpm_range"] = result["hourly_bpm_max"] - result["hourly_bpm_min"]
    result["hourly_bpm_p10"] = grouped["bpm"].quantile(0.10)
    result["hourly_bpm_p90"] = grouped["bpm"].quantile(0.90)

    for label, (start, end) in TIME_BUCKETS.items():
        mask = _bucket_mask(h["hour"], start, end)
        bucket = h.loc[mask].groupby(keys, sort=False)["bpm"].mean()
        result[f"hourly_bpm_{label}_mean"] = bucket

    for feature in ("steps", "calories"):
        agg = grouped[feature].agg(["sum", "mean", "std", "max", "count"])
        agg.columns = [
            f"hourly_{feature}_sum",
            f"hourly_{feature}_mean",
            f"hourly_{feature}_std",
            f"hourly_{feature}_max",
            f"hourly_{feature}_coverage_hours",
        ]
        result = result.join(agg)
        result[f"hourly_{feature}_std"] = result[f"hourly_{feature}_std"].fillna(0.0)
        for label, (start, end) in TIME_BUCKETS.items():
            mask = _bucket_mask(h["hour"], start, end)
            bucket = h.loc[mask].groupby(keys, sort=False)[feature].sum(min_count=1)
            result[f"hourly_{feature}_{label}_sum"] = bucket

    mindful = grouped["mindfulness_session"].agg(["sum", "max", "count"])
    mindful.columns = [
        "hourly_mindfulness_sum",
        "hourly_mindfulness_any",
        "hourly_mindfulness_coverage_hours",
    ]
    result = result.join(mindful)

    for zone in (1, 2, 3):
        feature = f"minutes_in_default_zone_{zone}"
        prefix = f"hourly_{feature}"
        agg = grouped[feature].agg(["sum", "mean", "std", "max", "count"])
        agg.columns = [
            f"{prefix}_sum",
            f"{prefix}_mean",
            f"{prefix}_std",
            f"{prefix}_max",
            f"{prefix}_coverage_hours",
        ]
        result = result.join(agg)
        result[f"{prefix}_std"] = result[f"{prefix}_std"].fillna(0.0)
        for label, (start, end) in TIME_BUCKETS.items():
            mask = _bucket_mask(h["hour"], start, end)
            bucket = h.loc[mask].groupby(keys, sort=False)[feature].sum(min_count=1)
            result[f"{prefix}_{label}_sum"] = bucket

    temp = h[keys].copy()
    temp["active"] = h["steps"].fillna(0).gt(0).astype(float)
    temp["zero"] = h["steps"].fillna(0).eq(0).astype(float)
    temp["high_zone"] = (
        h["minutes_in_default_zone_2"].fillna(0)
        + h["minutes_in_default_zone_3"].fillna(0)
    )
    temp["high_zone_hour"] = temp["high_zone"].gt(0).astype(float)
    derived = temp.groupby(keys, sort=False).agg(
        hourly_active_hours=("active", "sum"),
        hourly_zero_step_hours=("zero", "sum"),
        hourly_high_zone_minutes_sum=("high_zone", "sum"),
        hourly_high_zone_hours=("high_zone_hour", "sum"),
    )
    result = result.join(derived)

    result = result.reset_index()
    for name in HOURLY_FEATURES:
        if name not in result.columns:
            result[name] = np.nan
    return result[["id", "date", *HOURLY_FEATURES]]


# ============================================================
# SECTION 7: ADD HISTORY TO THE MOST USEFUL HOURLY SUMMARIES
# ============================================================
# Creating rolling history for every hourly summary would add many repetitive
# columns. I keep a smaller set of meaningful hourly summaries and calculate
# their recent averages and personal baselines over earlier days.

HOURLY_HISTORY_BASE = [
    "hourly_bpm_mean",
    "hourly_bpm_std",
    "hourly_bpm_max",
    "hourly_bpm_evening_mean",
    "hourly_steps_sum",
    "hourly_active_hours",
    "hourly_calories_sum",
    "hourly_high_zone_minutes_sum",
]


def hourly_history_feature_names() -> list[str]:
    names = []
    for feature in HOURLY_HISTORY_BASE:
        for window in ROLLING_WINDOWS:
            names.append(f"{feature}_previous_{window}day_mean")
        names.extend(
            [
                f"{feature}_previous_7day_std",
                f"{feature}_vs_previous_7day_mean",
                f"{feature}_personal_baseline",
                f"{feature}_vs_personal_baseline",
            ]
        )
    return names


HOURLY_HISTORY_FEATURES = hourly_history_feature_names()


def add_hourly_historical_features(frame: pd.DataFrame) -> pd.DataFrame:
    out = frame.sort_values(["id", "date"]).copy()

    for feature in HOURLY_HISTORY_BASE:
        if feature not in out.columns:
            out[feature] = np.nan
        out[feature] = pd.to_numeric(out[feature], errors="coerce")
        grouped = out.groupby("id", sort=False)[feature]

        for window in ROLLING_WINDOWS:
            out[f"{feature}_previous_{window}day_mean"] = grouped.transform(
                lambda s, w=window: s.shift(1).rolling(w, min_periods=1).mean()
            )

        out[f"{feature}_previous_7day_std"] = grouped.transform(
            lambda s: s.shift(1).rolling(7, min_periods=2).std(ddof=0)
        )
        mean7 = out[f"{feature}_previous_7day_mean"]
        out[f"{feature}_vs_previous_7day_mean"] = out[feature] - mean7
        out[f"{feature}_personal_baseline"] = grouped.transform(
            lambda s: s.shift(1).expanding(min_periods=1).mean()
        )
        out[f"{feature}_vs_personal_baseline"] = (
            out[feature] - out[f"{feature}_personal_baseline"]
        )

    return out


# ============================================================
# SECTION 7B: BUILD PERSONAL STRESS HISTORY FROM EARLIER SURVEYS
# ============================================================
# Previous completed surveys help the stress model learn what is normal for a
# specific person. The current survey is appended to history only AFTER the
# feature values for that date have been created, which prevents target leakage.

REAL_SURVEY_HISTORY_FEATURES = [
    "previous_real_survey_stress",
    "previous_real_survey_stress_2",
    "previous_real_survey_stress_3",
    "previous_3real_survey_mean",
    "previous_5real_survey_mean",
    "previous_7real_survey_mean",
    "previous_10real_survey_mean",
    "previous_3real_survey_std",
    "previous_5real_survey_std",
    "previous_10real_survey_std",
    "previous_real_survey_ewm3",
    "previous_real_survey_ewm5",
    "stress_personal_baseline",
    "stress_baseline_count",
    "previous_real_survey_vs_personal_baseline",
    "previous_real_survey_trend",
    "days_since_previous_real_survey",
    "real_survey_history_count",
]


def add_real_survey_history_features(frame: pd.DataFrame) -> pd.DataFrame:
    out = frame.sort_values(["id", "date"]).copy()
    for name in REAL_SURVEY_HISTORY_FEATURES:
        out[name] = np.nan

    for participant, idx in out.groupby("id", sort=False).groups.items():
        scores = []
        dates = []
        ewm3 = np.nan
        ewm5 = np.nan

        for row_idx in idx:
            current_date = out.at[row_idx, "date"]
            n = len(scores)
            out.at[row_idx, "real_survey_history_count"] = float(n)

            if n:
                out.at[row_idx, "previous_real_survey_stress"] = scores[-1]
                if n >= 2:
                    out.at[row_idx, "previous_real_survey_stress_2"] = scores[-2]
                    out.at[row_idx, "previous_real_survey_trend"] = scores[-1] - scores[-2]
                if n >= 3:
                    out.at[row_idx, "previous_real_survey_stress_3"] = scores[-3]

                for window in (3, 5, 7, 10):
                    values = scores[-window:]
                    out.at[row_idx, f"previous_{window}real_survey_mean"] = float(np.mean(values))
                for window in (3, 5, 10):
                    values = scores[-window:]
                    if len(values) >= 2:
                        out.at[row_idx, f"previous_{window}real_survey_std"] = float(np.std(values, ddof=0))

                out.at[row_idx, "previous_real_survey_ewm3"] = ewm3
                out.at[row_idx, "previous_real_survey_ewm5"] = ewm5

                personal = scores[-10:]
                baseline = float(np.mean(personal))
                out.at[row_idx, "stress_personal_baseline"] = baseline
                out.at[row_idx, "stress_baseline_count"] = float(len(personal))
                out.at[row_idx, "previous_real_survey_vs_personal_baseline"] = scores[-1] - baseline

                try:
                    out.at[row_idx, "days_since_previous_real_survey"] = float((current_date - dates[-1]).days)
                except Exception:
                    pass
            else:
                out.at[row_idx, "stress_baseline_count"] = 0.0

            current_score = pd.to_numeric(pd.Series([out.at[row_idx, "survey_stress_target"]]), errors="coerce").iloc[0]
            if pd.notna(current_score):
                score = float(current_score)
                scores.append(score)
                dates.append(current_date)
                ewm3 = score if np.isnan(ewm3) else (0.5 * score + 0.5 * ewm3)
                # Update the longer-term survey average while giving newer
                # surveys more weight than older ones.
                ewm5 = score if np.isnan(ewm5) else ((1.0/3.0) * score + (2.0/3.0) * ewm5)

    return out


# ============================================================
# SECTION 7C: BUILD HISTORY FOR THE ADDED SLEEP-EFFICIENCY INPUT
# ============================================================
# sleep_efficiency gets the same rolling means, standard deviations, and
# personal-baseline features as the core Fitbit values. This lets the model use
# both the current sleep result and how it compares with that person's history.


def metric_feature_block(metric: str) -> list[str]:
    names = [metric]
    for window in ROLLING_WINDOWS:
        names.append(f"{metric}_previous_{window}day_mean")
    for window in STD_WINDOWS:
        names.append(f"{metric}_previous_{window}day_std")
    names.extend([
        f"{metric}_vs_previous_7day_mean",
        f"{metric}_previous_7day_vs_30day_mean",
        f"{metric}_personal_baseline",
        f"{metric}_vs_personal_baseline",
    ])
    return names


ADDED_METRIC_FEATURE_BLOCKS = {metric: metric_feature_block(metric) for metric in ADDED_METRICS}


def add_candidate_metric_history_features(frame: pd.DataFrame) -> pd.DataFrame:
    out = frame.sort_values(["id", "date"]).copy()
    for metric in ADDED_METRICS:
        if metric not in out.columns:
            out[metric] = np.nan
        out[metric] = pd.to_numeric(out[metric], errors="coerce")
        grouped = out.groupby("id", sort=False)[metric]

        for window in ROLLING_WINDOWS:
            out[f"{metric}_previous_{window}day_mean"] = grouped.transform(
                lambda x, w=window: x.shift(1).rolling(w, min_periods=1).mean()
            )
        for window in STD_WINDOWS:
            out[f"{metric}_previous_{window}day_std"] = grouped.transform(
                lambda x, w=window: x.shift(1).rolling(w, min_periods=2).std(ddof=0)
            )

        mean7 = out[f"{metric}_previous_7day_mean"]
        mean30 = out[f"{metric}_previous_30day_mean"]
        out[f"{metric}_vs_previous_7day_mean"] = out[metric] - mean7
        out[f"{metric}_previous_7day_vs_30day_mean"] = mean7 - mean30
        out[f"{metric}_personal_baseline"] = grouped.transform(
            lambda x: x.shift(1).expanding(min_periods=1).mean()
        )
        out[f"{metric}_vs_personal_baseline"] = out[metric] - out[f"{metric}_personal_baseline"]

    return out




# ============================================================
# SECTION 7D: ADD THE SLEEP FEATURES USED BY THE PERSONAL BASELINE MODEL
# ============================================================
# The sleep model needs a stronger sense of the person's normal sleep pattern.
# These features add exact 1/2/3-night lags plus short and long exponentially
# weighted averages. All of them are built from earlier dates only.

SLEEP_PERSONALIZATION_FEATURES = [
    "previous_1day_sleep_efficiency",
    "previous_2day_sleep_efficiency",
    "previous_3day_sleep_efficiency",
    "previous_sleep_efficiency_ewm3",
    "previous_sleep_efficiency_ewm7",
]


def add_sleep_personalization_features(frame: pd.DataFrame) -> pd.DataFrame:
    out = frame.sort_values(["id", "date"]).copy()
    sleep = pd.to_numeric(out["sleep_efficiency"], errors="coerce")
    grouped = sleep.groupby(out["id"], sort=False)

    for lag in (1, 2, 3):
        out[f"previous_{lag}day_sleep_efficiency"] = grouped.shift(lag)

    # EWM gives more weight to recent nights than older nights. shift(1) makes
    # sure today's value is not included when building today's history.
    out["previous_sleep_efficiency_ewm3"] = grouped.transform(
        lambda s: s.shift(1).ewm(span=3, adjust=False, min_periods=1).mean()
    )
    out["previous_sleep_efficiency_ewm7"] = grouped.transform(
        lambda s: s.shift(1).ewm(span=7, adjust=False, min_periods=1).mean()
    )

    # Prefer a longer personal history when it exists because it is more stable.
    # For a new user with very little history, fall back to shorter windows, the
    # previous night, and finally the current sleep value as a cold-start value.
    candidates = [
        out.get("sleep_efficiency_previous_30day_mean"),
        out.get("sleep_efficiency_previous_14day_mean"),
        out.get("sleep_efficiency_previous_7day_mean"),
        out.get("sleep_efficiency_previous_3day_mean"),
        out.get("previous_1day_sleep_efficiency"),
        out.get("sleep_efficiency"),
    ]

    baseline = pd.Series(np.nan, index=out.index, dtype=float)
    for candidate in candidates:
        if candidate is None:
            continue
        numeric = pd.to_numeric(candidate, errors="coerce")
        baseline = baseline.fillna(numeric)

    out["sleep_personal_baseline"] = baseline
    out["sleep_efficiency_vs_personal_baseline"] = sleep - baseline
    return out


# Put the feature groups together once so training and scoring use the same
# column definitions. BASE_V14_FEATURES contains the daily/hourly Fitbit side,
# and SURVEY_BASE_FEATURES adds prior survey personalization.
BASE_V14_FEATURES = list(dict.fromkeys(
    DAILY_HISTORY_FEATURES + HOURLY_FEATURES + HOURLY_HISTORY_FEATURES
))
SURVEY_BASE_FEATURES = list(dict.fromkeys(BASE_V14_FEATURES + REAL_SURVEY_HISTORY_FEATURES))
ALL_ADDED_METRIC_FEATURES = [
    feature
    for metric in ADDED_METRICS
    for feature in ADDED_METRIC_FEATURE_BLOCKS[metric]
]
ENGINEERED_FEATURES = list(dict.fromkeys(SURVEY_BASE_FEATURES + ALL_ADDED_METRIC_FEATURES + SLEEP_PERSONALIZATION_FEATURES))


# ============================================================
# SECTION 8: BUILD THE FULL TRAINING TABLE
# ============================================================
# This function runs every feature-engineering step in the correct order and
# returns one row per participant per day for the training script.


def prepare_training_dataframe(daily: pd.DataFrame, hourly: pd.DataFrame) -> pd.DataFrame:
    required = {"id", "date"}
    missing = required.difference(daily.columns)
    if missing:
        raise ValueError(f"daily_clean.csv is missing required columns: {sorted(missing)}")

    out = daily.copy()
    out["date"] = pd.to_datetime(out["date"], errors="coerce")
    out = out[out["date"].notna()].sort_values(["id", "date"]).reset_index(drop=True)
    out = normalize_daily_features(out)
    out = add_training_targets(out)

    hourly_daily = aggregate_hourly_dataframe(hourly)
    out = out.merge(hourly_daily, on=["id", "date"], how="left")

    out = add_daily_historical_features(out)
    out = add_hourly_historical_features(out)
    out = add_real_survey_history_features(out)
    out = add_candidate_metric_history_features(out)
    out = add_sleep_personalization_features(out)
    return out.reset_index(drop=True)


# ============================================================
# SECTION 9: TURN ONE API REQUEST INTO MODEL FEATURES
# ============================================================
# Deployment receives a current day plus optional earlier history. This section
# rebuilds the same daily, hourly, survey-history, and sleep-history features
# used during training, then returns only the current day's row for prediction.


def _request_hourly_summary(records) -> dict:
    if not records:
        return {name: np.nan for name in HOURLY_FEATURES}
    frame = pd.DataFrame(records)
    summary = summarize_hourly_day(frame)
    return {name: summary.get(name, np.nan) for name in HOURLY_FEATURES}


def _history_survey_score(item: dict) -> float:
    direct = item.get("survey_stress_score")
    try:
        direct_value = float(direct)
        if 1.0 <= direct_value <= 5.0:
            return direct_value
    except (TypeError, ValueError):
        pass

    survey_record = item.get("survey") or item
    return calculate_survey_stress(survey_record)


def request_to_feature_row(input_data: dict) -> pd.DataFrame:
    current = input_data.get("current") or {}
    history = input_data.get("history") or []

    current_date = pd.to_datetime(current.get("date"), errors="coerce")
    if pd.isna(current_date):
        raise ValueError("current.date is required and must use YYYY-MM-DD format")

    rows = []
    all_daily_inputs = RAW_FEATURES + ADDED_METRICS

    for item in history:
        date = pd.to_datetime(item.get("date"), errors="coerce")
        if pd.isna(date) or date >= current_date:
            continue

        daily_values = item.get("daily") or {}
        row = {"id": "request_user", "date": date}
        for feature in all_daily_inputs:
            row[feature] = daily_values.get(feature, np.nan)
        row["survey_stress_target"] = _history_survey_score(item)
        row.update(_request_hourly_summary(item.get("hourly") or []))
        rows.append(row)

    current_values = current.get("daily") or {}
    current_row = {"id": "request_user", "date": current_date, "survey_stress_target": np.nan}
    for feature in all_daily_inputs:
        current_row[feature] = current_values.get(feature, np.nan)
    current_row.update(_request_hourly_summary(current.get("hourly") or []))
    rows.append(current_row)

    frame = pd.DataFrame(rows)
    frame = normalize_daily_features(frame)
    frame = add_daily_historical_features(frame)
    frame = add_hourly_historical_features(frame)
    frame = add_real_survey_history_features(frame)
    frame = add_candidate_metric_history_features(frame)
    frame = add_sleep_personalization_features(frame)

    current_feature_row = frame[frame["date"].eq(current_date)].tail(1).copy()
    if current_feature_row.empty:
        raise ValueError("Could not build the current-day feature row")

    for feature in ENGINEERED_FEATURES:
        if feature not in current_feature_row.columns:
            current_feature_row[feature] = np.nan

    return current_feature_row
