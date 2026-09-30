using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RestaurantERP.Models;

namespace RestaurantERP.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;

        public HomeController(UserManager<ApplicationUser> userManager)
        {
            _userManager = userManager;
        }

        public async Task<IActionResult> Index()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToAction("Login", "Account");

            var roles = await _userManager.GetRolesAsync(user);
            return roles.FirstOrDefault() switch
            {
                "Admin" => RedirectToAction("Index", "Admin"),
                "Manager" => RedirectToAction("Index", "Admin"),
                "Cashier" => RedirectToAction("Index", "Cashier"),
                "Kitchen" => RedirectToAction("Index", "Kitchen"),
                "محصل" => RedirectToAction("Products", "Admin"),
                _ => RedirectToAction("Login", "Account")
            };
        }
    }
}
