using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantERP.Data;
using RestaurantERP.Models;
using RestaurantERP.Services;

namespace RestaurantERP.Controllers
{
    [Authorize(Roles = "Admin,Manager,Kitchen")]
    public class KitchenController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly BranchService _branchService;

        public KitchenController(ApplicationDbContext context, BranchService branchService)
        {
            _context = context;
            _branchService = branchService;
        }

        public async Task<IActionResult> Index()
        {
            var isAdmin = User.IsInRole("Admin");
            var defaultBranchId = await _branchService.GetCurrentBranchIdAsync();

            // Read branchId from query if admin
            int activeBranchId = defaultBranchId;
            if (isAdmin && Request.Query.ContainsKey("branchId") &&
                int.TryParse(Request.Query["branchId"], out var qBranchId))
                activeBranchId = qBranchId;

            var branchId = activeBranchId;
            var branch = await _context.Branches.FindAsync(branchId);
            ViewBag.Branch = branch;
            ViewBag.BranchId = branchId;
            ViewBag.IsAdmin = isAdmin;
            if (isAdmin)
                ViewBag.AllBranches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetPendingOrders(int? branchId)
        {
            var isAdmin = User.IsInRole("Admin");
            var defaultId = await _branchService.GetCurrentBranchIdAsync();
            var activeBranch = (isAdmin && branchId.HasValue) ? branchId.Value : defaultId;

            var orders = await _context.Orders
                .Include(o => o.Items).ThenInclude(i => i.Product).ThenInclude(p => p!.Category)
                .Include(o => o.Table)
                .Where(o => o.BranchId == activeBranch)                          // ← KEY FIX
                .Where(o => o.Status == OrderStatus.Pending
                         || o.Status == OrderStatus.Preparing
                         || o.Status == OrderStatus.Ready)
                .OrderBy(o => o.CreatedAt)
                .ToListAsync();

            var now = DateTime.Now;
            var result = orders.Select(o => new
            {
                o.Id,
                o.OrderNumber,
                o.CreatedAt,
                elapsedSeconds = (int)(now - o.CreatedAt).TotalSeconds,
                status = o.Status.ToString(),
                orderType = o.OrderType.ToString(),
                o.Notes,
                tableNumber = o.Table?.TableNumber,
                hasKitchenItems = o.Items.Any(i => !i.SkipKitchen),
                allSkipKitchen = o.Items.All(i => i.SkipKitchen),
                items = o.Items.Select(i => new
                {
                    i.Id,
                    i.Quantity,
                    i.Notes,
                    i.SkipKitchen,
                    productName = i.ProductName,
                    productNameAr = i.ProductNameAr,
                    categoryName = i.Product?.Category?.Name,
                    categoryIcon = i.Product?.Category?.Icon
                })
            });

            return Json(new { orders = result });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateStatus([FromBody] KitchenUpdateStatusRequest req)
        {
            var branchId = await _branchService.GetCurrentBranchIdAsync();
            var order = await _context.Orders.FindAsync(req.OrderId);

            if (order == null)
                return Json(new { success = false, message = "Order not found" });

            // Security: prevent kitchen from updating orders of other branches
            if (order.BranchId != branchId)
                return Json(new { success = false, message = "Access denied — order belongs to another branch" });

            if (!Enum.TryParse<OrderStatus>(req.Status, out var status))
                return Json(new { success = false, message = "Invalid status" });

            order.Status = status;
            if (status == OrderStatus.Completed) order.CompletedAt = DateTime.Now;
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
    }

    [Authorize(Roles = "Admin,Manager")]
    public class ManagerController : Controller
    {
        private readonly ApplicationDbContext _contextApp;
        private readonly AnalyticsService _analyticsServ;
        private readonly BranchService _branchServices;

        public ManagerController(ApplicationDbContext context, AnalyticsService analytics, BranchService branchService)
        {
            _contextApp = context;
            _analyticsServ = analytics;
            _branchServices = branchService;
        }

        public async Task<IActionResult> Index()
        {
            var branchId = await _branchServices.GetCurrentBranchIdAsync();
            var stats = await _analyticsServ.GetDashboardStatsAsync(branchId);
            return View(stats);
        }

        public async Task<IActionResult> Orders(DateTime? from, DateTime? to)
        {
            from ??= DateTime.Today;
            to ??= DateTime.Today;
            var branchId = await _branchServices.GetCurrentBranchIdAsync();

            var orders = await _contextApp.Orders
                .Include(o => o.Items)
                .Include(o => o.Table)
                .Include(o => o.Cashier)
                .Include(o => o.Branch)
                .Where(o => o.BranchId == branchId)
                .Where(o => o.CreatedAt.Date >= from.Value.Date && o.CreatedAt.Date <= to.Value.Date)
                .OrderByDescending(o => o.CreatedAt)
                .ToListAsync();

            ViewBag.From = from.Value.ToString("yyyy-MM-dd");
            ViewBag.To = to.Value.ToString("yyyy-MM-dd");
            return View(orders);
        }

        public IActionResult Reports() => View();
    }

    public class KitchenUpdateStatusRequest
    {
        public int OrderId { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
