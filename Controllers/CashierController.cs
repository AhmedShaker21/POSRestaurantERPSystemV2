using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantERP.Data;
using RestaurantERP.Helpers;
using RestaurantERP.Models;
using RestaurantERP.Services;

namespace RestaurantERP.Controllers
{
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public class CashierController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly OrderService _orderService;
        private readonly BranchService _branchService;

        public CashierController(ApplicationDbContext context,
                                  UserManager<ApplicationUser> userManager,
                                  OrderService orderService,
                                  BranchService branchService)
        {
            _context = context;
            _userManager = userManager;
            _orderService = orderService;
            _branchService = branchService;
        }

        public async Task<IActionResult> Index(int? branchId)
        {
            // Admin can switch branches via query param; others use their assigned branch
            var isAdmin = User.IsInRole("Admin");
            var defaultBranchId = await _branchService.GetCurrentBranchIdAsync();
            var activeBranchId = (isAdmin && branchId.HasValue) ? branchId.Value : defaultBranchId;

            var branch = await _context.Branches.FindAsync(activeBranchId);

            var categories = await _context.Categories
                .Where(c => c.IsActive)
                .Include(c => c.Products.Where(p => p.IsActive && p.IsAvailable))
                .ToListAsync();

            var tables = await _context.DiningTables
                .Where(t => t.BranchId == activeBranchId)
                .ToListAsync();

            var settings = await _context.SystemSettings
                .Where(s => s.BranchId == activeBranchId || s.BranchId == null)
                .ToListAsync();

            var settingsDict = settings
                .OrderBy(s => s.BranchId == null ? 0 : 1)
                .GroupBy(s => s.Key)
                .ToDictionary(g => g.Key, g => g.Last().Value);

            // Pass all branches to view so Admin can switch
            if (isAdmin)
                ViewBag.AllBranches = await _context.Branches.Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync();

            ViewBag.Settings = settingsDict;
            ViewBag.Tables = tables;
            ViewBag.Branch = branch;
            ViewBag.BranchId = activeBranchId;
            ViewBag.IsAdmin = isAdmin;

            // Store active branch in session so GetProducts can use it without URL param
            HttpContext.Session.SetInt32("ActiveBranchId", activeBranchId);

            return View(categories);
        }

        [HttpGet]
        public async Task<IActionResult> GetProducts(int? categoryId)
        {
            // Get the active branch — URL param > Session > user default
            var defaultId = await _branchService.GetCurrentBranchIdAsync();
            var branchIdStr = Request.Query["branchId"].FirstOrDefault();
            int activeBranchId;
            if (int.TryParse(branchIdStr, out var qb) && qb > 0)
                activeBranchId = qb;                                         // from URL (admin branch switch)
            else if (HttpContext.Session.GetInt32("ActiveBranchId") is int sb && sb > 0)
                activeBranchId = sb;                                         // from session (set by Index action)
            else
                activeBranchId = defaultId;                                  // fallback to user's branch

            var query = _context.Products
                .Include(p => p.Category)
                .Include(p => p.ProductBranches)
                .Where(p => p.IsActive && p.IsAvailable
                         && p.ProductBranches.Any(pb => pb.BranchId == activeBranchId));

            if (categoryId.HasValue)
                query = query.Where(p => p.CategoryId == categoryId);

            // Load raw products first (EF can't compute EffectivePrice in SQL)
            var rawProducts = await query.Select(p => new
            {
                p.Id,
                p.Name,
                p.NameAr,
                p.Price,
                p.SellByBox,
                p.UnitsPerBox,
                p.BoxSellPrice,
                p.TaxRateOverride,
                p.ImageUrl,
                p.TrackStock,
                p.Barcode,
                p.MinStockAlert,
                p.Unit,
                // Stock the CASHIER can sell is what this branch holds, not what's
                // sitting in the central warehouse.
                BranchStock = p.ProductBranches
                    .Where(pb => pb.BranchId == activeBranchId)
                    .Select(pb => (decimal?)pb.StockQuantity).FirstOrDefault() ?? 0,
                IsSellableHere = p.ProductBranches
                    .Where(pb => pb.BranchId == activeBranchId)
                    .Select(pb => (bool?)pb.IsSellable).FirstOrDefault() ?? true,
                AvailableHere = p.ProductBranches
                    .Where(pb => pb.BranchId == activeBranchId)
                    .Select(pb => (bool?)pb.IsAvailable).FirstOrDefault() ?? true,
                BranchPrice = p.ProductBranches
                    .Where(pb => pb.BranchId == activeBranchId)
                    .Select(pb => pb.OverridePrice).FirstOrDefault(),
                BranchTax = p.ProductBranches
                    .Where(pb => pb.BranchId == activeBranchId)
                    .Select(pb => pb.TaxRateOverride).FirstOrDefault(),
                BranchSkipKitchen = p.ProductBranches
                    .Where(pb => pb.BranchId == activeBranchId)
                    .Select(pb => pb.SkipKitchenOverride).FirstOrDefault(),
                BranchMinAlert = p.ProductBranches
                    .Where(pb => pb.BranchId == activeBranchId)
                    .Select(pb => pb.MinStockAlertOverride).FirstOrDefault(),
                BranchUnitLabel = p.ProductBranches
                    .Where(pb => pb.BranchId == activeBranchId)
                    .Select(pb => pb.StockUnitLabel).FirstOrDefault(),
                CategoryName = p.Category!.Name,
                CategoryNameAr = p.Category.NameAr,
                CategoryColor = p.Category.ColorHex,
                CategoryIcon = p.Category.Icon,
                CategorySkipKitchen = p.Category.SkipKitchen,
                p.SkipKitchenOverride
            }).ToListAsync();

            // Default system tax rate
            var sysTaxStr = await _context.SystemSettings
                .Where(s => s.Key == "TaxRate" && (s.BranchId == activeBranchId || s.BranchId == null))
                .OrderByDescending(s => s.BranchId.HasValue)
                .Select(s => s.Value)
                .FirstOrDefaultAsync() ?? "14";
            var sysTax = decimal.Parse(sysTaxStr);

            // Compute tax-inclusive price per product
            var products = rawProducts.Select(p =>
            {
                // Price & tax are set per branch when the item is distributed there;
                // fall back to the product's own values for anything not distributed yet.
                var basePrice = p.BranchPrice
                    ?? (p.SellByBox && p.UnitsPerBox > 0 ? p.BoxSellPrice / p.UnitsPerBox : p.Price);
                var taxRate = p.BranchTax ?? p.TaxRateOverride ?? sysTax;
                var priceWithTax = Math.Round(basePrice * (1 + taxRate / 100), MidpointRounding.AwayFromZero); // round to nearest integer
                return new
                {
                    p.Id,
                    p.Name,
                    p.NameAr,
                    price = priceWithTax,   // ← tax-inclusive
                    basePrice,                        // original before tax
                    taxRate,
                    p.ImageUrl,
                    stockQuantity = p.BranchStock,
                    p.TrackStock,
                    p.Barcode,
                    p.CategoryName,
                    p.CategoryNameAr,
                    p.CategoryColor,
                    p.CategoryIcon,
                    p.IsSellableHere,
                    p.AvailableHere,
                    minStockAlert = p.BranchMinAlert ?? p.MinStockAlert,
                    stockUnit = p.BranchUnitLabel ?? p.Unit,
                    SkipKitchen = p.BranchSkipKitchen ?? p.SkipKitchenOverride ?? p.CategorySkipKitchen
                };
            })
            // Raw ingredients distributed to this branch (flour, rice, meat by the kilo)
            // are consumed inside dishes — they must never show as a sellable tile.
            .Where(p => p.IsSellableHere && p.AvailableHere)
            .Select(p => new
            {
                p.Id, p.Name, p.NameAr, p.price, p.basePrice, p.taxRate, p.ImageUrl,
                p.stockQuantity, p.TrackStock, p.minStockAlert, p.stockUnit, p.Barcode,
                p.CategoryName, p.CategoryNameAr, p.CategoryColor, p.CategoryIcon, p.SkipKitchen
            })
            .ToList();

            return Json(products);
        }

        [HttpPost]
        public async Task<IActionResult> PlaceOrder([FromBody] PlaceOrderRequest req)
        {
            var userId = _userManager.GetUserId(User);
            var branchId = await _branchService.GetCurrentBranchIdAsync();

            // ── SHIFT CHECK: reject if no open shift ──────────────
            var openShift = await _context.Shifts
                .FirstOrDefaultAsync(s => s.UserId == userId && s.BranchId == branchId && !s.IsClosed);

            if (openShift == null)
                return Json(new { success = false, message = "لا توجد وردية مفتوحة — يرجى فتح وردية أولاً قبل إنشاء الطلبات" });

            var settings = await _context.SystemSettings
                .Where(s => s.BranchId == branchId || s.BranchId == null)
                .ToListAsync();
            var taxRate = decimal.Parse(
                settings.Where(s => s.Key == "TaxRate")
                        .OrderByDescending(s => s.BranchId.HasValue)
                        .FirstOrDefault()?.Value ?? "14");

            var items = new List<OrderItem>();
            foreach (var item in req.Items)
            {
                var product = await _context.Products
                    .Include(p => p.Category)
                    .FirstOrDefaultAsync(p => p.Id == item.ProductId);
                if (product == null) continue;

                // Compute tax-inclusive unit price (same logic as GetProducts)
                var basePrice = product.SellByBox && product.UnitsPerBox > 0
                                  ? product.BoxSellPrice / product.UnitsPerBox
                                  : product.Price;
                var productTax = product.TaxRateOverride ?? taxRate;
                var unitPriceWithTax = Math.Round(basePrice * (1 + productTax / 100), MidpointRounding.AwayFromZero); // round to nearest integer

                items.Add(new OrderItem
                {
                    ProductId = item.ProductId,
                    ProductName = product.Name,
                    ProductNameAr = product.NameAr,
                    Quantity = item.Quantity,
                    UnitPrice = unitPriceWithTax,
                    TotalPrice = unitPriceWithTax * item.Quantity,
                    Notes = item.Notes,
                    SkipKitchen = product.SkipKitchenOverride ?? product.Category?.SkipKitchen ?? false
                });
                if (product.TrackStock)
                {
                    // Sell out of THIS branch's allocation, not the central warehouse.
                    var pb = await _context.ProductBranches
                        .FirstOrDefaultAsync(x => x.ProductId == product.Id && x.BranchId == branchId);
                    if (pb != null)
                    {
                        var before = pb.StockQuantity;
                        pb.StockQuantity -= item.Quantity;

                        _context.InventoryLogs.Add(new InventoryLog
                        {
                            ProductId = product.Id,
                            BranchId = branchId,
                            QuantityChange = -item.Quantity,
                            QuantityBefore = before,
                            QuantityAfter = pb.StockQuantity,
                            Reason = "بيع",
                            Type = InventoryLogType.Sale,
                            UnitCostAtEvent = product.CostPrice,
                            MovementValue = product.CostPrice * item.Quantity,
                            CreatedAt = DateTime.Now,
                            CreatedById = userId
                        });
                    }
                }
            }

            // Dine-in tabs can stay open: the customer keeps ordering and settles at the end.
            // Only table orders may defer; takeaway/delivery always collect immediately.
            var deferPayment = req.TableId.HasValue && !req.PayNow;

            var order = new Order
            {
                CashierId = userId,
                TableId = req.TableId,
                BranchId = branchId,
                OrderType = req.Type,
                CustomerName = req.CustomerName,
                CustomerPhone = req.CustomerPhone,
                Notes = req.Notes,
                TaxRate = 0,           // tax is already baked into each item price
                DiscountAmount = req.DiscountAmount,
                AmountPaid = deferPayment ? 0 : req.AmountPaid,
                PaymentMethod = req.PaymentMethod,
                IsPaid = !deferPayment,
                PaidAt = deferPayment ? null : DateTime.Now,
                Status = OrderStatus.Pending
            };

            var created = await _orderService.CreateOrderAsync(order, items);

            // ── Increment shift order count ───────────────────────
            openShift.TotalOrders++;
            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                orderId = created.Id,
                orderNumber = created.OrderNumber,
                total = created.Total,
                tableId = created.TableId,
                isPaid = created.IsPaid,
                orderType = created.OrderType.ToString()
            });
        }

        // ── TABLE STATUS (for live badges on the table selector) ──
        [HttpGet]
        public async Task<IActionResult> GetTablesStatus()
        {
            var branchId = await _branchService.GetCurrentBranchIdAsync();

            var tables = await _context.DiningTables
                .Where(t => t.BranchId == branchId)
                .OrderBy(t => t.TableNumber)
                .ToListAsync();

            var openOrders = await _context.Orders
                .Where(o => o.BranchId == branchId
                         && o.TableId != null
                         && o.CreatedAt.Date == DateTime.Today
                         && o.Status != OrderStatus.Cancelled
                         && o.Status != OrderStatus.Refunded)
                .Select(o => new { o.TableId, o.Total, o.IsPaid })
                .ToListAsync();

            var result = tables.Select(t => new
            {
                t.Id,
                t.TableNumber,
                status = t.Status.ToString(),
                openOrdersCount = openOrders.Count(o => o.TableId == t.Id),
                openTotal = openOrders.Where(o => o.TableId == t.Id).Sum(o => o.Total),
                outstanding = openOrders.Where(o => o.TableId == t.Id && !o.IsPaid).Sum(o => o.Total),
                unpaidCount = openOrders.Count(o => o.TableId == t.Id && !o.IsPaid)
            });

            return Json(result);
        }

        // ── COMBINED BILL FOR A TABLE (all of today's live orders) ──
        [HttpGet]
        public async Task<IActionResult> GetTableBill(int tableId)
        {
            var table = await _context.DiningTables.FindAsync(tableId);
            if (table == null)
                return Json(new { success = false, message = "الطاولة غير موجودة" });

            var orders = await _context.Orders
                .Include(o => o.Items).ThenInclude(i => i.Product)
                .Where(o => o.TableId == tableId
                         && o.CreatedAt.Date == DateTime.Today
                         && o.Status != OrderStatus.Cancelled
                         && o.Status != OrderStatus.Refunded)
                .OrderBy(o => o.CreatedAt)
                .ToListAsync();

            if (!orders.Any())
                return Json(new { success = false, message = "لا يوجد طلبات مفتوحة على هذه الطاولة" });

            var allItems = orders.SelectMany(o => o.Items).ToList();
            var unpaid = orders.Where(o => !o.IsPaid).ToList();

            return Json(new
            {
                success = true,
                tableId = table.Id,
                tableNumber = table.TableNumber,
                orderCount = orders.Count,
                orders = orders.Select(o => new
                {
                    o.Id,
                    o.OrderNumber,
                    time = o.CreatedAt.ToString("HH:mm"),
                    o.Total,
                    o.IsPaid
                }),
                subTotal = orders.Sum(o => o.SubTotal),
                discountAmount = orders.Sum(o => o.DiscountAmount),
                total = orders.Sum(o => o.Total),
                paidTotal = orders.Where(o => o.IsPaid).Sum(o => o.Total),
                outstanding = unpaid.Sum(o => o.Total),
                unpaidCount = unpaid.Count,
                items = allItems
                    .GroupBy(i => new { i.ProductId, i.UnitPrice, i.Notes })
                    .Select(g => new
                    {
                        productName = g.First().ProductName,
                        productNameAr = g.First().ProductNameAr,
                        quantity = g.Sum(i => i.Quantity),
                        unitPrice = g.Key.UnitPrice,
                        totalPrice = g.Sum(i => i.TotalPrice),
                        notes = g.Key.Notes
                    })
            });
        }

        // ── SETTLE + CLOSE TABLE: collect every unpaid order, then free the table ──
        [HttpPost]
        public async Task<IActionResult> CloseTable([FromBody] CloseTableRequest req)
        {
            var table = await _context.DiningTables.FindAsync(req.TableId);
            if (table == null)
                return Json(new { success = false, message = "الطاولة غير موجودة" });

            var unpaid = await _context.Orders
                .Where(o => o.TableId == req.TableId
                         && !o.IsPaid
                         && o.CreatedAt.Date == DateTime.Today
                         && o.Status != OrderStatus.Cancelled
                         && o.Status != OrderStatus.Refunded)
                .ToListAsync();

            var outstanding = unpaid.Sum(o => o.Total);

            foreach (var o in unpaid)
            {
                o.IsPaid = true;
                o.PaidAt = DateTime.Now;
                o.PaymentMethod = req.PaymentMethod;
                o.AmountPaid = o.Total;
            }

            table.Status = TableStatus.Available;
            await _context.SaveChangesAsync();

            return Json(new { success = true, settledCount = unpaid.Count, settledTotal = outstanding });
        }

        [HttpGet]
        public async Task<IActionResult> GetOrderForPrint(int? id, int? orderId)
        {
            var resolvedId = id ?? orderId ?? 0;
            var order = await _context.Orders
                .Include(o => o.Items).ThenInclude(i => i.Product)
                .Include(o => o.Table)
                .Include(o => o.Cashier)
                .Include(o => o.Branch)
                .FirstOrDefaultAsync(o => o.Id == resolvedId);

            if (order == null)
                return Json(new { success = false, message = "Order not found" });

            order.IsPrinted = true;
            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                id = order.Id,
                orderNumber = order.OrderNumber,
                createdAt = order.CreatedAt,
                type = (int)order.OrderType,
                orderType = order.OrderType.ToString(),
                status = order.Status.ToString(),
                paymentMethod = (int)order.PaymentMethod,
                subTotal = order.SubTotal,
                taxRate = order.TaxRate,
                taxAmount = order.TaxAmount,
                discountAmount = order.DiscountAmount,
                total = order.Total,
                amountPaid = order.AmountPaid,
                change = order.Change,
                customerName = order.CustomerName,
                notes = order.Notes,
                table = order.Table?.TableNumber,
                cashier = (order.Cashier as ApplicationUser)?.FullName
                                 ?? (order.Cashier as ApplicationUser)?.FullNameAr
                                 ?? order.Cashier?.UserName ?? order.Cashier?.Email ?? "—",
                cashierAr = (order.Cashier as ApplicationUser)?.FullNameAr
                                 ?? (order.Cashier as ApplicationUser)?.FullName
                                 ?? order.Cashier?.UserName ?? "—",
                branchName = order.Branch?.Name,
                branchNameAr = order.Branch?.NameAr,
                items = order.Items.Select(i => new
                {
                    productId = i.ProductId,
                    productName = !string.IsNullOrEmpty(i.ProductName) ? i.ProductName : i.Product?.Name ?? "Item",
                    productNameAr = !string.IsNullOrEmpty(i.ProductNameAr) ? i.ProductNameAr : i.Product?.NameAr ?? "",
                    quantity = i.Quantity,
                    unitPrice = i.UnitPrice,
                    totalPrice = i.TotalPrice,
                    notes = i.Notes
                })
            });
        }

        public async Task<IActionResult> History(DateTime? date)
        {
            date ??= DateTime.Today;
            var userId = _userManager.GetUserId(User);
            var branchId = await _branchService.GetCurrentBranchIdAsync();
            var isAdmin = User.IsInRole("Admin") || User.IsInRole("Manager");

            var query = _context.Orders
                .Include(o => o.Items)
                .Include(o => o.Table)
                .Where(o => o.CreatedAt.Date == date.Value.Date && o.BranchId == branchId);

            if (!isAdmin)
                query = query.Where(o => o.CashierId == userId);

            var orders = await query.OrderByDescending(o => o.CreatedAt).ToListAsync();
            ViewBag.Date = date.Value.ToString("yyyy-MM-dd");

            var completedStatuses = new[] { OrderStatus.Completed, OrderStatus.Refunded, OrderStatus.PartialRefund };
            var grossSales = orders.Where(o => completedStatuses.Contains(o.Status)).Sum(o => o.Total);
            var refundedToday = await _context.Refunds
                .Where(r => r.BranchId == branchId && r.CreatedAt.Date == date.Value.Date
                         && r.Status == RefundStatus.Completed
                         && (!isAdmin ? r.ProcessedById == userId : true))
                .SumAsync(r => r.RefundTotal);

            ViewBag.TotalSales = Math.Max(0, grossSales - refundedToday);
            return View(orders);
        }

        [HttpPost]
        public async Task<IActionResult> OpenShift([FromBody] OpenShiftRequest req)
        {
            var userId = _userManager.GetUserId(User);
            var branchId = await _branchService.GetCurrentBranchIdAsync();
            var existing = await _context.Shifts
                .FirstOrDefaultAsync(s => s.UserId == userId && s.BranchId == branchId && !s.IsClosed);
            if (existing != null) return Json(new { success = false, message = "Shift already open" });

            _context.Shifts.Add(new Shift
            {
                UserId = userId!,
                BranchId = branchId,
                OpeningCash = req.OpeningCash,
                StartTime = DateTime.Now
            });
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> CloseShift([FromBody] CloseShiftRequest req)
        {
            var userId = _userManager.GetUserId(User);
            var branchId = await _branchService.GetCurrentBranchIdAsync();
            var shift = await _context.Shifts
                .FirstOrDefaultAsync(s => s.UserId == userId && s.BranchId == branchId && !s.IsClosed);
            if (shift == null) return Json(new { success = false });

            var statuses = new[] { OrderStatus.Completed, OrderStatus.Refunded, OrderStatus.PartialRefund };
            var gross = await _context.Orders
                .Where(o => o.CashierId == userId && o.BranchId == branchId
                         && o.CreatedAt >= shift.StartTime && statuses.Contains(o.Status)
                         && o.PaymentMethod == PaymentMethod.Cash)
                .SumAsync(o => o.Total);
            var refunds = await _context.Refunds
                .Where(r => r.ProcessedById == userId && r.BranchId == branchId
                         && r.CreatedAt >= shift.StartTime && r.Status == RefundStatus.Completed
                         && r.RefundMethod == RefundMethod.Cash)
                .SumAsync(r => r.RefundTotal);

            shift.EndTime = DateTime.Now;
            shift.ClosingCash = req.ClosingCash;
            shift.TotalSales = Math.Max(0, gross - refunds);
            shift.IsClosed = true;
            shift.Notes = req.Notes;
            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                totalSales = shift.TotalSales,
                expectedCash = shift.OpeningCash + shift.TotalSales,
                difference = req.ClosingCash - (shift.OpeningCash + shift.TotalSales)
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetCurrentShift()
        {
            var userId = _userManager.GetUserId(User);
            var branchId = await _branchService.GetCurrentBranchIdAsync();
            var shift = await _context.Shifts
                .FirstOrDefaultAsync(s => s.UserId == userId && s.BranchId == branchId && !s.IsClosed);
            return Json(shift != null
                ? new { isOpen = true, shift.Id, shift.StartTime, shift.OpeningCash }
                : new { isOpen = false });
        }

        [HttpPost]
        public async Task<IActionResult> CancelOrder(int id, string reason)
        {
            var order = await _context.Orders.FindAsync(id);
            if (order == null) return Json(new { success = false });
            if (order.Status == OrderStatus.Completed)
                return Json(new { success = false, message = "Cannot cancel completed order" });
            order.Status = OrderStatus.Cancelled;
            order.Notes = $"CANCELLED: {reason}";
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
        [HttpPost]
        public IActionResult OpenDrawer()
        {
            CashDrawer.OpenDrawer();
            return Ok();
        }
    }

    public class PlaceOrderRequest
    {
        public List<OrderItemRequest> Items { get; set; } = new();
        public int? TableId { get; set; }
        public OrderType Type { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerPhone { get; set; }
        public string? Notes { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal AmountPaid { get; set; }
        public PaymentMethod PaymentMethod { get; set; }
        // false = keep the tab open (dine-in only); true = collect immediately
        public bool PayNow { get; set; } = true;
    }
    public class OrderItemRequest { public int ProductId { get; set; } public int Quantity { get; set; } public string? Notes { get; set; } }
    public class CloseTableRequest { public int TableId { get; set; } public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash; }
    public class OpenShiftRequest { public decimal OpeningCash { get; set; } }
    public class CloseShiftRequest { public decimal ClosingCash { get; set; } public string? Notes { get; set; } }
}