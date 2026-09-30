using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantERP.Data;
using RestaurantERP.Models;
using RestaurantERP.Services;

namespace RestaurantERP.Controllers
{
    [Authorize(Roles = "Admin,Manager")]
    public class RefundController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly BranchService _branchService;

        public RefundController(ApplicationDbContext context,
                                UserManager<ApplicationUser> userManager,
                                BranchService branchService)
        {
            _context = context;
            _userManager = userManager;
            _branchService = branchService;
        }

        // ── Cashier: Refund POS Page ──────────────────────────────
        public IActionResult Index()
        {
            return View();
        }

        // ── Cashier: Lookup order by number ──────────────────────
        [HttpGet]
        public async Task<IActionResult> LookupOrder(string orderNumber)
        {
            if (string.IsNullOrWhiteSpace(orderNumber))
                return Json(new { success = false, message = "Please enter an order number" });

            var order = await _context.Orders
                .Include(o => o.Items).ThenInclude(i => i.Product)
                .Include(o => o.Cashier)
                .Include(o => o.Table)
                .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber.Trim().ToUpper());

            if (order == null)
                return Json(new { success = false, message = $"Order '{orderNumber}' not found" });

            if (order.Status == OrderStatus.Cancelled)
                return Json(new { success = false, message = "Cannot refund a cancelled order" });

            if (order.Status == OrderStatus.Refunded)
                return Json(new { success = false, message = "This order has already been fully refunded" });

            if (order.Status != OrderStatus.Completed && order.Status != OrderStatus.PartialRefund)
                return Json(new { success = false, message = $"Order status is '{order.Status}' — only completed orders can be refunded" });

            // Find existing refunds for this order to show already-refunded quantities
            var existingRefunds = await _context.RefundItems
                .Include(ri => ri.Refund)
                .Where(ri => ri.Refund!.OriginalOrderId == order.Id
                             && ri.Refund.Status == RefundStatus.Completed)
                .ToListAsync();

            var refundedQtyMap = existingRefunds
                .GroupBy(ri => ri.OrderItemId)
                .ToDictionary(g => g.Key, g => g.Sum(ri => ri.Quantity));

            return Json(new
            {
                success = true,
                order = new
                {
                    order.Id,
                    order.OrderNumber,
                    order.CreatedAt,
                    status = order.Status.ToString(),
                    orderType = order.OrderType.ToString(),
                    paymentMethod = order.PaymentMethod.ToString(),
                    cashierName = order.Cashier?.UserName,
                    tableNumber = order.Table?.TableNumber,
                    order.SubTotal,
                    order.TaxRate,
                    order.TaxAmount,
                    order.DiscountAmount,
                    order.Total,
                    items = order.Items.Select(i => new
                    {
                        i.Id,
                        productName = !string.IsNullOrEmpty(i.ProductName) ? i.ProductName : i.Product?.Name ?? "Unknown",
                        productNameAr = !string.IsNullOrEmpty(i.ProductNameAr) ? i.ProductNameAr : i.Product?.NameAr ?? "",
                        i.Quantity,
                        i.UnitPrice,
                        i.TotalPrice,
                        alreadyRefunded = refundedQtyMap.GetValueOrDefault(i.Id, 0),
                        availableToRefund = i.Quantity - refundedQtyMap.GetValueOrDefault(i.Id, 0)
                    }).Where(i => i.availableToRefund > 0)
                }
            });
        }

        // ── Cashier: Process Refund ───────────────────────────────
        [HttpPost]
        public async Task<IActionResult> ProcessRefund([FromBody] ProcessRefundRequest req)
        {
            if (req.Items == null || !req.Items.Any())
                return Json(new { success = false, message = "No items selected for refund" });

            var order = await _context.Orders
                .Include(o => o.Items).ThenInclude(i => i.Product)
                .FirstOrDefaultAsync(o => o.Id == req.OrderId);

            if (order == null)
                return Json(new { success = false, message = "Order not found" });

            if (order.Status == OrderStatus.Cancelled || order.Status == OrderStatus.Refunded)
                return Json(new { success = false, message = "Order cannot be refunded" });

            var userId = _userManager.GetUserId(User);

            // Validate quantities against what's available
            foreach (var reqItem in req.Items)
            {
                var orderItem = order.Items.FirstOrDefault(i => i.Id == reqItem.OrderItemId);
                if (orderItem == null)
                    return Json(new { success = false, message = $"Order item {reqItem.OrderItemId} not found" });

                var alreadyRefunded = await _context.RefundItems
                    .Include(ri => ri.Refund)
                    .Where(ri => ri.OrderItemId == reqItem.OrderItemId
                                 && ri.Refund!.Status == RefundStatus.Completed)
                    .SumAsync(ri => ri.Quantity);

                var available = orderItem.Quantity - alreadyRefunded;
                if (reqItem.Quantity > available)
                    return Json(new { success = false, message = $"Cannot refund {reqItem.Quantity} of '{orderItem.ProductName}' — only {available} available" });
            }

            // Build refund items
            var refundItems = new List<RefundItem>();
            decimal refundSubtotal = 0;

            foreach (var reqItem in req.Items)
            {
                var orderItem = order.Items.First(i => i.Id == reqItem.OrderItemId);
                var lineTotal = orderItem.UnitPrice * reqItem.Quantity;
                refundSubtotal += lineTotal;

                refundItems.Add(new RefundItem
                {
                    OrderItemId = reqItem.OrderItemId,
                    ProductName = !string.IsNullOrEmpty(orderItem.ProductName)
                                       ? orderItem.ProductName
                                       : orderItem.Product?.Name ?? "Item",
                    ProductNameAr = !string.IsNullOrEmpty(orderItem.ProductNameAr)
                                       ? orderItem.ProductNameAr
                                       : orderItem.Product?.NameAr ?? "",
                    Quantity = reqItem.Quantity,
                    UnitPrice = orderItem.UnitPrice,
                    TotalPrice = lineTotal
                });

                // Restore stock if product tracks stock
                var product = await _context.Products.FindAsync(orderItem.ProductId);
                if (product != null && product.TrackStock)
                {
                    product.StockQuantity += reqItem.Quantity;
                    _context.Update(product);
                }
            }

            var taxRate = order.TaxRate / 100;
            var refundTax = refundSubtotal * taxRate;
            var refundTotal = refundSubtotal + refundTax;

            // Determine if this is a full or partial refund
            var totalOrderQty = order.Items.Sum(i => i.Quantity);
            var refundQty = req.Items.Sum(i => i.Quantity);

            // Check all previous refunds too
            var previousRefundQty = await _context.RefundItems
                .Include(ri => ri.Refund)
                .Where(ri => ri.Refund!.OriginalOrderId == order.Id
                             && ri.Refund.Status == RefundStatus.Completed)
                .SumAsync(ri => ri.Quantity);

            var totalRefundedAfter = previousRefundQty + refundQty;
            var refundType = totalRefundedAfter >= totalOrderQty ? RefundType.Full : RefundType.Partial;

            // Create refund record
            var branchId = await _branchService.GetCurrentBranchIdAsync();
            var refund = new Refund
            {
                RefundNumber = await GenerateRefundNumber(),
                OriginalOrderId = order.Id,
                ProcessedById = userId,
                BranchId = branchId,
                RefundType = refundType,
                RefundMethod = req.RefundMethod,
                RefundAmount = refundSubtotal,
                RefundTax = refundTax,
                RefundTotal = refundTotal,
                Reason = req.Reason,
                Notes = req.Notes,
                Status = RefundStatus.Completed,
                Items = refundItems
            };

            _context.Refunds.Add(refund);

            // Update order status
            order.Status = refundType == RefundType.Full
                ? OrderStatus.Refunded
                : OrderStatus.PartialRefund;

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                refundNumber = refund.RefundNumber,
                refundTotal = refund.RefundTotal,
                refundType = refund.RefundType.ToString(),
                message = $"Refund {refund.RefundNumber} processed successfully"
            });
        }

        // ── Admin: Refunds List ───────────────────────────────────
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> AdminRefunds(DateTime? date, string? search, bool allBranches = false)
        {
            date ??= DateTime.Today;
            var branchId = await _branchService.GetCurrentBranchIdAsync();
            var isAdmin = User.IsInRole("Admin");

            var query = _context.Refunds
                .Include(r => r.OriginalOrder)
                .Include(r => r.ProcessedBy)
                .Include(r => r.Branch)
                .Include(r => r.Items).ThenInclude(ri => ri.OrderItem).ThenInclude(oi => oi!.Product)
                .Where(r => r.CreatedAt.Date == date.Value.Date);

            // Filter by branch unless Admin explicitly asks for all
            if (!isAdmin || !allBranches)
                query = query.Where(r => r.BranchId == branchId);

            if (!string.IsNullOrEmpty(search))
                query = query.Where(r => r.RefundNumber.Contains(search)
                                      || r.OriginalOrder!.OrderNumber.Contains(search));

            var refunds = await query.OrderByDescending(r => r.CreatedAt).ToListAsync();

            ViewBag.Date = date.Value.ToString("yyyy-MM-dd");
            ViewBag.TotalRefunded = refunds.Where(r => r.Status == RefundStatus.Completed).Sum(r => r.RefundTotal);
            ViewBag.RefundCount = refunds.Count(r => r.Status == RefundStatus.Completed);
            ViewBag.Search = search;
            ViewBag.AllBranches = allBranches;
            ViewBag.IsAdmin = isAdmin;

            return View(refunds);
        }

        // ── Admin: Refund Detail ──────────────────────────────────
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> RefundDetail(int id)
        {
            var refund = await _context.Refunds
                .Include(r => r.OriginalOrder).ThenInclude(o => o!.Items).ThenInclude(i => i.Product)
                .Include(r => r.OriginalOrder).ThenInclude(o => o!.Table)
                .Include(r => r.ProcessedBy)
                .Include(r => r.Items).ThenInclude(ri => ri.OrderItem).ThenInclude(oi => oi!.Product)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (refund == null)
                return NotFound();

            return View(refund);
        }

        // ── API: Today's refund summary (for dashboard) ───────────
        [HttpGet]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> GetTodayRefunds()
        {
            var today = DateTime.Today;
            var refunds = await _context.Refunds
                .Include(r => r.OriginalOrder)
                .Include(r => r.ProcessedBy)
                .Include(r => r.Items)
                .Where(r => r.CreatedAt.Date == today && r.Status == RefundStatus.Completed)
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();

            return Json(new
            {
                totalAmount = refunds.Sum(r => r.RefundTotal),
                count = refunds.Count,
                refunds = refunds.Select(r => new
                {
                    r.Id,
                    r.RefundNumber,
                    originalOrderNumber = r.OriginalOrder?.OrderNumber,
                    refundType = r.RefundType.ToString(),
                    refundMethod = r.RefundMethod.ToString(),
                    r.RefundAmount,
                    r.RefundTax,
                    r.RefundTotal,
                    r.Reason,
                    r.CreatedAt,
                    processedBy = r.ProcessedBy?.UserName,
                    itemCount = r.Items.Count,
                    items = r.Items.Select(i => new
                    {
                        i.ProductName,
                        i.ProductNameAr,
                        i.Quantity,
                        i.UnitPrice,
                        i.TotalPrice
                    })
                })
            });
        }

        // ── Helper: Generate refund number ────────────────────────
        private async Task<string> GenerateRefundNumber()
        {
            var today = DateTime.Today;
            var count = await _context.Refunds.CountAsync(r => r.CreatedAt.Date == today);
            return $"REF-{today:yyyyMMdd}-{(count + 1):D3}";
        }
    }

    // ── Request Models ────────────────────────────────────────────
    public class ProcessRefundRequest
    {
        public int OrderId { get; set; }
        public List<RefundItemRequest> Items { get; set; } = new();
        public RefundMethod RefundMethod { get; set; } = RefundMethod.Cash;
        public string? Reason { get; set; }
        public string? Notes { get; set; }
    }

    public class RefundItemRequest
    {
        public int OrderItemId { get; set; }
        public int Quantity { get; set; }
    }
}