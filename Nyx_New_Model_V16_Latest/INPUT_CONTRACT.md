# V16 Input Contract

## Current daily Fitbit measurements

Required measurement types:

- `bpm` — beats per minute
- `mindfulness_session` — boolean or 0/1
- `exertion_points_percentage` — ratio 0 to 1
- `very_active_minutes` — minutes
- `moderately_active_minutes` — minutes
- `lightly_active_minutes` — minutes
- `sedentary_minutes` — minutes
- `minutes_in_default_zone_1` — minutes
- `minutes_in_default_zone_2` — minutes
- `minutes_in_default_zone_3` — minutes
- `steps` — count
- `calories` — kcal
- `sleep_efficiency` — percent

This is 13 unique Fitbit measurement types total: the original 12 from Version 1 plus sleep efficiency, which was the only added metric selected by V15 for both targets.

## Hourly data

Hourly records may use only these original Version-1 measurement types:

- `bpm`
- `mindfulness_session`
- `steps`
- `calories`
- `minutes_in_default_zone_1`
- `minutes_in_default_zone_2`
- `minutes_in_default_zone_3`

No hourly temperature, SCL, distance, or other new measurement type is used.

## History

Each history day can include the same daily/hourly measurements. Historical `sleep_efficiency` is used to calculate exact prior-night lags, rolling means/variability, EWM history, and the personal sleep baseline.

A history day may also include a real prior stress survey as `survey_stress_score` (1–5) or the survey mood fields. Only surveys dated before the prediction date are used.

For the 7-day personal anomaly baseline, send **at least the last 7 days** of history. The personal comparison needs at least 3 days with data inside the window (surveys for stress, `sleep_efficiency` for sleep). With fewer days, `personal_7day.available` is `false`; the population comparison still runs.

## Excluded

V16 does not require resting HR, RMSSD/HRV, minutes asleep, minutes awake, breathing rate, temperature, SpO2, SCL, VO2 max, distance, or sleep-stage ratios.
