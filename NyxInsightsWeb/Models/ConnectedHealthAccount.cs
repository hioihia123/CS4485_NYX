using System.ComponentModel.DataAnnotations;

namespace NyxInsightsWeb.Models
{
    public class ConnectedHealthAccount
    {
        public int Id { get; set; }

        // The ASP.NET Identity user who owns this connection.
        [Required]
        public string UserId { get; set; } = string.Empty;

        // The user's Google Health identity returned by Google.
        [Required]
        public string GoogleHealthUserId { get; set; } = string.Empty;
        public string? EncryptedAccessToken { get; set; }
        public string? EncryptedRefreshToken { get; set; }
        public DateTime? AccessTokenExpiresAt { get; set; }
        public string? GrantedScopes { get; set; }
        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastSyncedAt { get; set; }
    }
}
