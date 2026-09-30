# نادي مصنع الطائرات — Restaurant ERP System

## Quick Setup

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 1. Extract and open the project
```bash
cd RestaurantERP
```

### 2. Restore dependencies
```bash
dotnet restore
```

### 3. Apply the database migration
```bash
dotnet ef migrations add Initial
dotnet ef database update
```

### 4. Run the application
```bash
dotnet run
```

Open your browser at `https://localhost:5001` (or `http://localhost:5000`)

---

## Demo Accounts

| Role     | Email                    | Password     |
|----------|--------------------------|--------------|
| Admin    | admin@restaurant.com     | Admin@123    |
| Manager  | manager@restaurant.com   | Manager@123  |
| Cashier  | cashier@restaurant.com   | Cashier@123  |
| Kitchen  | kitchen@restaurant.com   | Kitchen@123  |

---

## Features

### 🔐 Roles & Access
- **Admin** — Full access: dashboard analytics, CRUD products/categories/users, orders, expenses, inventory, settings, shifts, reports
- **Manager** — Dashboard, all orders, reports
- **Cashier** — POS terminal, order history, shift management
- **Kitchen** — Live kitchen display with auto-refresh, order status updates

### 🌙 Dark Mode / Light Mode
Toggle using the moon/sun icon in the top navigation bar. Preference is saved automatically.

### 🌐 Arabic / English
Toggle using the AR/EN button in the top navigation bar. The entire UI switches language and direction (RTL/LTR).

### 🧾 POS Cashier
- Category tabs + search for products
- Cart with quantity controls
- Dine In / Takeaway / Delivery
- Table selection for Dine In
- Payment: Cash / Card / Digital Wallet
- Auto change calculation
- Printable invoice with نادي مصنع الطائرات header

### 📊 Admin Dashboard
- Today's sales, orders, growth metrics
- Revenue line chart (switchable revenue/orders)
- Category doughnut chart
- Top products ranking
- Quick action shortcuts

### 🍳 Kitchen Display
- Live order queue (auto-refreshes every 30 seconds)
- Color-coded by status (New → Preparing → Ready)
- Urgency timer (red alert after 15 minutes)
- One-tap status updates

### 📦 Inventory
- Stock tracking per product
- Low stock alerts (≤10 units)
- Adjust stock: Set / Add / Subtract
- Reason logging

### 💰 Expenses
- Log and categorize expenses
- Monthly totals
- Filter by category

### 📈 Reports
- Revenue trend chart
- Payment method breakdown
- Top selling products
- Order type analysis
- CSV export

---

## Tech Stack
- **Backend**: ASP.NET 8 MVC + Entity Framework Core
- **Database**: SQLite (development) — change to SQL Server in `appsettings.json`
- **Auth**: ASP.NET Identity with roles
- **Frontend**: Custom CSS (dark/light mode, RTL/LTR) + Vanilla JS + Chart.js
- **Fonts**: Cairo (Arabic), Poppins (English) via Google Fonts
