using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantERP.Data;
using RestaurantERP.Models;
using RestaurantERP.Services;

namespace RestaurantERP.Controllers
{
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public class WaiterController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly OrderService _orderService;
        private readonly BranchService _branchService;

        public WaiterController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, OrderService orderService, BranchService branchService)
        {
            _context = context;
            _userManager = userManager;
            _orderService = orderService;
            _branchService = branchService;
        }

        public async Task<IActionResult> Index()
        {
            var branchId = await _branchService.GetCurrentBranchIdAsync();

            var categories = await _context.Categories
                .Where(c => c.IsActive)
                .Include(c => c.Products.Where(p => p.IsActive && p.IsAvailable
                                                 && p.ProductBranches.Any(pb => pb.BranchId == branchId)))
                .ToListAsync();

            // Tables belong to a branch — showing every branch's tables produced
            // duplicated numbers (two "01", two "02", …) on the selector.
            var tables = await _context.DiningTables
                .Where(t => t.BranchId == branchId)
                .OrderBy(t => t.TableNumber)
                .ToListAsync();

            var settings = await _context.SystemSettings
                .Where(s => s.BranchId == branchId || s.BranchId == null)
                .OrderByDescending(s => s.BranchId.HasValue)
                .ToListAsync();

            ViewBag.Settings = settings
                .GroupBy(s => s.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);
            ViewBag.Tables = tables;

            return View(categories);
        }

        public async Task<IActionResult> Tables()
        {
            var branchId = await _branchService.GetCurrentBranchIdAsync();

            var tables = await _context.DiningTables
                .Where(t => t.BranchId == branchId)
                .OrderBy(t => t.TableNumber)
                .ToListAsync();

            var activeOrders = await _context.Orders
                .Include(o => o.Items)
                .Where(o => o.BranchId == branchId
                         && o.CreatedAt.Date == DateTime.Today
                         && !o.IsPaid
                         && o.Status != OrderStatus.Cancelled
                         && o.Status != OrderStatus.Refunded)
                .ToListAsync();

            var settings = await _context.SystemSettings
                .Where(s => s.BranchId == branchId || s.BranchId == null)
                .OrderByDescending(s => s.BranchId.HasValue)
                .ToListAsync();
            ViewBag.Settings = settings
                .GroupBy(s => s.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);

            ViewBag.ActiveOrders = activeOrders;
            return View(tables);
        }

        public async Task<IActionResult> MyOrders(DateTime? date)
        {
            date ??= DateTime.Today;
            var userId = _userManager.GetUserId(User);
            var isAdmin = User.IsInRole("Admin") || User.IsInRole("Manager");
            var branchId = await _branchService.GetCurrentBranchIdAsync();

            var query = _context.Orders
                .Include(o => o.Items)
                .Include(o => o.Table)
                .Where(o => o.CreatedAt.Date == date.Value.Date && o.BranchId == branchId);

            if (!isAdmin)
                query = query.Where(o => o.CashierId == userId);

            var orders = await query.OrderByDescending(o => o.CreatedAt).ToListAsync();
            ViewBag.Date = date.Value.ToString("yyyy-MM-dd");
            ViewBag.TotalSales = orders.Where(o => o.Status == OrderStatus.Completed).Sum(o => o.Total);

            return View(orders);
        }

        [HttpGet]
        public async Task<IActionResult> GetProducts(int? categoryId)
        {
            var branchId = await _branchService.GetCurrentBranchIdAsync();

            var query = _context.Products
                .Include(p => p.Category)
                .Where(p => p.IsActive && p.IsAvailable
                         && p.ProductBranches.Any(pb => pb.BranchId == branchId));

            if (categoryId.HasValue && categoryId.Value > 0)
                query = query.Where(p => p.CategoryId == categoryId.Value);

            var raw = await query
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    nameAr = p.NameAr,
                    p.Price,
                    p.SellByBox,
                    p.UnitsPerBox,
                    p.BoxSellPrice,
                    p.TaxRateOverride,

                    imageUrl = string.IsNullOrWhiteSpace(p.ImageUrl)
                        ? "/images/no-image.png"
                        : p.ImageUrl.StartsWith("/")
                            ? p.ImageUrl
                            : "/" + p.ImageUrl,

                    stockQuantity = p.StockQuantity,
                    trackStock = p.TrackStock,

                    categoryName = p.Category != null ? p.Category.Name : "",
                    categoryNameAr = p.Category != null ? p.Category.NameAr : "",
                    categoryColor = p.Category != null ? p.Category.ColorHex : "#1e3a5f",
                    categoryIcon = p.Category != null ? p.Category.Icon : "🍽️"
                })
                .ToListAsync();

            var sysTaxStr = await _context.SystemSettings
                .Where(s => s.Key == "TaxRate" && (s.BranchId == branchId || s.BranchId == null))
                .OrderByDescending(s => s.BranchId.HasValue)
                .Select(s => s.Value)
                .FirstOrDefaultAsync() ?? "14";
            decimal.TryParse(sysTaxStr, out var sysTax);

            // Show the tax-inclusive price — the same figure PlaceOrder charges, so the
            // menu price and the bill always agree.
            var products = raw.Select(p =>
            {
                var basePrice = p.SellByBox && p.UnitsPerBox > 0 ? p.BoxSellPrice / p.UnitsPerBox : p.Price;
                var taxRate = p.TaxRateOverride ?? sysTax;
                var priceWithTax = Math.Round(basePrice * (1 + taxRate / 100), MidpointRounding.AwayFromZero);
                return new
                {
                    p.id,
                    p.name,
                    p.nameAr,
                    price = priceWithTax,
                    p.imageUrl,
                    p.stockQuantity,
                    p.trackStock,
                    p.categoryName,
                    p.categoryNameAr,
                    p.categoryColor,
                    p.categoryIcon
                };
            }).ToList();

            return Json(products);
        }

        [HttpGet]
        public async Task<IActionResult> GetTables()
        {
            var branchId = await _branchService.GetCurrentBranchIdAsync();

            var tables = await _context.DiningTables
                .Where(t => t.BranchId == branchId)
                .OrderBy(t => t.TableNumber)
                .ToListAsync();

            var activeOrders = await _context.Orders
                .Include(o => o.Items)
                .Where(o => o.BranchId == branchId
                         && o.CreatedAt.Date == DateTime.Today
                         && !o.IsPaid
                         && o.Status != OrderStatus.Cancelled
                         && o.Status != OrderStatus.Refunded)
                .Select(o => new
                {
                    o.TableId,
                    o.Id,
                    o.OrderNumber,
                    o.Status,
                    ItemCount = o.Items.Count,
                    o.SubTotal
                })
                .ToListAsync();

            var result = tables.Select(t => new
            {
                t.Id,
                t.TableNumber,
                t.Capacity,
                t.Section,
                status = t.Status.ToString(),
                activeOrder = activeOrders.FirstOrDefault(o => o.TableId == t.Id)
            });

            return Json(result);
        }

        [HttpPost]
        public async Task<IActionResult> PlaceOrder([FromBody] WaiterOrderRequest req)
        {
            try
            {
                if (req == null)
                {
                    return Json(new { success = false, message = "Invalid request data." });
                }

                if (req.Items == null || !req.Items.Any())
                {
                    return Json(new { success = false, message = "Order has no items." });
                }

                int branchId;

                if (req.OrderType == OrderType.DineIn)
                {
                    if (req.TableId == null)
                    {
                        return Json(new { success = false, message = "Please select a table." });
                    }

                    var table = await _context.DiningTables
                        .FirstOrDefaultAsync(t => t.Id == req.TableId.Value);

                    if (table == null)
                    {
                        return Json(new { success = false, message = "Selected table was not found." });
                    }

                    branchId = table.BranchId;
                }
                else
                {
                    var branch = await _context.Branches.FirstOrDefaultAsync();

                    if (branch == null)
                    {
                        return Json(new { success = false, message = "No branch found in database." });
                    }

                    branchId = branch.Id;
                }

                var branchExists = await _context.Branches.AnyAsync(b => b.Id == branchId);

                if (!branchExists)
                {
                    return Json(new
                    {
                        success = false,
                        message = $"Invalid BranchId: {branchId}. This branch does not exist."
                    });
                }

                var subtotal = 0m;
                var orderItems = new List<OrderItem>();

                // System tax rate (percent), same source the cashier uses
                var sysTaxStr = await _context.SystemSettings
                    .Where(s => s.Key == "TaxRate" && (s.BranchId == branchId || s.BranchId == null))
                    .OrderByDescending(s => s.BranchId.HasValue)
                    .Select(s => s.Value)
                    .FirstOrDefaultAsync() ?? "14";
                decimal.TryParse(sysTaxStr, out var sysTax);

                foreach (var item in req.Items)
                {
                    var product = await _context.Products
                        .Include(p => p.Category)
                        .FirstOrDefaultAsync(p => p.Id == item.ProductId);

                    if (product == null)
                    {
                        return Json(new
                        {
                            success = false,
                            message = $"Product with ID {item.ProductId} was not found."
                        });
                    }

                    // Match the cashier exactly: bake tax into the unit price and round to
                    // the nearest pound, so the same product costs the same on both screens.
                    var basePrice = product.SellByBox && product.UnitsPerBox > 0
                                      ? product.BoxSellPrice / product.UnitsPerBox
                                      : product.Price;
                    var productTax = product.TaxRateOverride ?? sysTax;
                    var unitPriceWithTax = Math.Round(basePrice * (1 + productTax / 100), MidpointRounding.AwayFromZero);
                    var lineTotal = unitPriceWithTax * item.Quantity;

                    subtotal += lineTotal;

                    orderItems.Add(new OrderItem
                    {
                        ProductId = product.Id,
                        ProductName = product.Name,
                        ProductNameAr = product.NameAr,
                        Quantity = item.Quantity,
                        UnitPrice = unitPriceWithTax,
                        TotalPrice = lineTotal,          // ← was missing: made every order total 0.00
                        Notes = item.Notes,
                        SkipKitchen = product.SkipKitchenOverride ?? product.Category?.SkipKitchen ?? false
                    });
                }

                var discount = req.DiscountAmount;

                // A waiter taking a dine-in order isn't collecting money — the order goes
                // onto the table's open tab and the cashier settles the whole table later.
                var isTableOrder = req.OrderType == OrderType.DineIn && req.TableId.HasValue;

                var order = new Order
                {
                    OrderNumber = "ORD-" + DateTime.Now.ToString("yyyyMMddHHmmss"),
                    CreatedAt = DateTime.Now,

                    // Without this the order has no owner, so it was filtered out of
                    // سجل الطلبات (which shows the signed-in user's own orders).
                    CashierId = _userManager.GetUserId(User),

                    TableId = req.OrderType == OrderType.DineIn ? req.TableId : null,
                    OrderType = req.OrderType,
                    Status = OrderStatus.Pending,

                    CustomerName = req.CustomerName,
                    CustomerPhone = req.CustomerPhone,
                    Notes = req.Notes,

                    BranchId = branchId,

                    DiscountAmount = discount,
                    TaxRate = 0,   // tax is already baked into each item price (same as cashier)

                    AmountPaid = isTableOrder ? 0 : req.AmountPaid,
                    PaymentMethod = req.PaymentMethod,
                    IsPaid = !isTableOrder,
                    PaidAt = isTableOrder ? null : DateTime.Now
                };

                var createdOrder = await _orderService.CreateOrderAsync(order, orderItems);

                return Json(new
                {
                    success = true,
                    orderId = createdOrder.Id,
                    orderNumber = createdOrder.OrderNumber,
                    total = createdOrder.Total
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = ex.Message,
                    details = ex.InnerException?.Message
                });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetOrderForPrint(int? id, int? orderId)
        {
            var resolvedId = id ?? orderId ?? 0;

            var order = await _context.Orders
                .Include(o => o.Items)
                    .ThenInclude(i => i.Product)
                .Include(o => o.Table)
                .Include(o => o.Cashier)
                .FirstOrDefaultAsync(o => o.Id == resolvedId);

            if (order == null)
                return Json(new { success = false, message = "Order not found" });

            order.IsPrinted = true;
            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                order = new
                {
                    order.Id,
                    order.OrderNumber,
                    order.CreatedAt,
                    orderType = order.OrderType.ToString(),
                    status = order.Status.ToString(),
                    paymentMethod = order.PaymentMethod.ToString(),
                    order.SubTotal,
                    order.TaxRate,
                    order.TaxAmount,
                    order.DiscountAmount,
                    order.Total,
                    order.AmountPaid,
                    order.Change,
                    order.CustomerName,
                    order.Notes,
                    tableNumber = order.Table?.TableNumber,
                    waiterName = order.Cashier?.UserName,
                    items = order.Items.Select(i => new
                    {
                        i.ProductId,
                        productName = i.Product != null ? i.Product.Name : "",
                        productNameAr = i.Product != null ? i.Product.NameAr : "",
                        i.Quantity,
                        i.UnitPrice,
                        i.TotalPrice,
                        i.Notes
                    })
                }
            });
        }

        [HttpPost]
        public async Task<IActionResult> RequestBill([FromBody] RequestBillRequest req)
        {
            // Bill the whole table, not one order — a table accumulates several orders
            // before the customer asks to pay.
            var query = _context.Orders
                .Include(o => o.Items).ThenInclude(i => i.Product)
                .Include(o => o.Table)
                .Include(o => o.Cashier)
                .Include(o => o.Branch)
                .Where(o => o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded);

            List<Order> orders;
            if (req.TableId > 0)
            {
                orders = await query
                    .Where(o => o.TableId == req.TableId
                             && o.CreatedAt.Date == DateTime.Today
                             && !o.IsPaid)
                    .OrderBy(o => o.CreatedAt)
                    .ToListAsync();
            }
            else
            {
                orders = await query.Where(o => o.Id == req.OrderId).ToListAsync();
            }

            if (!orders.Any())
                return Json(new { success = false, message = "لا يوجد حساب مفتوح على هذه الطاولة" });

            var allItems = orders.SelectMany(o => o.Items).ToList();

            return Json(new
            {
                success = true,
                order = new
                {
                    Id = orders.Last().Id,
                    OrderNumber = string.Join("، ", orders.Select(o => o.OrderNumber)),
                    orderCount = orders.Count,
                    tableNumber = orders.First().Table?.TableNumber,
                    orderType = orders.First().OrderType.ToString(),
                    status = orders.First().Status.ToString(),
                    createdAt = orders.First().CreatedAt,
                    cashier = orders.First().Cashier != null
                        ? (string.IsNullOrEmpty(orders.First().Cashier!.FullNameAr)
                            ? orders.First().Cashier!.UserName
                            : orders.First().Cashier!.FullNameAr)
                        : null,
                    branchNameAr = orders.First().Branch?.NameAr,
                    customerName = orders.First().CustomerName,
                    SubTotal = orders.Sum(o => o.SubTotal),
                    TaxAmount = orders.Sum(o => o.TaxAmount),
                    DiscountAmount = orders.Sum(o => o.DiscountAmount),
                    Total = orders.Sum(o => o.Total),
                    items = allItems
                        .GroupBy(i => new { i.ProductId, i.UnitPrice })
                        .Select(g => new
                        {
                            productName = g.First().ProductName,
                            productNameAr = g.First().ProductNameAr,
                            Quantity = g.Sum(i => i.Quantity),
                            UnitPrice = g.Key.UnitPrice,
                            TotalPrice = g.Sum(i => i.TotalPrice)
                        })
                }
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetTableOrder(int tableId)
        {
            // A table accumulates orders until the cashier settles it, so return the
            // FULL running tab — not just the most recent order (which made the table
            // look like it had been reset every time something new was sent).
            var orders = await _context.Orders
                .Include(o => o.Items)
                    .ThenInclude(i => i.Product)
                .Where(o => o.TableId == tableId
                         && o.CreatedAt.Date == DateTime.Today
                         && o.Status != OrderStatus.Cancelled
                         && o.Status != OrderStatus.Refunded
                         && !o.IsPaid)
                .OrderBy(o => o.CreatedAt)
                .ToListAsync();

            if (!orders.Any())
                return Json(new { hasOrder = false });

            var allItems = orders.SelectMany(o => o.Items).ToList();

            // Still "Pending" if anything is waiting on the kitchen
            var status = orders.Any(o => o.Status == OrderStatus.Pending) ? "Pending"
                       : orders.Any(o => o.Status == OrderStatus.Preparing) ? "Preparing"
                       : orders.Any(o => o.Status == OrderStatus.Ready) ? "Ready"
                       : "Completed";

            return Json(new
            {
                hasOrder = true,
                orderId = orders.Last().Id,
                orderNumber = string.Join("، ", orders.Select(o => o.OrderNumber)),
                orderCount = orders.Count,
                status,
                total = orders.Sum(o => o.Total),
                itemCount = allItems.Sum(i => i.Quantity),
                orders = orders.Select(o => new
                {
                    o.Id,
                    o.OrderNumber,
                    time = o.CreatedAt.ToString("HH:mm"),
                    o.Total,
                    status = o.Status.ToString()
                }),
                items = allItems
                    .GroupBy(i => new { i.ProductId, i.UnitPrice, i.Notes })
                    .Select(g => new
                    {
                        productName = g.First().ProductName,
                        productNameAr = g.First().ProductNameAr,
                        Quantity = g.Sum(i => i.Quantity),
                        UnitPrice = g.Key.UnitPrice,
                        TotalPrice = g.Sum(i => i.TotalPrice)
                    })
            });
        }

        [HttpPost]
        public async Task<IActionResult> FreeTable([FromBody] FreeTableRequest req)
        {
            var table = await _context.DiningTables.FindAsync(req.TableId);

            if (table == null)
                return Json(new { success = false, message = "Table not found" });

            // Don't let a table be cleared while money is still owed on it —
            // the cashier must settle the tab from the cashier screen first.
            var outstanding = await _context.Orders
                .Where(o => o.TableId == req.TableId
                         && !o.IsPaid
                         && o.CreatedAt.Date == DateTime.Today
                         && o.Status != OrderStatus.Cancelled
                         && o.Status != OrderStatus.Refunded)
                .SumAsync(o => (decimal?)o.Total) ?? 0;

            if (outstanding > 0)
                return Json(new
                {
                    success = false,
                    message = $"لا يمكن تفريغ الطاولة — يوجد حساب مفتوح بقيمة {outstanding:0.##} ج.م لم يتم تحصيله بعد"
                });

            table.Status = TableStatus.Cleaning;
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
    }

    public class WaiterOrderRequest
    {
        public List<WaiterOrderItemRequest> Items { get; set; } = new();
        public int? TableId { get; set; }
        public OrderType OrderType { get; set; } = OrderType.DineIn;
        public string? CustomerName { get; set; }
        public string? CustomerPhone { get; set; }
        public string? Notes { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal AmountPaid { get; set; }
        public PaymentMethod PaymentMethod { get; set; }
    }

    public class WaiterOrderItemRequest
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public string? Notes { get; set; }
    }

    public class RequestBillRequest
    {
        public int OrderId { get; set; }
        public int TableId { get; set; }
    }

    public class FreeTableRequest
    {
        public int TableId { get; set; }
    }
}