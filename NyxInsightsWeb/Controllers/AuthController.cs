using Microsoft.AspNetCore.Mvc;
using System.Configuration;
using System.Data.SqlClient;
using NyxInsightsWeb.Models;

namespace NyxInsightsWeb.Controllers
{
    public class AuthController : Controller
    {
        private readonly ILogger<AuthController> _logger;

        public AuthController(ILogger<AuthController> logger)
        {
            _logger = logger;
        }

        public IActionResult Login()
        {
            return View();
        }

        [HttpPost]
        public IActionResult Login(User user)
        {
            if (user == null)
            {
                return View();
            }

            //User placeholder = new User();
            //placeholder.username = "place";
            //placeholder.password = "holder";

            if ((user.username == "place" || user.username == "second") && (user.password == "holder" || user.password == "second"))
            {
                HttpContext.Session.SetString("SessionName", user.username);
                return RedirectToAction("Index","Home");
            } else
            {
                ViewBag.Message = "Invalid Email or Password";
            }

            return View();
        }
    }
}
