"""
Nyx Insights - Model Version 16

This is the main training file for V16. The model has two jobs:
1. predict the user's survey-based stress score from Fitbit and survey history
2. predict next-night sleep efficiency

The stress model keeps the reduced V15 input set. The sleep model uses the
same reduced inputs, but it predicts how much the next night's sleep should
change from that person's normal sleep baseline. This was the main idea that
allowed V16 to recover the stronger sleep performance from V12 without
bringing back all of V12's extra sensor inputs.
"""

from __future__ import annotations

import json
import warnings
from pathlib import Path

import joblib
import numpy as np
import pandas as pd

from sklearn.base import clone
from sklearn.ensemble import (
    ExtraTreesRegressor,
    GradientBoostingRegressor,
    HistGradientBoostingRegressor,
    RandomForestRegressor,
)
from sklearn.feature_selection import SelectKBest, VarianceThreshold, f_regression
from sklearn.impute import SimpleImputer
from sklearn.metrics import mean_absolute_error, r2_score
from sklearn.model_selection import GroupKFold
from sklearn.pipeline import Pipeline

from nyx_v16_features import (
    ADDED_METRIC_FEATURE_BLOCKS,
    ADDED_METRIC_UNITS,
    HOURLY_RAW_FEATURES,
    RAW_FEATURES,
    REAL_SURVEY_HISTORY_FEATURES,
    SLEEP_PERSONALIZATION_FEATURES,
    SURVEY_BASE_FEATURES,
    prepare_training_dataframe,
)

warnings.filterwarnings("ignore", category=UserWarning)


# ============================================================
# SECTION 1: FILE PATHS, INPUT UNITS, AND REFERENCE RESULTS
# ============================================================
# This section defines where the data and trained models are stored. It also
# keeps the V12 and V15 results here so the final metadata can show whether
# V16 actually improved the model instead of making us compare results by hand.

BASE_DIR = Path(__file__).resolve().parent
DAILY_PATH = BASE_DIR / "daily_clean.csv"
HOURLY_PATH = BASE_DIR / "hourly_clean.csv"
OUTPUT_DIR = BASE_DIR / "model_bundle_v16"

V15_REFERENCE = {
    "stress": {"r2": 0.10120396852687641, "mae": 0.8499814412776232},
    "sleep": {"r2": 0.25818108032958087, "mae": 2.764388626838572},
}

V12_REFERENCE = {
    "stress": {"r2": 0.13556450295382316, "mae": 0.8251026709128446},
    "sleep": {"r2": 0.740880988997149, "mae": 2.298112342822178},
}

CORE_INPUT_UNITS = {
    "bpm": "beats per minute",
    "mindfulness_session": "boolean or 0/1",
    "exertion_points_percentage": "ratio from 0 to 1",
    "very_active_minutes": "minutes",
    "moderately_active_minutes": "minutes",
    "lightly_active_minutes": "minutes",
    "sedentary_minutes": "minutes",
    "minutes_in_default_zone_1": "minutes",
    "minutes_in_default_zone_2": "minutes",
    "minutes_in_default_zone_3": "minutes",
    "steps": "count",
    "calories": "kcal",
}

# V15 tested several extra Fitbit measurements and sleep_efficiency was the
# only one that clearly earned a place in the reduced deployment input set.
# V16 keeps that decision instead of testing those measurements again.
DEPLOYMENT_ADDED_METRICS = ["sleep_efficiency"]
DEPLOYMENT_FEATURES = list(dict.fromkeys(
    list(SURVEY_BASE_FEATURES)
    + ADDED_METRIC_FEATURE_BLOCKS["sleep_efficiency"]
))

# Stress stays on the same V15 feature set so V16 changes only the sleep side.
# The extra sleep-history features are added only to SLEEP_FEATURES. This lets
# us tell whether the sleep architecture, rather than a new stress input, caused
# the performance change.
STRESS_FEATURES = list(DEPLOYMENT_FEATURES)
SLEEP_FEATURES = list(dict.fromkeys(
    DEPLOYMENT_FEATURES + SLEEP_PERSONALIZATION_FEATURES
))


# ============================================================
# SECTION 2: BUILD THE PREPROCESSING PIPELINE
# ============================================================
# Before a model trains, missing values are filled with the median. For the
# sleep model we also remove columns that never change and keep the 60 features
# that have the strongest statistical relationship with the sleep target.
# Keeping these steps inside the pipeline is important because they are learned
# separately inside each training fold instead of using information from test data.


def make_pipeline(estimator, select_k=None):
    steps = [
        (
            "imputer",
            SimpleImputer(
                strategy="median",
                add_indicator=True,
                keep_empty_features=True,
            ),
        ),
    ]

    if select_k is not None:
        steps.append(("remove_constant_features", VarianceThreshold(threshold=0.0)))
        steps.append(("feature_selection", SelectKBest(f_regression, k=select_k)))

    steps.append(("model", estimator))
    return Pipeline(steps=steps)


# ============================================================
# SECTION 3: DEFINE THE ALGORITHMS WE WANT TO COMPARE
# ============================================================
# I test four tree-based regression algorithms because the Fitbit features are
# nonlinear and many of them interact with each other. Every algorithm receives
# the same feature set and the same participant-grouped folds, so the comparison
# is based on the model itself instead of a different train/test split.


def stress_candidates():
    return {
        # Extra Trees builds many randomized trees. It is useful when the data
        # has many related features because the extra randomness can reduce overfitting.
        "Stress_ExtraTrees": make_pipeline(
            ExtraTreesRegressor(
                n_estimators=250,
                max_depth=7,
                min_samples_leaf=6,
                max_features=0.70,
                random_state=42,
                n_jobs=-1,
            )
        ),
        # Random Forest averages many decision trees to make the final prediction.
        "Stress_RandomForest": make_pipeline(
            RandomForestRegressor(
                n_estimators=250,
                max_depth=8,
                min_samples_leaf=7,
                max_features=0.70,
                random_state=42,
                n_jobs=-1,
            )
        ),
        # Gradient Boosting builds trees in sequence, with each tree trying to
        # correct errors left by the previous trees.
        "Stress_GradientBoost": make_pipeline(
            GradientBoostingRegressor(
                n_estimators=180,
                learning_rate=0.035,
                max_depth=2,
                min_samples_leaf=8,
                loss="huber",
                random_state=42,
            )
        ),
        # HistGradientBoosting is another boosting method that groups values into
        # bins first, which can make nonlinear training more efficient.
        "Stress_HistGradientBoost": make_pipeline(
            HistGradientBoostingRegressor(
                max_iter=220,
                learning_rate=0.04,
                max_leaf_nodes=18,
                min_samples_leaf=18,
                l2_regularization=2.0,
                random_state=42,
            )
        ),
    }


def sleep_candidates(prefix):
    """Create the four sleep regressors after selecting the best 60 features."""
    return {
        f"{prefix}_ExtraTrees": make_pipeline(
            ExtraTreesRegressor(
                n_estimators=500,
                max_depth=5,
                min_samples_leaf=6,
                max_features=0.65,
                random_state=42,
                n_jobs=-1,
            ),
            select_k=60,
        ),
        f"{prefix}_RandomForest": make_pipeline(
            RandomForestRegressor(
                n_estimators=500,
                max_depth=7,
                min_samples_leaf=8,
                max_features=0.65,
                random_state=42,
                n_jobs=-1,
            ),
            select_k=60,
        ),
        f"{prefix}_GradientBoost": make_pipeline(
            GradientBoostingRegressor(
                n_estimators=300,
                learning_rate=0.03,
                max_depth=2,
                min_samples_leaf=8,
                loss="huber",
                random_state=42,
            ),
            select_k=60,
        ),
        f"{prefix}_HistGradientBoost": make_pipeline(
            HistGradientBoostingRegressor(
                max_iter=320,
                learning_rate=0.035,
                max_leaf_nodes=18,
                min_samples_leaf=18,
                l2_regularization=2.0,
                random_state=42,
            ),
            select_k=60,
        ),
    }


# ============================================================
# SECTION 4: EVALUATION HELPERS
# ============================================================
# These small functions keep the evaluation code consistent. MAE tells us the
# average size of the prediction error, while R² tells us how much better the
# model explains variation compared with simply predicting the overall mean.


def calculate_metrics(actual, predicted):
    table = pd.DataFrame({"actual": actual, "predicted": predicted}).dropna()
    if table.empty:
        return {"n": 0, "mae": None, "baseline_mae": None, "r2": None}

    y = table["actual"].astype(float)
    p = table["predicted"].astype(float)
    baseline = np.full(len(y), y.mean())
    return {
        "n": int(len(table)),
        "mae": float(mean_absolute_error(y, p)),
        "baseline_mae": float(mean_absolute_error(y, baseline)),
        "r2": float(r2_score(y, p)),
    }


def winner_name(results):
    return max(results, key=lambda name: (results[name]["r2"], -results[name]["mae"]))


def sleep_baseline(frame, fallback):
    """Choose the person's sleep baseline, with a fallback for missing history."""
    baseline = pd.to_numeric(frame["sleep_personal_baseline"], errors="coerce")
    current = pd.to_numeric(frame["sleep_efficiency"], errors="coerce")
    return baseline.fillna(current).fillna(float(fallback))


# ============================================================
# SECTION 5: CROSS-VALIDATE THE STRESS MODELS
# ============================================================
# GroupKFold keeps each participant entirely in either training or testing for
# a fold. This is stricter than randomly splitting rows because the model cannot
# see another day from the same person while that person is being evaluated.


def tune_stress(df):
    labeled = df[df["survey_stress_target"].notna()].copy()
    folds = GroupKFold(n_splits=min(5, labeled["id"].nunique()))
    results = {}
    cache = {}

    print("\nV16 STRESS - V15 REDUCED INPUT CONTRACT")
    print("========================================")

    for name, template in stress_candidates().items():
        pred = pd.Series(np.nan, index=labeled.index, dtype=float)
        for tr_pos, te_pos in folds.split(labeled, groups=labeled["id"]):
            tr = labeled.iloc[tr_pos].index
            te = labeled.iloc[te_pos].index
            model = clone(template)
            model.fit(df.loc[tr, STRESS_FEATURES], df.loc[tr, "survey_stress_target"])
            pred.loc[te] = np.clip(model.predict(df.loc[te, STRESS_FEATURES]), 1.0, 5.0)

        metrics = calculate_metrics(df.loc[labeled.index, "survey_stress_target"], pred)
        results[name] = metrics
        cache[name] = pred
        print(f"  {name}: R²={metrics['r2']:.4f}, MAE={metrics['mae']:.4f}")

    winner = winner_name(results)
    return winner, results, cache[winner]


# ============================================================
# SECTION 6: COMPARE TWO WAYS TO PREDICT SLEEP
# ============================================================
# This is the main V16 experiment. The direct model tries to predict the full
# next-night sleep-efficiency value. The baseline-delta model starts with the
# person's normal sleep level and predicts only how much the next night should
# move above or below that baseline.
#
# Both methods use the same features, participant folds, algorithms, and
# SelectKBest(60). That makes the comparison fair because the main difference
# between them is the prediction target, not the input data.


def tune_sleep_direct(df):
    labeled = df[df["sleep_target"].notna()].copy()
    folds = GroupKFold(n_splits=min(5, labeled["id"].nunique()))
    results = {}
    cache = {}

    print("\nV16 SLEEP A - DIRECT TARGET + SELECTKBest(60)")
    print("================================================")

    for name, template in sleep_candidates("SleepDirect").items():
        pred = pd.Series(np.nan, index=labeled.index, dtype=float)
        for tr_pos, te_pos in folds.split(labeled, groups=labeled["id"]):
            tr = labeled.iloc[tr_pos].index
            te = labeled.iloc[te_pos].index
            model = clone(template)
            model.fit(df.loc[tr, SLEEP_FEATURES], df.loc[tr, "sleep_target"])
            pred.loc[te] = np.clip(model.predict(df.loc[te, SLEEP_FEATURES]), 0.0, 100.0)

        metrics = calculate_metrics(df.loc[labeled.index, "sleep_target"], pred)
        results[name] = metrics
        cache[name] = pred
        print(f"  {name}: R²={metrics['r2']:.4f}, MAE={metrics['mae']:.4f}")

    winner = winner_name(results)
    return winner, results, cache[winner]


def tune_sleep_delta(df):
    labeled = df[df["sleep_target"].notna()].copy()
    folds = GroupKFold(n_splits=min(5, labeled["id"].nunique()))
    results = {}
    cache = {}

    baseline_only = pd.Series(np.nan, index=labeled.index, dtype=float)

    print("\nV16 SLEEP B - PERSONAL BASELINE + PREDICTED DELTA")
    print("==================================================")

    for name, template in sleep_candidates("SleepDelta").items():
        pred = pd.Series(np.nan, index=labeled.index, dtype=float)

        for tr_pos, te_pos in folds.split(labeled, groups=labeled["id"]):
            tr = labeled.iloc[tr_pos].index
            te = labeled.iloc[te_pos].index

            fallback = float(df.loc[tr, "sleep_target"].mean())
            train_base = sleep_baseline(df.loc[tr], fallback)
            test_base = sleep_baseline(df.loc[te], fallback)
            baseline_only.loc[te] = np.clip(test_base.to_numpy(dtype=float), 0.0, 100.0)

            y_delta = df.loc[tr, "sleep_target"].astype(float) - train_base
            model = clone(template)
            model.fit(df.loc[tr, SLEEP_FEATURES], y_delta)
            delta = model.predict(df.loc[te, SLEEP_FEATURES])
            pred.loc[te] = np.clip(test_base.to_numpy(dtype=float) + delta, 0.0, 100.0)

        metrics = calculate_metrics(df.loc[labeled.index, "sleep_target"], pred)
        results[name] = metrics
        cache[name] = pred
        print(f"  {name}: R²={metrics['r2']:.4f}, MAE={metrics['mae']:.4f}")

    baseline_metrics = calculate_metrics(
        df.loc[labeled.index, "sleep_target"], baseline_only
    )
    print(
        f"  PERSONAL BASELINE ONLY: R²={baseline_metrics['r2']:.4f}, "
        f"MAE={baseline_metrics['mae']:.4f}"
    )

    winner = winner_name(results)
    return winner, results, cache[winner], baseline_metrics


# ============================================================
# SECTION 7: TRAIN THE FINAL WINNING MODELS
# ============================================================
# After cross-validation picks the best stress and sleep algorithms, these
# functions retrain the winners on all available labeled rows. These are the
# models that get saved for the API/deployment code.


def fit_final_stress(df, winner):
    labeled = df[df["survey_stress_target"].notna()]
    model = clone(stress_candidates()[winner])
    model.fit(labeled[STRESS_FEATURES], labeled["survey_stress_target"])
    return model


def fit_final_sleep_delta(df, winner):
    labeled = df[df["sleep_target"].notna()]
    fallback = float(labeled["sleep_target"].mean())
    base = sleep_baseline(labeled, fallback)
    delta_target = labeled["sleep_target"].astype(float) - base

    model = clone(sleep_candidates("SleepDelta")[winner])
    model.fit(labeled[SLEEP_FEATURES], delta_target)
    return model, fallback


# ============================================================
# SECTION 8: RUN THE FULL TRAINING PROCESS
# ============================================================
# main() connects all of the earlier sections: load the CSV files, build the
# engineered features, compare models, train the winners, and save the model
# files plus metadata that explains exactly how this version was trained.


def main():
    # Load the two cleaned datasets and turn them into one participant-day table
    # containing the daily, hourly, survey-history, and sleep-history features.
    print("Nyx Insights V16")
    print("=================")
    print("Inputs: V15 reduced contract (12 original + sleep_efficiency).")
    print("Sleep experiment: restore V12 personal-baseline delta architecture.")

    daily = pd.read_csv(DAILY_PATH)
    hourly = pd.read_csv(HOURLY_PATH)
    df = prepare_training_dataframe(daily, hourly)

    hourly_coverage = int(df["hourly_hours_observed"].fillna(0).gt(0).sum())

    print(f"\nDaily rows: {len(df)}")
    print(f"Participants: {df['id'].nunique()}")
    print(f"Rows with hourly data: {hourly_coverage}")
    print(f"Real survey labels: {df['survey_stress_target'].notna().sum()}")
    print(f"Next-night sleep labels: {df['sleep_target'].notna().sum()}")
    print(f"Stress features: {len(STRESS_FEATURES)}")
    print(f"Sleep features before SelectKBest: {len(SLEEP_FEATURES)}")

    # Cross-validation is used first so we choose the algorithms using held-out
    # participants instead of choosing a model based on its training score.
    stress_winner, stress_results, stress_pred = tune_stress(df)
    direct_winner, direct_results, direct_pred = tune_sleep_direct(df)
    delta_winner, delta_results, delta_pred, baseline_metrics = tune_sleep_delta(df)

    # Once the winners are known, train them again using every labeled row.
    final_stress = fit_final_stress(df, stress_winner)
    final_sleep, sleep_fallback = fit_final_sleep_delta(df, delta_winner)

    # Save the trained estimators. score.py loads these files when the model is
    # called locally or through the Azure endpoint.
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    joblib.dump(final_stress, OUTPUT_DIR / "stress_model.pkl")
    joblib.dump(final_sleep, OUTPUT_DIR / "sleep_model.pkl")

    direct_metrics = calculate_metrics(df["sleep_target"], direct_pred)
    delta_metrics = calculate_metrics(df["sleep_target"], delta_pred)

    # Save the experiment details and evaluation results in JSON. This makes the
    # model easier to audit because the input contract, winners, metrics, and
    # excluded predictors are stored next to the trained model files.
    metadata = {
        "model_version": "16.0",
        "algorithm": "ReducedInputsWithV12StyleSleepBaselineDelta",
        "goal": (
            "Keep V15's reduced deployment inputs and restore V12's causal sleep-history, "
            "personal-baseline delta target, and SelectKBest(60) architecture."
        ),
        "core_v1_raw_inputs": RAW_FEATURES,
        "core_v1_input_count": len(RAW_FEATURES),
        "core_input_units": CORE_INPUT_UNITS,
        "hourly_raw_inputs_used": HOURLY_RAW_FEATURES,
        "added_fitbit_metrics": DEPLOYMENT_ADDED_METRICS,
        "added_fitbit_metric_units": {k: ADDED_METRIC_UNITS[k] for k in DEPLOYMENT_ADDED_METRICS},
        "recommended_fitbit_raw_inputs": RAW_FEATURES + DEPLOYMENT_ADDED_METRICS,
        "recommended_fitbit_raw_input_count": len(RAW_FEATURES) + len(DEPLOYMENT_ADDED_METRICS),
        "previous_survey_history_is_separate_app_input": True,
        "survey_history_rule": (
            "Only real surveys from dates before the prediction date are predictors. "
            "The current-day survey remains the stress target."
        ),
        "survey_history_features": REAL_SURVEY_HISTORY_FEATURES,
        "sleep_personalization_features_added_in_v16": SLEEP_PERSONALIZATION_FEATURES,
        "sleep_baseline_rule": (
            "Use prior 30-day sleep-efficiency mean when available, then prior 14/7/3-day means, "
            "then previous-night sleep efficiency, then current sleep efficiency only as a cold-start fallback."
        ),
        "sleep_training_target": "next_night_sleep_efficiency_minus_personal_sleep_baseline",
        "sleep_prediction_rule": "personal_sleep_baseline_plus_predicted_delta, clipped to 0-100",
        "sleep_feature_selection": "VarianceThreshold then SelectKBest(f_regression, k=60) inside each training fold",
        "stress_feature_set": STRESS_FEATURES,
        "sleep_feature_set_before_selection": SLEEP_FEATURES,
        "feature_counts": {
            "stress": len(STRESS_FEATURES),
            "sleep_before_select_k": len(SLEEP_FEATURES),
            "sleep_selected_k": 60,
        },
        "winners": {
            "stress": stress_winner,
            "sleep_direct_comparison": direct_winner,
            "sleep_final_baseline_delta": delta_winner,
        },
        "metrics": {
            "stress": calculate_metrics(df["survey_stress_target"], stress_pred),
            "sleep": delta_metrics,
        },
        "sleep_architecture_comparison": {
            "personal_baseline_only": baseline_metrics,
            "direct_target_with_same_features_and_select_k": direct_metrics,
            "personal_baseline_delta_final": delta_metrics,
        },
        "candidate_results": {
            "stress": stress_results,
            "sleep_direct": direct_results,
            "sleep_baseline_delta": delta_results,
        },
        "v15_reference_metrics": V15_REFERENCE,
        "v12_reference_metrics": V12_REFERENCE,
        "sleep_fallback_mean": sleep_fallback,
        "validation": "participant-grouped 5-fold cross-validation",
        "daily_rows": int(len(df)),
        "rows_with_hourly_data": hourly_coverage,
        "real_survey_count": int(df["survey_stress_target"].notna().sum()),
        "sleep_target_count": int(df["sleep_target"].notna().sum()),
        "excluded_predictors": [
            "current-day survey answer",
            "future surveys",
            "future Fitbit days in rolling features",
            "Fitbit stress_score",
            "sleep_points_percentage",
            "responsiveness_points_percentage",
            "resting_hr",
            "rmssd",
            "minutesAsleep",
            "minutesAwake",
            "full_sleep_breathing_rate",
            "temperature",
            "SpO2",
            "SCL",
            "VO2 max",
            "distance",
            "sleep stage ratios",
            "hourly measurements outside the Version-1 hourly types",
        ],
        "interpretation_notes": (
            "V16 is designed to test whether V12's strong sleep performance came from its personalized "
            "baseline/delta architecture rather than from requiring many additional Fitbit measurements. "
            "All sleep-history features are causal and use earlier dates only."
        ),
    }

    with open(OUTPUT_DIR / "metadata.json", "w", encoding="utf-8") as f:
        json.dump(metadata, f, indent=2)

    print("\nFINAL V16 PARTICIPANT-GROUPED RESULTS")
    print("=====================================")
    print(
        f"Stress: R²={metadata['metrics']['stress']['r2']:.4f}, "
        f"MAE={metadata['metrics']['stress']['mae']:.4f}"
    )
    print(
        f"Sleep direct: R²={direct_metrics['r2']:.4f}, "
        f"MAE={direct_metrics['mae']:.4f}"
    )
    print(
        f"Sleep baseline only: R²={baseline_metrics['r2']:.4f}, "
        f"MAE={baseline_metrics['mae']:.4f}"
    )
    print(
        f"Sleep baseline + delta: R²={delta_metrics['r2']:.4f}, "
        f"MAE={delta_metrics['mae']:.4f}"
    )
    print(f"\nSaved model bundle to: {OUTPUT_DIR}")


if __name__ == "__main__":
    main()
