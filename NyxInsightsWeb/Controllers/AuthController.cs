
using Microsoft.AspNetCore.Mvc;

namespace NyxInsightsWeb.Controllers
{
    public class AuthController : Controller
    {
        [HttpGet]
        public IActionResult Login()
        {
            return Redirect("/Identity/Account/Login");
        }
    }
}

