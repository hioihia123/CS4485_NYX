# Nyx Insights Model V16

V16 keeps the reduced V15 deployment input contract and restores the V12-style personalized sleep architecture.

## Raw Fitbit inputs

V16 requires the original 12 Version-1 measurements plus one added measurement selected by V15: `sleep_efficiency`.

The 12 original inputs are `bpm`, `mindfulness_session`, `exertion_points_percentage`, `very_active_minutes`, `moderately_active_minutes`, `lightly_active_minutes`, `sedentary_minutes`, `minutes_in_default_zone_1`, `minutes_in_default_zone_2`, `minutes_in_default_zone_3`, `steps`, and `calories`.

Hourly records are still allowed only for the Version-1 measurements that exist hourly: BPM, mindfulness, steps, calories, and zones 1/2/3.

## Survey use

The current real survey is the stress training target and is never a predictor. Earlier real surveys are allowed as historical personalization features.

## What changed from V15

Stress stays on the V15 reduced feature family. Sleep now adds exact previous 1/2/3-night sleep-efficiency values, EWM3/EWM7 history, a causal personal sleep baseline, and V12-style `SelectKBest(k=60)` feature selection.

The final sleep model predicts the change from the personal baseline rather than predicting the full next-night efficiency directly:

`next-night sleep = personal baseline + predicted delta`

All history features use earlier dates only.

## Training

From PowerShell in this folder:

```powershell
.\setup_python314.bat
.\run_training.bat
```

The trained bundle is written to `model_bundle_v16` and contains:

- `stress_model.pkl`
- `sleep_model.pkl`
- `metadata.json`

## Local scoring test

After training:

```powershell
.\run_local_test.bat
```

## Important experiment in metadata.json

V16 reports three sleep results separately:

1. personal sleep baseline only
2. direct next-night prediction with the same features and SelectKBest(60)
3. personal baseline + predicted delta (final V16 sleep architecture)

This makes it possible to measure whether the architecture itself recovers V12-level sleep performance without restoring V12's large sensor list.
