using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NyxInsightsWeb.Models
{
    public class DailyMetrics
    {
        [JsonPropertyName("bpm")] public double? Bpm { get; set; }
        [JsonPropertyName("mindfulness_session")] public bool? MindfulnessSession { get; set; }
        [JsonPropertyName("exertion_points_percentage")] public double? ExertionPointsPercentage { get; set; }
        [JsonPropertyName("very_active_minutes")] public double? VeryActiveMinutes { get; set; }
        [JsonPropertyName("moderately_active_minutes")] public double? ModeratelyActiveMinutes { get; set; }
        [JsonPropertyName("lightly_active_minutes")] public double? LightlyActiveMinutes { get; set; }
        [JsonPropertyName("sedentary_minutes")] public double? SedentaryMinutes { get; set; }
        [JsonPropertyName("minutes_in_default_zone_1")] public double? Zone1 { get; set; }
        [JsonPropertyName("minutes_in_default_zone_2")] public double? Zone2 { get; set; }
        [JsonPropertyName("minutes_in_default_zone_3")] public double? Zone3 { get; set; }
        [JsonPropertyName("steps")] public long? Steps { get; set; }
        [JsonPropertyName("calories")] public double? Calories { get; set; }
        [JsonPropertyName("sleep_efficiency")] public double? SleepEfficiency { get; set; }

        [JsonIgnore] public double? SleepMinutes { get; set; }

        [JsonIgnore]
        public bool HasAnyData =>
            Bpm.HasValue || MindfulnessSession.HasValue || ExertionPointsPercentage.HasValue ||
            VeryActiveMinutes.HasValue || ModeratelyActiveMinutes.HasValue || LightlyActiveMinutes.HasValue ||
            SedentaryMinutes.HasValue || Zone1.HasValue || Zone2.HasValue || Zone3.HasValue ||
            Steps.HasValue || Calories.HasValue || SleepEfficiency.HasValue;
    }

    public class HourlyMetrics
    {
        [JsonPropertyName("hour")] public int Hour { get; set; }
        [JsonPropertyName("bpm")] public double? Bpm { get; set; }
        [JsonPropertyName("mindfulness_session")] public bool? MindfulnessSession { get; set; }
        [JsonPropertyName("steps")] public long? Steps { get; set; }
        [JsonPropertyName("calories")] public double? Calories { get; set; }
        [JsonPropertyName("minutes_in_default_zone_1")] public double? Zone1 { get; set; }
        [JsonPropertyName("minutes_in_default_zone_2")] public double? Zone2 { get; set; }
        [JsonPropertyName("minutes_in_default_zone_3")] public double? Zone3 { get; set; }

        [JsonIgnore]
        public bool HasAnyData =>
            Bpm.HasValue || MindfulnessSession.HasValue || Steps.HasValue || Calories.HasValue ||
            Zone1.HasValue || Zone2.HasValue || Zone3.HasValue;
    }

    public class DayRecord
    {
        [JsonPropertyName("date")] public string Date { get; set; } = "";
        [JsonPropertyName("daily")] public DailyMetrics Daily { get; set; } = new();
        [JsonPropertyName("hourly")] public List<HourlyMetrics>? Hourly { get; set; }

        [JsonPropertyName("survey_stress_score")] public double? SurveyStressScore { get; set; }
    }

    public class DailyHistoryResult
    {
        public List<DayRecord> Days { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
    }


    public class ModelInput
    {
        [JsonPropertyName("current")] public DayRecord Current { get; set; } = new();
        [JsonPropertyName("history")] public List<DayRecord> History { get; set; } = new();
    }

    public class ModelRequest
    {
        [JsonPropertyName("input_data")] public ModelInput InputData { get; set; } = new();
    }

    public class ModelRequestResult
    {
        public ModelRequest Request { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
    }
}