# V16 Architecture

## Stress

Original 12 Fitbit measurements + hourly versions where available + 3/7/14/30-day history + prior real survey history + sleep efficiency history -> four participant-grouped algorithms -> final stress prediction.

## Sleep

The raw Fitbit contract is unchanged from the selected V15 contract: original 12 + sleep efficiency.

Sleep efficiency is converted into causal history features including exact 1/2/3-night lags, 3/7/14/30-day summaries, EWM3/EWM7, and a personal baseline.

Training target:

`delta = next-night sleep efficiency - personal sleep baseline`

Prediction:

`next-night sleep efficiency = personal sleep baseline + predicted delta`

Before the sleep estimator, V16 uses median imputation, constant-feature removal, and `SelectKBest(f_regression, k=60)` inside each training fold.

Validation remains participant-grouped 5-fold cross-validation.
