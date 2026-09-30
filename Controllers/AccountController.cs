using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RestaurantERP.Models;
using RestaurantERP.ViewModels;

namespace RestaurantERP.Controllers
{
    public class AccountController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;

        public AccountController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager)
        {
            _userManager = userManager;
            _signInManager = signInManager;
        }

        [HttpGet]
        public async Task<IActionResult> Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                var currentUser = await _userManager.GetUserAsync(User);

                if (currentUser != null)
                    return await RedirectUserByRole(currentUser, returnUrl);
            }

            ViewData["ReturnUrl"] = returnUrl;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl;

            if (!ModelState.IsValid)
                return View(model);

            var user = await _userManager.FindByEmailAsync(model.Email);

            if (user == null || !user.IsActive)
            {
                ModelState.AddModelError("", "Invalid credentials or account disabled.");
                return View(model);
            }

            var result = await _signInManager.PasswordSignInAsync(
                user,
                model.Password,
                model.RememberMe,
                lockoutOnFailure: false
            );

            if (result.Succeeded)
                return await RedirectUserByRole(user, returnUrl);

            ModelState.AddModelError("", "Invalid login attempt.");
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Login", "Account");
        }

        public IActionResult AccessDenied()
        {
            return View();
        }

        private async Task<IActionResult> RedirectUserByRole(ApplicationUser user, string? returnUrl = null)
        {
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            if (await _userManager.IsInRoleAsync(user, "Admin"))
                return RedirectToAction("Index", "Admin");

            if (await _userManager.IsInRoleAsync(user, "Manager"))
                return RedirectToAction("Index", "Admin");       // Manager sees Admin dashboard

            if (await _userManager.IsInRoleAsync(user, "Cashier"))
                return RedirectToAction("Index", "Cashier");

            if (await _userManager.IsInRoleAsync(user, "Kitchen"))
                return RedirectToAction("Index", "Kitchen");


            if (await _userManager.IsInRoleAsync(user, "محصل"))
                return RedirectToAction("Products", "Admin");       // محصل sees products only

            return RedirectToAction("Login", "Account");            // unknown role → back to login
        }
    
    }
}