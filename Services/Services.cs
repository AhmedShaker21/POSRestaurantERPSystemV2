using Microsoft.EntityFrameworkCore;
using RestaurantERP.Data;
using RestaurantERP.Models;

namespace RestaurantERP.Services
{
    public class OrderService
    {
        private readonly ApplicationDbContext _context;
        public OrderService(ApplicationDbContext context) { _context = context; }

        public async Task<Order> CreateOrderAsync(Order order, List<OrderItem> items)
        {
            // Snapshot the cost of each line so profit reporting stays correct even if
            // the product's cost price changes later.
            var productIds = items.Select(i => i.ProductId).Distinct().ToList();
            var costs = await _context.Products
                .Where(p => productIds.Contains(p.Id))
                .Select(p => new { p.Id, p.CostPrice })
                .ToDictionaryAsync(p => p.Id, p => p.CostPrice);

            foreach (var i in items)
            {
                if (i.UnitCost <= 0)
                    i.UnitCost = costs.GetValueOrDefault(i.ProductId, 0);
                i.TotalCost = i.UnitCost * i.Quantity;
            }

            order.OrderNumber = await GenerateOrderNumber();
            order.Items = items;
            order.SubTotal = items.Sum(i => i.TotalPrice);
            order.TaxAmount = order.SubTotal * (order.TaxRate / 100);
            order.Total = order.SubTotal + order.TaxAmount - order.DiscountAmount;
            order.Change = order.AmountPaid - order.Total;

            // All items skip kitchen → Completed immediately (no kitchen needed)
            // Mixed or kitchen items → Pending (goes to kitchen queue)
            if (items.Count > 0 && items.All(i => i.SkipKitchen))
            {
                order.Status = OrderStatus.Completed;
                order.CompletedAt = DateTime.Now;
            }
            else
            {
                order.Status = OrderStatus.Pending;
            }

            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            // ── AUTO-OCCUPY TABLE ─────────────────────────────────
            // The moment an order is sent for a table, that table becomes occupied
            // so the cashier/waiter immediately sees it's taken. It stays occupied
            // until the table's whole bill is settled (CloseTable / SettleTable).
            if (order.TableId.HasValue)
            {
                var table = await _context.DiningTables.FindAsync(order.TableId.Value);
                if (table != null && table.Status != TableStatus.Occupied)
                {
                    table.Status = TableStatus.Occupied;
                    await _context.SaveChangesAsync();
                }
            }

            return order;
        }

        public async Task<string> GenerateOrderNumber()
        {
            var today = DateTime.Today;
            var count = await _context.Orders.CountAsync(o => o.CreatedAt.Date == today);
            return $"ORD-{today:yyyyMMdd}-{(count + 1):D3}";
        }

        public async Task<bool> UpdateOrderStatusAsync(int orderId, OrderStatus status)
        {
            var order = await _context.Orders.FindAsync(orderId);
            if (order == null) return false;
            order.Status = status;
            if (status == OrderStatus.Completed) order.CompletedAt = DateTime.Now;
            await _context.SaveChangesAsync();
            return true;
        }
    }

    public class AnalyticsService
    {
        private readonly ApplicationDbContext _context;
        public AnalyticsService(ApplicationDbContext context) { _context = context; }

        public async Task<DashboardStats> GetDashboardStatsAsync(int? branchId = null)
        {
            var today = DateTime.Today;
            var thisMonth = new DateTime(today.Year, today.Month, 1);
            var lastMonth = thisMonth.AddMonths(-1);
            var weekStart = today.AddDays(-(int)today.DayOfWeek);
            var last30From = today.AddDays(-29);

            var completedStatuses = new[] {
                OrderStatus.Completed,
                OrderStatus.Refunded,
                OrderStatus.PartialRefund
            };

            // ── Branch filter helper ──────────────────────────────
            // When branchId is null → all branches (global view)
            IQueryable<Order> OrdersQ()
            {
                var q = _context.Orders.AsQueryable();
                if (branchId.HasValue) q = q.Where(o => o.BranchId == branchId.Value);
                return q;
            }
            IQueryable<Refund> RefundsQ()
            {
                var q = _context.Refunds.AsQueryable();
                if (branchId.HasValue) q = q.Where(r => r.BranchId == branchId.Value);
                return q;
            }
            IQueryable<Expense> ExpensesQ()
            {
                var q = _context.Expenses.AsQueryable();
                if (branchId.HasValue) q = q.Where(e => e.BranchId == branchId.Value);
                return q;
            }
            IQueryable<OrderItem> OrderItemsQ()
            {
                var q = _context.OrderItems.AsQueryable();
                if (branchId.HasValue) q = q.Where(oi => oi.Order!.BranchId == branchId.Value);
                return q;
            }
            IQueryable<RefundItem> RefundItemsQ()
            {
                var q = _context.RefundItems.AsQueryable();
                if (branchId.HasValue) q = q.Where(ri => ri.Refund!.BranchId == branchId.Value);
                return q;
            }

            // ── Load orders ───────────────────────────────────────
            var todayOrders = await OrdersQ()
                .Where(o => o.CreatedAt.Date == today && completedStatuses.Contains(o.Status))
                .ToListAsync();

            var weekOrders = await OrdersQ()
                .Where(o => o.CreatedAt.Date >= weekStart && completedStatuses.Contains(o.Status))
                .ToListAsync();

            var monthOrders = await OrdersQ()
                .Where(o => o.CreatedAt >= thisMonth && completedStatuses.Contains(o.Status))
                .ToListAsync();

            var lastMonthOrders = await OrdersQ()
                .Where(o => o.CreatedAt >= lastMonth && o.CreatedAt < thisMonth && completedStatuses.Contains(o.Status))
                .ToListAsync();

            var last30Orders = await OrdersQ()
                .Where(o => o.CreatedAt >= last30From && completedStatuses.Contains(o.Status))
                .GroupBy(o => o.CreatedAt.Date)
                .Select(g => new { Date = g.Key, Revenue = g.Sum(o => o.Total), Count = g.Count() })
                .OrderBy(x => x.Date)
                .ToListAsync();

            // ── Load refunds ──────────────────────────────────────
            var todayRefunds = await RefundsQ()
                .Where(r => r.CreatedAt.Date == today && r.Status == RefundStatus.Completed)
                .ToListAsync();

            var weekRefunds = await RefundsQ()
                .Where(r => r.CreatedAt.Date >= weekStart && r.Status == RefundStatus.Completed)
                .ToListAsync();

            var monthRefunds = await RefundsQ()
                .Where(r => r.CreatedAt >= thisMonth && r.Status == RefundStatus.Completed)
                .ToListAsync();

            var lastMonthRefunds = await RefundsQ()
                .Where(r => r.CreatedAt >= lastMonth && r.CreatedAt < thisMonth && r.Status == RefundStatus.Completed)
                .ToListAsync();

            var last30Refunds = await RefundsQ()
                .Where(r => r.CreatedAt >= last30From && r.Status == RefundStatus.Completed)
                .GroupBy(r => r.CreatedAt.Date)
                .Select(g => new { Date = g.Key, Amount = g.Sum(r => r.RefundTotal) })
                .ToListAsync();

            var refundByDay = last30Refunds.ToDictionary(r => r.Date, r => r.Amount);

            // ── Load expenses ─────────────────────────────────────
            var todayExpenses = await ExpensesQ().Where(e => e.Date.Date == today).SumAsync(e => e.Amount);
            var weekExpenses = await ExpensesQ().Where(e => e.Date.Date >= weekStart).SumAsync(e => e.Amount);
            var monthExpenses = await ExpensesQ().Where(e => e.Date >= thisMonth).SumAsync(e => e.Amount);
            var lastMonthExpenses = await ExpensesQ().Where(e => e.Date >= lastMonth && e.Date < thisMonth).SumAsync(e => e.Amount);

            var last30Expenses = await ExpensesQ()
                .Where(e => e.Date >= last30From)
                .GroupBy(e => e.Date.Date)
                .Select(g => new { Date = g.Key, Amount = g.Sum(e => e.Amount) })
                .ToListAsync();

            var expenseByDay = last30Expenses.ToDictionary(e => e.Date, e => e.Amount);

            // ── Load cost of goods sold (COGS) ────────────────────
            var todayCogs = await OrderItemsQ()
                .Include(oi => oi.Product)
                .Where(oi => oi.Order!.CreatedAt.Date == today
                          && completedStatuses.Contains(oi.Order.Status)
                          && oi.Product != null)
                .SumAsync(oi => oi.Product!.CostPrice * oi.Quantity);

            var todayRefundCogs = await RefundItemsQ()
                .Include(ri => ri.OrderItem).ThenInclude(oi => oi!.Product)
                .Where(ri => ri.Refund!.CreatedAt.Date == today
                          && ri.Refund.Status == RefundStatus.Completed
                          && ri.OrderItem != null && ri.OrderItem.Product != null)
                .SumAsync(ri => ri.OrderItem!.Product!.CostPrice * ri.Quantity);

            var weekCogs = await OrderItemsQ()
                .Include(oi => oi.Product)
                .Where(oi => oi.Order!.CreatedAt.Date >= weekStart
                          && completedStatuses.Contains(oi.Order.Status)
                          && oi.Product != null)
                .SumAsync(oi => oi.Product!.CostPrice * oi.Quantity);

            var weekRefundCogs = await RefundItemsQ()
                .Include(ri => ri.OrderItem).ThenInclude(oi => oi!.Product)
                .Where(ri => ri.Refund!.CreatedAt.Date >= weekStart
                          && ri.Refund.Status == RefundStatus.Completed
                          && ri.OrderItem != null && ri.OrderItem.Product != null)
                .SumAsync(ri => ri.OrderItem!.Product!.CostPrice * ri.Quantity);

            var monthCogs = await OrderItemsQ()
                .Include(oi => oi.Product)
                .Where(oi => oi.Order!.CreatedAt >= thisMonth
                          && completedStatuses.Contains(oi.Order.Status)
                          && oi.Product != null)
                .SumAsync(oi => oi.Product!.CostPrice * oi.Quantity);

            var monthRefundCogs = await RefundItemsQ()
                .Include(ri => ri.OrderItem).ThenInclude(oi => oi!.Product)
                .Where(ri => ri.Refund!.CreatedAt >= thisMonth
                          && ri.Refund.Status == RefundStatus.Completed
                          && ri.OrderItem != null && ri.OrderItem.Product != null)
                .SumAsync(ri => ri.OrderItem!.Product!.CostPrice * ri.Quantity);

            var last30CogsRaw = await OrderItemsQ()
                .Include(oi => oi.Product)
                .Where(oi => oi.Order!.CreatedAt >= last30From
                          && completedStatuses.Contains(oi.Order.Status)
                          && oi.Product != null)
                .GroupBy(oi => oi.Order!.CreatedAt.Date)
                .Select(g => new { Date = g.Key, Cogs = g.Sum(oi => oi.Product!.CostPrice * oi.Quantity) })
                .ToListAsync();

            var cogsByDay = last30CogsRaw.ToDictionary(c => c.Date, c => c.Cogs);

            // COGS recovered from refunded items per day
            var last30RefundCogsRaw = await RefundItemsQ()
                .Include(ri => ri.OrderItem).ThenInclude(oi => oi!.Product)
                .Where(ri => ri.Refund!.CreatedAt >= last30From
                          && ri.Refund.Status == RefundStatus.Completed
                          && ri.OrderItem != null && ri.OrderItem.Product != null)
                .GroupBy(ri => ri.Refund!.CreatedAt.Date)
                .Select(g => new { Date = g.Key, Cogs = g.Sum(ri => ri.OrderItem!.Product!.CostPrice * ri.Quantity) })
                .ToListAsync();

            var refundCogsByDay = last30RefundCogsRaw.ToDictionary(c => c.Date, c => c.Cogs);

            // ── Net revenue calcs ─────────────────────────────────
            var todayRefundTotal = todayRefunds.Sum(r => r.RefundTotal);
            var weekRefundTotal = weekRefunds.Sum(r => r.RefundTotal);
            var monthRefundTotal = monthRefunds.Sum(r => r.RefundTotal);
            var lastMonthRefundTot = lastMonthRefunds.Sum(r => r.RefundTotal);

            var todayNetRevenue = Math.Max(0, todayOrders.Sum(o => o.Total) - todayRefundTotal);
            var weekNetRevenue = Math.Max(0, weekOrders.Sum(o => o.Total) - weekRefundTotal);
            var monthNetRevenue = Math.Max(0, monthOrders.Sum(o => o.Total) - monthRefundTotal);
            var lastMonthNetRev = Math.Max(0, lastMonthOrders.Sum(o => o.Total) - lastMonthRefundTot);

            var todayNetCogs = Math.Max(0, todayCogs - todayRefundCogs);
            var weekNetCogs = Math.Max(0, weekCogs - weekRefundCogs);
            var monthNetCogs = Math.Max(0, monthCogs - monthRefundCogs);

            var todayGrossProfit = todayNetRevenue - todayNetCogs;
            var weekGrossProfit = weekNetRevenue - weekNetCogs;
            var monthGrossProfit = monthNetRevenue - monthNetCogs;

            var todayNetProfit = todayGrossProfit - todayExpenses;
            var weekNetProfit = weekGrossProfit - weekExpenses;
            var monthNetProfit = monthGrossProfit - monthExpenses;

            var lastMonthNetCogs = await OrderItemsQ()
                .Include(oi => oi.Product)
                .Where(oi => oi.Order!.CreatedAt >= lastMonth && oi.Order.CreatedAt < thisMonth
                          && completedStatuses.Contains(oi.Order.Status) && oi.Product != null)
                .SumAsync(oi => oi.Product!.CostPrice * oi.Quantity);
            var lastMonthNetProfit = Math.Max(0, lastMonthNetRev) - lastMonthNetCogs - lastMonthExpenses;

            var profitGrowth = lastMonthNetProfit > 0
                ? Math.Round(((monthNetProfit - lastMonthNetProfit) / lastMonthNetProfit) * 100, 1) : 0;
            var revenueGrowth = lastMonthNetRev > 0
                ? Math.Round(((monthNetRevenue - lastMonthNetRev) / lastMonthNetRev) * 100, 1) : 0;

            var todayProfitMargin = todayNetRevenue > 0 ? Math.Round((todayNetProfit / todayNetRevenue) * 100, 1) : 0;
            var weekProfitMargin = weekNetRevenue > 0 ? Math.Round((weekNetProfit / weekNetRevenue) * 100, 1) : 0;
            var monthProfitMargin = monthNetRevenue > 0 ? Math.Round((monthNetProfit / monthNetRevenue) * 100, 1) : 0;

            var allDates = Enumerable.Range(0, 30).Select(i => last30From.AddDays(i)).ToList();
            var dailyProfitSeries = allDates.Select(date => {
                var grossRev = last30Orders.FirstOrDefault(x => x.Date == date)?.Revenue ?? 0;
                var refunds = refundByDay.GetValueOrDefault(date, 0);
                var cogs = cogsByDay.GetValueOrDefault(date, 0);
                var refCogs = refundCogsByDay.GetValueOrDefault(date, 0);
                var expenses = expenseByDay.GetValueOrDefault(date, 0);
                var netRev = Math.Max(0, grossRev - refunds);
                var netCogs = Math.Max(0, cogs - refCogs);
                return new DailyProfitDto
                {
                    Date = date,
                    Revenue = netRev,
                    Cogs = netCogs,
                    Expenses = expenses,
                    GrossProfit = netRev - netCogs,
                    NetProfit = netRev - netCogs - expenses,
                    Orders = last30Orders.FirstOrDefault(x => x.Date == date)?.Count ?? 0
                };
            }).ToList();

            var weeklyBreakdown = Enumerable.Range(0, 7).Select(i => {
                var date = weekStart.AddDays(i);
                var dp = dailyProfitSeries.FirstOrDefault(x => x.Date == date);
                return new WeeklyDayDto
                {
                    DayName = date.ToString("ddd"),
                    DayNameAr = GetArabicDayName(date.DayOfWeek),
                    Date = date,
                    Revenue = dp?.Revenue ?? 0,
                    NetProfit = dp?.NetProfit ?? 0,
                    Orders = dp?.Orders ?? 0,
                    IsToday = date == today
                };
            }).ToList();

            // ── Top products ──────────────────────────────────────
            var topProducts = await OrderItemsQ()
                .Include(oi => oi.Product)
                .Where(oi => oi.Order!.CreatedAt >= thisMonth && completedStatuses.Contains(oi.Order.Status))
                .GroupBy(oi => new { oi.ProductId })
                .Select(g => new ProductSalesDto
                {
                    ProductId = g.Key.ProductId,
                    Name = g.Max(oi => oi.ProductName) ?? "",
                    NameAr = g.Max(oi => oi.ProductNameAr) ?? "",
                    Quantity = g.Sum(oi => oi.Quantity),
                    Revenue = g.Sum(oi => oi.TotalPrice)
                })
                .OrderByDescending(x => x.Revenue).Take(10).ToListAsync();

            foreach (var p in topProducts.Where(p => string.IsNullOrEmpty(p.Name)))
            {
                var product = await _context.Products.FindAsync(p.ProductId);
                if (product != null) { p.Name = product.Name; p.NameAr = product.NameAr; p.CostPrice = product.CostPrice; }
            }
            foreach (var p in topProducts.Where(p => p.CostPrice == 0))
            {
                var product = await _context.Products.FindAsync(p.ProductId);
                if (product != null) p.CostPrice = product.CostPrice;
            }

            // ── Category revenue ──────────────────────────────────
            var categoryRevenue = await OrderItemsQ()
                .Include(oi => oi.Product).ThenInclude(p => p!.Category)
                .Where(oi => oi.Order!.CreatedAt >= thisMonth && completedStatuses.Contains(oi.Order.Status))
                .Where(oi => oi.Product != null && oi.Product.Category != null)
                .GroupBy(oi => new {
                    oi.Product!.Category!.Id,
                    oi.Product.Category.Name,
                    oi.Product.Category.NameAr,
                    oi.Product.Category.ColorHex
                })
                .Select(g => new CategoryRevenueDto
                {
                    Name = g.Key.Name,
                    NameAr = g.Key.NameAr,
                    ColorHex = g.Key.ColorHex,
                    Revenue = g.Sum(oi => oi.TotalPrice)
                })
                .OrderByDescending(x => x.Revenue).ToListAsync();

            // ── Misc ──────────────────────────────────────────────
            var pendingOrders = await OrdersQ()
                .Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Preparing)
                .CountAsync();

            var lowStockProducts = await _context.Products
                .Where(p => p.TrackStock && p.StockQuantity <= p.MinStockAlert && p.IsActive)
                .CountAsync();

            return new DashboardStats
            {
                // Sales
                TodaySales = todayNetRevenue,
                TodayOrders = todayOrders.Count,
                MonthSales = monthNetRevenue,
                MonthOrders = monthOrders.Count,
                RevenueGrowth = revenueGrowth,
                PendingOrders = pendingOrders,
                LowStockCount = lowStockProducts,

                // Refunds
                TodayRefundAmount = todayRefundTotal,
                TodayRefundCount = todayRefunds.Count,

                // Profit
                TodayNetProfit = todayNetProfit,
                TodayGrossProfit = todayGrossProfit,
                TodayExpenses = todayExpenses,
                TodayCogs = todayNetCogs,
                TodayProfitMargin = todayProfitMargin,

                WeekNetProfit = weekNetProfit,
                WeekRevenue = weekNetRevenue,
                WeekExpenses = weekExpenses,
                WeekProfitMargin = weekProfitMargin,

                MonthNetProfit = monthNetProfit,
                MonthGrossProfit = monthGrossProfit,
                MonthExpenses = monthExpenses,
                MonthCogs = monthNetCogs,
                MonthProfitMargin = monthProfitMargin,
                ProfitGrowth = profitGrowth,

                DailyProfit = dailyProfitSeries,
                WeeklyBreakdown = weeklyBreakdown,
                DailySales = dailyProfitSeries.Select(d => new DailySalesDto
                {
                    Date = d.Date,
                    Revenue = d.Revenue,
                    Orders = d.Orders
                }).ToList(),
                TopProducts = topProducts,
                CategoryRevenue = categoryRevenue
            };
        }

        private static string GetArabicDayName(DayOfWeek day) => day switch
        {
            DayOfWeek.Sunday => "الأحد",
            DayOfWeek.Monday => "الاثنين",
            DayOfWeek.Tuesday => "الثلاثاء",
            DayOfWeek.Wednesday => "الأربعاء",
            DayOfWeek.Thursday => "الخميس",
            DayOfWeek.Friday => "الجمعة",
            DayOfWeek.Saturday => "السبت",
            _ => ""
        };
    }

    // DTOs
    public class DashboardStats
    {
        // Sales
        public decimal TodaySales { get; set; }
        public int TodayOrders { get; set; }
        public decimal MonthSales { get; set; }
        public int MonthOrders { get; set; }
        public decimal RevenueGrowth { get; set; }
        public int PendingOrders { get; set; }
        public int LowStockCount { get; set; }

        // Refunds
        public decimal TodayRefundAmount { get; set; }
        public int TodayRefundCount { get; set; }

        // Today Profit
        public decimal TodayNetProfit { get; set; }
        public decimal TodayGrossProfit { get; set; }
        public decimal TodayExpenses { get; set; }
        public decimal TodayCogs { get; set; }
        public decimal TodayProfitMargin { get; set; }

        // Week Profit
        public decimal WeekNetProfit { get; set; }
        public decimal WeekRevenue { get; set; }
        public decimal WeekExpenses { get; set; }
        public decimal WeekProfitMargin { get; set; }

        // Month Profit
        public decimal MonthNetProfit { get; set; }
        public decimal MonthGrossProfit { get; set; }
        public decimal MonthExpenses { get; set; }
        public decimal MonthCogs { get; set; }
        public decimal MonthProfitMargin { get; set; }
        public decimal ProfitGrowth { get; set; }

        // Series
        public List<DailySalesDto> DailySales { get; set; } = new();
        public List<DailyProfitDto> DailyProfit { get; set; } = new();
        public List<WeeklyDayDto> WeeklyBreakdown { get; set; } = new();
        public List<ProductSalesDto> TopProducts { get; set; } = new();
        public List<CategoryRevenueDto> CategoryRevenue { get; set; } = new();
    }

    public class DailySalesDto
    {
        public DateTime Date { get; set; }
        public decimal Revenue { get; set; }
        public int Orders { get; set; }
    }

    public class DailyProfitDto
    {
        public DateTime Date { get; set; }
        public decimal Revenue { get; set; }
        public decimal Cogs { get; set; }
        public decimal Expenses { get; set; }
        public decimal GrossProfit { get; set; }
        public decimal NetProfit { get; set; }
        public int Orders { get; set; }
    }

    public class WeeklyDayDto
    {
        public string DayName { get; set; } = "";
        public string DayNameAr { get; set; } = "";
        public DateTime Date { get; set; }
        public decimal Revenue { get; set; }
        public decimal NetProfit { get; set; }
        public int Orders { get; set; }
        public bool IsToday { get; set; }
    }

    public class ProductSalesDto
    {
        public int ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string NameAr { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Revenue { get; set; }
        public decimal CostPrice { get; set; }
    }

    public class CategoryRevenueDto
    {
        public string Name { get; set; } = string.Empty;
        public string NameAr { get; set; } = string.Empty;
        public string ColorHex { get; set; } = "#000";
        public decimal Revenue { get; set; }
    }
}