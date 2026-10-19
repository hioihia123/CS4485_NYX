using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NyxInsightsWeb.Models;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Authorization;

namespace NyxInsightsWeb.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;

        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

	[Authorize]
        public IActionResult Index()
        {

            return View();
        }

	[AllowAnonymous]
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
