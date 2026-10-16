using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NyxInsightsWeb.Models;


namespace NyxInsightsWeb.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;

        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

        public IActionResult Index()
        {
            //if (HttpContext.Session.GetString("SessionName") == null)
            if (HttpContext.Session.GetString("SessionName") == null)
            {
                return RedirectToAction("Login", "Auth");
            }

            return View();
        }
    }
}
