using System.ComponentModel.DataAnnotations;

namespace NyxInsightsWeb.Models
{
    public class User
    {
        public int user_id { get; set; }
        [Required]
        public string username { get; set; }
        [Required, DataType(DataType.Password)]
        public string password { get; set; }
        public bool RememberMe { get; set; }
    }
}
