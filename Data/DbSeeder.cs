using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RestaurantERP.Models;

namespace RestaurantERP.Data
{
    public static class DbSeeder
    {
        public static async Task SeedAsync(IServiceProvider serviceProvider, bool autoMigrate = true, bool seedDemoData = false)
        {
            using var scope = serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

            if (autoMigrate)
            {
                await context.Database.MigrateAsync();
            }
            else
            {
                // Fail fast and loudly rather than running against a stale schema
                var pending = await context.Database.GetPendingMigrationsAsync();
                if (pending.Any())
                    throw new InvalidOperationException(
                        "Pending migrations: " + string.Join(", ", pending) +
                        ". Run Update-Database during a maintenance window, or set Hosting:AutoMigrate=true.");
            }

            // ── Roles ─────────────────────────────────────────────
            string[] roles = { "Admin", "Manager", "Cashier", "Kitchen", "محصل" };
            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                    await roleManager.CreateAsync(new IdentityRole(role));
                // Ensure NormalizedName matches (fixes Arabic role 403 issue)
                var existing = await roleManager.FindByNameAsync(role);
                if (existing != null && existing.NormalizedName != role)
                {
                    existing.NormalizedName = role;
                    await roleManager.UpdateAsync(existing);
                }
            }

            // ── Admin user ────────────────────────────────────────
            if (await userManager.FindByEmailAsync("admin@restaurant.com") == null)
            {
                var admin = new ApplicationUser
                {
                    UserName = "admin@restaurant.com",
                    Email = "admin@restaurant.com",
                    FullName = "System Administrator",
                    FullNameAr = "مدير النظام",
                    EmailConfirmed = true,
                    IsActive = true
                };
                await userManager.CreateAsync(admin, "Admin@123");
                await userManager.AddToRoleAsync(admin, "Admin");
            }

            // ── Manager user ──────────────────────────────────────
            if (seedDemoData && await userManager.FindByEmailAsync("manager@restaurant.com") == null)
            {
                var manager = new ApplicationUser
                {
                    UserName = "manager@restaurant.com",
                    Email = "manager@restaurant.com",
                    FullName = "Ahmed Manager",
                    FullNameAr = "أحمد المدير",
                    EmailConfirmed = true,
                    IsActive = true
                };
                await userManager.CreateAsync(manager, "Manager@123");
                await userManager.AddToRoleAsync(manager, "Manager");
            }

            // ── Cashier user ──────────────────────────────────────
            if (seedDemoData && await userManager.FindByEmailAsync("cashier@restaurant.com") == null)
            {
                var cashier = new ApplicationUser
                {
                    UserName = "cashier@restaurant.com",
                    Email = "cashier@restaurant.com",
                    FullName = "Mohamed Cashier",
                    FullNameAr = "محمد الكاشير",
                    EmailConfirmed = true,
                    IsActive = true
                };
                await userManager.CreateAsync(cashier, "Cashier@123");
                await userManager.AddToRoleAsync(cashier, "Cashier");
            }

            // ── Kitchen user ──────────────────────────────────────
            if (seedDemoData && await userManager.FindByEmailAsync("kitchen@restaurant.com") == null)
            {
                var kitchen = new ApplicationUser
                {
                    UserName = "kitchen@restaurant.com",
                    Email = "kitchen@restaurant.com",
                    FullName = "Kitchen Staff",
                    FullNameAr = "موظف المطبخ",
                    EmailConfirmed = true,
                    IsActive = true
                };
                await userManager.CreateAsync(kitchen, "Kitchen@123");
                await userManager.AddToRoleAsync(kitchen, "Kitchen");
            }

            // ── Branches ──────────────────────────────────────────
            if (!context.Branches.Any())
            {
                var managerUser = await userManager.FindByEmailAsync("manager@restaurant.com");

                var branches = new List<Branch>
                {
                    new() {
                        Name = "Main Branch",  NameAr = "الفرع بجانب البوابه",
                        Address = "Cairo, Egypt", Phone = "02-12345678",
                        Email = "main@restaurant.com", ManagerId = managerUser?.Id,
                        IsActive = true, IsMainBranch = true,
                        ColorHex = "#2563a8", Icon = "🏢"
                    },
                    new() {
                        Name = "Branch 2", NameAr = "فرع قاعة الافراح",
                        Address = "Cairo", Phone = "02-22345678",
                        Email = "Branch02@restaurant.com",
                        IsActive = true, IsMainBranch = false,
                        ColorHex = "#22c55e", Icon = "🏪"
                    },
                    new() {
                        Name = "Branch 3", NameAr = "فرع اخر النادي",
                        Address = "Cairo", Phone = "02-32345678",
                        Email = "Branch03@restaurant.com",
                        IsActive = true, IsMainBranch = false,
                        ColorHex = "#f59e0b", Icon = "🏬"
                    }
                };
                context.Branches.AddRange(branches);
                await context.SaveChangesAsync();

                var mainBranchId = branches[0].Id;
                var branch2Id = branches[1].Id;

                var allUsers = userManager.Users.ToList();
                foreach (var user in allUsers)
                {
                    context.UserBranches.Add(new UserBranch
                    {
                        UserId = user.Id,
                        BranchId = mainBranchId,
                        IsPrimary = true
                    });
                    user.DefaultBranchId = mainBranchId;
                    await userManager.UpdateAsync(user);
                }
                await context.SaveChangesAsync();
            }

            // ── Categories (demo only) ────────────────────────────
            if (seedDemoData && !context.Categories.Any())
            {
                var categories = new List<Category>
                {
                    new() { Name = "Hot Drinks",  NameAr = "مشروبات ساخنة", Icon = "☕", ColorHex = "#8B4513" },
                    new() { Name = "Cold Drinks", NameAr = "مشروبات باردة", Icon = "🧊", ColorHex = "#00BFFF" },
                    new() { Name = "Main Dishes", NameAr = "أطباق رئيسية",  Icon = "🍽️", ColorHex = "#FF6B35" },
                    new() { Name = "Sandwiches",  NameAr = "سندوتشات",      Icon = "🥪", ColorHex = "#FFD700" },
                    new() { Name = "Salads",      NameAr = "سلطات",          Icon = "🥗", ColorHex = "#32CD32" },
                    new() { Name = "Desserts",    NameAr = "حلويات",         Icon = "🍰", ColorHex = "#FF69B4" },
                    new() { Name = "Juices",      NameAr = "عصائر",          Icon = "🥤", ColorHex = "#FFA500" },
                    new() { Name = "Soups",       NameAr = "شوربات",         Icon = "🥣", ColorHex = "#DC143C" },
                };
                context.Categories.AddRange(categories);
                await context.SaveChangesAsync();
            }

            // ── Products ──────────────────────────────────────────
            // Helper: create a product with all new fields defaulted — avoids NOT NULL errors
            static Product P(string name, string nameAr, decimal price, decimal cost, int catId, bool trackStock = false, int stock = 0) =>
                new()
                {
                    Name = name,
                    NameAr = nameAr,
                    Price = price,
                    CostPrice = cost,
                    CategoryId = catId,
                    IsAvailable = true,
                    IsActive = true,
                    TrackStock = trackStock,
                    StockQuantity = stock,
                    // ── New fields — must be set explicitly to avoid NULL errors ──
                    Barcode = string.Empty,
                    BoxBarcode = string.Empty,
                    SellByBox = false,
                    UnitsPerBox = 1,
                    BoxSellPrice = 0,
                    BoxCostPrice = 0,
                    TaxRateOverride = null,
                };

            if (seedDemoData && !context.Products.Any())
            {
                var cats = await context.Categories.ToListAsync();
                var catDict = cats.ToDictionary(c => c.Name);

                var products = new List<Product>
                {
                    // ── Hot Drinks ───────────────────────────────
                    P("Egyptian Tea",    "شاي مصري",          5,  1,  catDict["Hot Drinks"].Id, true, 100),
                    P("Nescafe",         "نسكافيه",           15, 5,  catDict["Hot Drinks"].Id),
                    P("Turkish Coffee",  "قهوة تركي",         20, 7,  catDict["Hot Drinks"].Id),
                    P("Karak Tea",       "شاي كرك",           15, 4,  catDict["Hot Drinks"].Id),
                    P("Hot Chocolate",   "شوكولاتة ساخنة",    25, 10, catDict["Hot Drinks"].Id),
                    // ── Cold Drinks ──────────────────────────────
                    P("Pepsi",           "بيبسي",             15, 7,  catDict["Cold Drinks"].Id, true, 50),
                    P("7Up",             "سبن أب",            15, 7,  catDict["Cold Drinks"].Id, true, 40),
                    P("Water Bottle",    "مياه معدنية",       5,  2,  catDict["Cold Drinks"].Id, true, 100),
                    P("Lemon Mint",      "ليمون بالنعناع",    20, 5,  catDict["Cold Drinks"].Id),
                    P("Blue Hawaii",     "بلو هاواي",         35, 12, catDict["Cold Drinks"].Id),
                    P("Pina Colada",     "بنا كولادا",        35, 12, catDict["Cold Drinks"].Id),
                    // ── Main Dishes ──────────────────────────────
                    P("Grilled Chicken", "فراخ مشوية",        85, 35, catDict["Main Dishes"].Id),
                    P("Kofta",           "كفتة",              65, 28, catDict["Main Dishes"].Id),
                    P("Fish Fillet",     "فيليه سمك",         90, 40, catDict["Main Dishes"].Id),
                    P("Chicken Tikka",   "تيكا دجاج",         95, 42, catDict["Main Dishes"].Id),
                    P("Mixed Grill",     "مشاوي مشكلة",      150, 65, catDict["Main Dishes"].Id),
                    // ── Sandwiches ───────────────────────────────
                    P("Falafel Sandwich","سندوتش فلافل",      10, 3,  catDict["Sandwiches"].Id),
                    P("Hawawshi",        "هوواوشي",           35, 15, catDict["Sandwiches"].Id),
                    P("Club Sandwich",   "كلوب سندوتش",       55, 22, catDict["Sandwiches"].Id),
                    P("Chicken Burger",  "برجر دجاج",         65, 28, catDict["Sandwiches"].Id),
                    // ── Salads ───────────────────────────────────
                    P("Green Salad",     "سلطة خضراء",        25, 8,  catDict["Salads"].Id),
                    P("Fattoush",        "فتوش",              30, 10, catDict["Salads"].Id),
                    P("Caesar Salad",    "سيزر سلطة",         45, 18, catDict["Salads"].Id),
                    // ── Desserts ─────────────────────────────────
                    P("Om Ali",          "أم علي",            35, 12, catDict["Desserts"].Id),
                    P("Kunafa",          "كنافة",             40, 15, catDict["Desserts"].Id),
                    P("Ice Cream",       "آيس كريم",          25, 10, catDict["Desserts"].Id),
                    // ── Juices ───────────────────────────────────
                    P("Fresh Orange Juice","عصير برتقال طازج",30, 10, catDict["Juices"].Id),
                    P("Mango Juice",     "عصير مانجو",        35, 12, catDict["Juices"].Id),
                    P("Strawberry Juice","عصير فراولة",       30, 12, catDict["Juices"].Id),
                    // ── Soups ────────────────────────────────────
                    P("Lentil Soup",     "شوربة عدس",         25, 8,  catDict["Soups"].Id),
                    P("Chicken Soup",    "شوربة دجاج",        30, 10, catDict["Soups"].Id),
                };

                context.Products.AddRange(products);
                await context.SaveChangesAsync();

                // ── Assign all products to all branches (many-to-many) ──
                var allBranches = await context.Branches.Select(b => b.Id).ToListAsync();
                foreach (var product in products)
                {
                    foreach (var bId in allBranches)
                    {
                        context.ProductBranches.Add(new ProductBranch
                        {
                            ProductId = product.Id,
                            BranchId = bId
                        });
                    }
                }
                await context.SaveChangesAsync();
            }

            // ── Tables (demo only) ────────────────────────────────
            if (seedDemoData && !context.DiningTables.Any())
            {
                var mainBranch = await context.Branches.FirstOrDefaultAsync(b => b.IsMainBranch);
                var branch2 = await context.Branches.FirstOrDefaultAsync(b => !b.IsMainBranch && b.IsActive);
                var mainBranchId = mainBranch?.Id ?? 1;
                var b2Id = branch2?.Id ?? mainBranchId;

                var tables = new List<DiningTable>();
                for (int i = 1; i <= 10; i++)
                    tables.Add(new DiningTable
                    {
                        TableNumber = i.ToString("D2"),
                        Capacity = i <= 4 ? 2 : i <= 8 ? 4 : 6,
                        Section = i <= 4 ? "Indoor A" : i <= 8 ? "Indoor B" : "Outdoor",
                        Status = TableStatus.Available,
                        BranchId = mainBranchId
                    });
                for (int i = 1; i <= 5; i++)
                    tables.Add(new DiningTable
                    {
                        TableNumber = i.ToString("D2"),
                        Capacity = i <= 2 ? 2 : 4,
                        Section = "Main Hall",
                        Status = TableStatus.Available,
                        BranchId = b2Id
                    });

                context.DiningTables.AddRange(tables);
                await context.SaveChangesAsync();
            }

            // ── System Settings ───────────────────────────────────
            if (!context.SystemSettings.Any())
            {
                context.SystemSettings.AddRange(
                    new SystemSettings { Key = "OrgName", Value = "نادي دار الطائرات" },
                    new SystemSettings { Key = "OrgNameEn", Value = "Aircraft Factory Club" },
                    new SystemSettings { Key = "TaxRate", Value = "14" },
                    new SystemSettings { Key = "Currency", Value = "EGP" },
                    new SystemSettings { Key = "CurrencyAr", Value = "ج.م" },
                    new SystemSettings { Key = "Phone", Value = "02-12345678" },
                    new SystemSettings { Key = "Address", Value = "القاهرة، مصر" },
                    new SystemSettings { Key = "FooterNote", Value = "شكراً لزيارتكم - Thank you for your visit" }
                );
                await context.SaveChangesAsync();
            }

            // ── Sample orders (demo only) ─────────────────────────
            if (seedDemoData && !context.Orders.Any())
            {
                var rng = new Random(42);
                var products = await context.Products.ToListAsync();
                var cashierUser = await userManager.FindByEmailAsync("cashier@restaurant.com");
                var tables = await context.DiningTables.ToListAsync();
                var branchIds = await context.Branches.Select(b => b.Id).ToListAsync();

                for (int day = 29; day >= 1; day--)
                {
                    int count = rng.Next(8, 25);
                    for (int o = 0; o < count; o++)
                    {
                        var selected = products.OrderBy(_ => rng.Next()).Take(rng.Next(1, 5)).ToList();
                        var items = selected.Select(p =>
                        {
                            var qty = rng.Next(1, 4);
                            return new OrderItem
                            {
                                ProductId = p.Id,
                                ProductName = p.Name,
                                ProductNameAr = p.NameAr,
                                UnitPrice = p.Price,
                                Quantity = qty,
                                TotalPrice = p.Price * qty,
                                SkipKitchen = false
                            };
                        }).ToList();

                        var sub = items.Sum(i => i.TotalPrice);
                        var tax = Math.Round(sub * 0.14m, 2);
                        var discount = rng.Next(0, 4) == 0 ? Math.Round(sub * 0.10m, 2) : 0;
                        var total = sub + tax - discount;
                        var bId = branchIds[rng.Next(branchIds.Count)];

                        context.Orders.Add(new Order
                        {
                            OrderNumber = $"ORD-{DateTime.Now.AddDays(-day):yyyyMMdd}-{o + 1:D3}",
                            CreatedAt = DateTime.Now.AddDays(-day).AddHours(rng.Next(8, 22)).AddMinutes(rng.Next(0, 60)),
                            CompletedAt = DateTime.Now.AddDays(-day).AddHours(rng.Next(9, 23)),
                            Status = OrderStatus.Completed,
                            OrderType = (OrderType)rng.Next(0, 3),
                            TableId = tables.Count > 0 && rng.Next(0, 2) == 0 ? tables[rng.Next(tables.Count)].Id : null,
                            CashierId = cashierUser?.Id,
                            BranchId = bId,
                            SubTotal = sub,
                            TaxRate = 14,
                            TaxAmount = tax,
                            DiscountAmount = discount,
                            Total = total,
                            AmountPaid = total + rng.Next(0, 2) * 10,
                            Change = rng.Next(0, 2) * 10m,
                            PaymentMethod = (PaymentMethod)rng.Next(0, 3),
                            IsPrinted = true,
                            Items = items
                        });
                    }
                }
                await context.SaveChangesAsync();
            }
        }
    }
}
