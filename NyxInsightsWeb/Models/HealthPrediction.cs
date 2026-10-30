using System.ComponentModel.DataAnnotations;

namespace NyxInsightsWeb.Models
{
    public class HealthPrediction
    {
        [Key]
        public int Id { get; set; }

        public string UserId { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public double Bpm { get; set; }

        public double MindfulnessSession { get; set; }

        public double ExertionPointsPercentage { get; set; }

        public double VeryActiveMinutes { get; set; }

        public double ModeratelyActiveMinutes { get; set; }

        public double LightlyActiveMinutes { get; set; }

        public double SedentaryMinutes { get; set; }

        public double MinutesInDefaultZone1 { get; set; }

        public double MinutesInDefaultZone2 { get; set; }

        public double MinutesInDefaultZone3 { get; set; }

        public double Steps { get; set; }

        public double Calories { get; set; }

        public double StressScore { get; set; }

        public double SleepEfficiencyNextNight { get; set; }

        public string? AiExplanation { get; set; }
    }
}
