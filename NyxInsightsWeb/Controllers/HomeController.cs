using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NyxInsightsWeb.Models;
using Microsoft.AspNetCore.Diagnostics;

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

        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Login", "Auth");
        }

	public IActionResult Error()
	{
	    var error = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
	    _logger.LogError(
        	error?.Error,
       		"Unhandled exception at {Path}",
        	error?.Path
    	    );

    return StatusCode(500,
        "An internal server error occurred. Check Azure logs.");
	}
    }
}
