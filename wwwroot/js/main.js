// =============================================
// RESTAURANT ERP - MAIN JS
// نادي مصنع الطائرات
// =============================================

'use strict';

// ===== THEME MANAGER =====
const ThemeManager = {
  init() {
    const saved = localStorage.getItem('erp-theme') || 'light';
    this.apply(saved);
  },
  apply(theme) {
    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem('erp-theme', theme);
    const btn = document.getElementById('themeToggle');
    if (btn) btn.innerHTML = theme === 'dark' ? '☀️' : '🌙';
  },
  toggle() {
    const current = document.documentElement.getAttribute('data-theme') || 'light';
    this.apply(current === 'dark' ? 'light' : 'dark');
  }
};

// ===== LANGUAGE MANAGER =====
const LangManager = {
  translations: {
    ar: {
      dashboard: 'لوحة التحكم', products: 'المنتجات', categories: 'الفئات',
      orders: 'الطلبات', users: 'المستخدمون', tables: 'الطاولات',
      expenses: 'المصروفات', inventory: 'المخزون', reports: 'التقارير',
      settings: 'الإعدادات', cashier: 'الكاشير', kitchen: 'المطبخ',
      logout: 'تسجيل الخروج', search: 'بحث...', add: 'إضافة',
      edit: 'تعديل', delete: 'حذف', save: 'حفظ', cancel: 'إلغاء',
      confirm: 'تأكيد', yes: 'نعم', no: 'لا', name: 'الاسم',
      price: 'السعر', category: 'الفئة', status: 'الحالة',
      actions: 'الإجراءات', total: 'الإجمالي', subtotal: 'المجموع الفرعي',
      tax: 'الضريبة', discount: 'الخصم', payment: 'الدفع',
      cash: 'نقدي', card: 'بطاقة', digital: 'إلكتروني',
      'dine-in': 'صالة', takeaway: 'تيك أواي', delivery: 'توصيل',
      pending: 'معلق', preparing: 'يتحضر', ready: 'جاهز',
      completed: 'مكتمل', cancelled: 'ملغي', today: 'اليوم',
      'this-month': 'هذا الشهر', 'today-sales': 'مبيعات اليوم',
      'total-orders': 'إجمالي الطلبات', 'monthly-revenue': 'إيرادات الشهر',
      'pending-orders': 'طلبات معلقة', 'low-stock': 'مخزون منخفض',
      'top-products': 'أعلى المنتجات مبيعاً', 'sales-analytics': 'تحليل المبيعات',
      'new-order': 'طلب جديد', 'place-order': 'تأكيد الطلب',
      'add-product': 'إضافة منتج', 'edit-product': 'تعديل منتج',
      'delete-product': 'حذف المنتج', 'product-name': 'اسم المنتج',
      'product-name-ar': 'اسم المنتج (عربي)', 'cost-price': 'سعر التكلفة',
      'selling-price': 'سعر البيع', available: 'متاح', unavailable: 'غير متاح',
      'track-stock': 'تتبع المخزون', 'stock-qty': 'الكمية في المخزون',
      'min-stock': 'حد التنبيه', 'order-type': 'نوع الطلب',
      'table-no': 'رقم الطاولة', 'customer-name': 'اسم العميل',
      'customer-phone': 'هاتف العميل', notes: 'ملاحظات',
      'amount-paid': 'المبلغ المدفوع', change: 'الباقي',
      'print-invoice': 'طباعة الفاتورة', 'shift-management': 'إدارة الوردية',
      'open-shift': 'فتح وردية', 'close-shift': 'إغلاق وردية',
      'opening-cash': 'رأس المال', 'closing-cash': 'النقدية الختامية',
      invoice: 'فاتورة', receipt: 'إيصال', 'thank-you': 'شكراً لزيارتكم',
      role: 'الدور', admin: 'مدير النظام', manager: 'مدير',
      waiter: 'نادل', 'active-users': 'المستخدمون النشطون',
      'revenue-growth': 'نمو الإيرادات', 'avg-order': 'متوسط الطلب',
      'payment-method': 'طريقة الدفع', 'order-number': 'رقم الطلب',
      'created-at': 'تاريخ الإنشاء', quantity: 'الكمية', unit: 'الوحدة',
      address: 'العنوان', phone: 'الهاتف', 'org-name': 'اسم المؤسسة',
      'tax-rate': 'نسبة الضريبة', currency: 'العملة',
      'add-expense': 'إضافة مصروف', amount: 'المبلغ', date: 'التاريخ',
      description: 'الوصف', title: 'العنوان', 'expense-category': 'فئة المصروف',
      profit: 'الربح', 'profit-margin': 'هامش الربح',
      barcode: 'الباركود', image: 'الصورة'
    },
    en: {
      dashboard: 'Dashboard', products: 'Products', categories: 'Categories',
      orders: 'Orders', users: 'Users', tables: 'Tables',
      expenses: 'Expenses', inventory: 'Inventory', reports: 'Reports',
      settings: 'Settings', cashier: 'Cashier', kitchen: 'Kitchen',
      logout: 'Logout', search: 'Search...', add: 'Add',
      edit: 'Edit', delete: 'Delete', save: 'Save', cancel: 'Cancel',
      confirm: 'Confirm', yes: 'Yes', no: 'No', name: 'Name',
      price: 'Price', category: 'Category', status: 'Status',
      actions: 'Actions', total: 'Total', subtotal: 'Subtotal',
      tax: 'Tax', discount: 'Discount', payment: 'Payment',
      cash: 'Cash', card: 'Card', digital: 'Digital',
      'dine-in': 'Dine In', takeaway: 'Take Away', delivery: 'Delivery',
      pending: 'Pending', preparing: 'Preparing', ready: 'Ready',
      completed: 'Completed', cancelled: 'Cancelled', today: 'Today',
      'this-month': 'This Month', 'today-sales': "Today's Sales",
      'total-orders': 'Total Orders', 'monthly-revenue': 'Monthly Revenue',
      'pending-orders': 'Pending Orders', 'low-stock': 'Low Stock',
      'top-products': 'Top Products', 'sales-analytics': 'Sales Analytics',
      'new-order': 'New Order', 'place-order': 'Place Order',
      'add-product': 'Add Product', 'edit-product': 'Edit Product',
      'delete-product': 'Delete Product', 'product-name': 'Product Name',
      'product-name-ar': 'Product Name (Arabic)', 'cost-price': 'Cost Price',
      'selling-price': 'Selling Price', available: 'Available', unavailable: 'Unavailable',
      'track-stock': 'Track Stock', 'stock-qty': 'Stock Quantity',
      'min-stock': 'Min Stock Alert', 'order-type': 'Order Type',
      'table-no': 'Table No.', 'customer-name': 'Customer Name',
      'customer-phone': 'Customer Phone', notes: 'Notes',
      'amount-paid': 'Amount Paid', change: 'Change',
      'print-invoice': 'Print Invoice', 'shift-management': 'Shift Management',
      'open-shift': 'Open Shift', 'close-shift': 'Close Shift',
      'opening-cash': 'Opening Cash', 'closing-cash': 'Closing Cash',
      invoice: 'Invoice', receipt: 'Receipt', 'thank-you': 'Thank you for your visit',
      role: 'Role', admin: 'Admin', manager: 'Manager',
      waiter: 'Waiter', 'active-users': 'Active Users',
      'revenue-growth': 'Revenue Growth', 'avg-order': 'Avg Order Value',
      'payment-method': 'Payment Method', 'order-number': 'Order Number',
      'created-at': 'Created At', quantity: 'Quantity', unit: 'Unit',
      address: 'Address', phone: 'Phone', 'org-name': 'Organization Name',
      'tax-rate': 'Tax Rate', currency: 'Currency',
      'add-expense': 'Add Expense', amount: 'Amount', date: 'Date',
      description: 'Description', title: 'Title', 'expense-category': 'Expense Category',
      profit: 'Profit', 'profit-margin': 'Profit Margin',
      barcode: 'Barcode', image: 'Image'
    }
  },

  init() {
    const saved = localStorage.getItem('erp-lang') || 'ar';
    this.apply(saved);
  },

  apply(lang) {
    document.documentElement.setAttribute('lang', lang);
    document.documentElement.setAttribute('dir', lang === 'ar' ? 'rtl' : 'ltr');
    localStorage.setItem('erp-lang', lang);
    const btn = document.getElementById('langToggle');
    if (btn) btn.textContent = lang === 'ar' ? 'EN' : 'عربي';
    this.translatePage(lang);
  },

  toggle() {
    const current = localStorage.getItem('erp-lang') || 'ar';
    this.apply(current === 'ar' ? 'en' : 'ar');
  },

  t(key) {
    const lang = localStorage.getItem('erp-lang') || 'ar';
    return this.translations[lang]?.[key] || this.translations['en']?.[key] || key;
  },

  translatePage(lang) {
    document.querySelectorAll('[data-t]').forEach(el => {
      const key = el.getAttribute('data-t');
      const translation = this.translations[lang]?.[key];
      if (translation) {
        if (el.tagName === 'INPUT' && el.getAttribute('placeholder')) {
          el.placeholder = translation;
        } else {
          el.textContent = translation;
        }
      }
    });
    document.querySelectorAll('[data-t-placeholder]').forEach(el => {
      const key = el.getAttribute('data-t-placeholder');
      const translation = this.translations[lang]?.[key];
      if (translation) el.placeholder = translation;
    });
    // Show/hide language-specific content
    document.querySelectorAll('[data-lang]').forEach(el => {
      el.style.display = el.getAttribute('data-lang') === lang ? '' : 'none';
    });
  }
};

// ===== TOAST MANAGER =====
const Toast = {
  container: null,
  init() {
    this.container = document.getElementById('toastContainer');
    if (!this.container) {
      this.container = document.createElement('div');
      this.container.id = 'toastContainer';
      this.container.className = 'toast-container';
      document.body.appendChild(this.container);
    }
  },
  show(message, type = 'success', duration = 3500) {
    this.init();
    const icons = { success: '✅', error: '❌', warning: '⚠️', info: 'ℹ️' };
    const toast = document.createElement('div');
    toast.className = `toast ${type !== 'success' ? type : ''}`;
    toast.innerHTML = `<span>${icons[type] || icons.success}</span><span>${message}</span>`;
    this.container.appendChild(toast);
    setTimeout(() => {
      toast.style.opacity = '0';
      toast.style.transform = 'translateX(100px)';
      toast.style.transition = '0.3s ease';
      setTimeout(() => toast.remove(), 300);
    }, duration);
  },
  success: (msg) => Toast.show(msg, 'success'),
  error: (msg) => Toast.show(msg, 'error'),
  warning: (msg) => Toast.show(msg, 'warning'),
  info: (msg) => Toast.show(msg, 'info'),
};

// ===== API HELPERS =====
const API = {
  token: document.querySelector('meta[name="__RequestVerificationToken"]')?.content,

  async get(url) {
    const res = await fetch(url, { headers: { 'X-Requested-With': 'XMLHttpRequest' } });
    return res.json();
  },

  async post(url, data) {
    const res = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'RequestVerificationToken': this.token || '',
        'X-Requested-With': 'XMLHttpRequest'
      },
      body: JSON.stringify(data)
    });
    return res.json();
  },

  async postForm(url, formData) {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'RequestVerificationToken': this.token || '' },
      body: formData
    });
    return res.json();
  }
};

// ===== MODAL MANAGER =====
const Modal = {
  open(id) {
    const overlay = document.getElementById(id);
    if (overlay) {
      overlay.classList.add('open');
      document.body.style.overflow = 'hidden';
    }
  },
  close(id) {
    const overlay = document.getElementById(id);
    if (overlay) {
      overlay.classList.remove('open');
      document.body.style.overflow = '';
    }
  },
  closeAll() {
    document.querySelectorAll('.modal-overlay.open').forEach(m => {
      m.classList.remove('open');
    });
    document.body.style.overflow = '';
  }
};

// Close modals on overlay click
document.addEventListener('click', (e) => {
  if (e.target.classList.contains('modal-overlay')) Modal.closeAll();
});

// ===== NUMBER FORMATTER =====
const Formatter = {
  currency(val, currency = 'EGP') {
    const lang = localStorage.getItem('erp-lang') || 'ar';
    const symbol = lang === 'ar' ? 'ج.م' : currency;
    return `${symbol} ${Number(val).toFixed(2)}`;
  },
  number(val) {
    return Number(val).toLocaleString();
  },
  percent(val) {
    return `${val > 0 ? '+' : ''}${Number(val).toFixed(1)}%`;
  },
  date(d, includeTime = true) {
    const date = new Date(d);
    const lang = localStorage.getItem('erp-lang') || 'ar';
    const opts = { year: 'numeric', month: 'short', day: 'numeric' };
    if (includeTime) { opts.hour = '2-digit'; opts.minute = '2-digit'; }
    return date.toLocaleDateString(lang === 'ar' ? 'ar-EG' : 'en-US', opts);
  }
};

// ===== CONFIRM DIALOG =====
function confirmAction(message, callback) {
  const lang = localStorage.getItem('erp-lang') || 'ar';
  const isAr = lang === 'ar';
  if (confirm(message)) callback();
}

// ===== LOADING =====
const Loading = {
  show() {
    let overlay = document.getElementById('loadingOverlay');
    if (!overlay) {
      overlay = document.createElement('div');
      overlay.id = 'loadingOverlay';
      overlay.className = 'loading-overlay';
      overlay.innerHTML = '<div class="spinner"></div>';
      document.body.appendChild(overlay);
    }
    overlay.style.display = 'flex';
  },
  hide() {
    const overlay = document.getElementById('loadingOverlay');
    if (overlay) overlay.style.display = 'none';
  }
};

// ===== SIDEBAR TOGGLE =====
function toggleSidebar() {
  const sidebar = document.querySelector('.sidebar');
  if (sidebar) sidebar.classList.toggle('open');
}

// ===== PRINT FUNCTIONS =====
function printInvoice(invoiceHtml) {
  const win = window.open('', '_blank', 'width=400,height=700');
  win.document.write(`<!DOCTYPE html>
<html dir="${localStorage.getItem('erp-lang') === 'ar' ? 'rtl' : 'ltr'}" lang="${localStorage.getItem('erp-lang') || 'ar'}">
<head>
<meta charset="UTF-8">
<title>Invoice - فاتورة</title>
<link href="/css/fonts.css" rel="stylesheet">
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body { font-family: 'Cairo', 'Segoe UI', Tahoma, monospace; font-size: 13px; color: #000; background: #fff; padding: 10px; }
  .inv-center { text-align: center; }
  .inv-org { font-size: 20px; font-weight: 900; color: #1a3a5c; margin-bottom: 4px; }
  .inv-sub { font-size: 11px; color: #555; }
  hr { border: none; border-top: 1px dashed #000; margin: 8px 0; }
  .inv-row { display: flex; justify-content: space-between; padding: 2px 0; font-size: 12px; }
  .inv-bold { font-weight: 700; }
  .inv-total { font-size: 16px; font-weight: 900; color: #1a3a5c; }
  .inv-items table { width: 100%; border-collapse: collapse; }
  .inv-items th, .inv-items td { padding: 4px 6px; text-align: right; font-size: 12px; }
  .inv-items th { border-bottom: 1px solid #000; font-weight: 700; }
  .inv-footer { text-align: center; font-size: 11px; color: #555; margin-top: 10px; }
  @media print { @page { margin: 5mm; size: 80mm auto; } }
</style>
</head>
<body>
${invoiceHtml}
<script>window.onload = () => { window.print(); setTimeout(() => window.close(), 500); }</script>
</body></html>`);
  win.document.close();
}

// ===== CHART HELPER =====
function createChart(canvasId, config) {
  const canvas = document.getElementById(canvasId);
  if (!canvas) return null;
  if (canvas._chart) canvas._chart.destroy();
  const isDark = document.documentElement.getAttribute('data-theme') === 'dark';
  const gridColor = isDark ? 'rgba(255,255,255,0.05)' : 'rgba(0,0,0,0.05)';
  const textColor = isDark ? '#94a3b8' : '#64748b';

  if (config.options?.scales) {
    Object.values(config.options.scales).forEach(scale => {
      scale.ticks = scale.ticks || {};
      scale.ticks.color = textColor;
      scale.grid = scale.grid || {};
      scale.grid.color = gridColor;
    });
  }
  if (config.options?.plugins?.legend?.labels) {
    config.options.plugins.legend.labels.color = textColor;
  }

  const chart = new Chart(canvas, config);
  canvas._chart = chart;
  return chart;
}

// ===== INIT =====
document.addEventListener('DOMContentLoaded', () => {
  ThemeManager.init();
  LangManager.init();
  Toast.init();

  document.getElementById('themeToggle')?.addEventListener('click', () => ThemeManager.toggle());
  document.getElementById('langToggle')?.addEventListener('click', () => LangManager.toggle());

  // Auto-dismiss alerts
  document.querySelectorAll('.alert').forEach(alert => {
    setTimeout(() => {
      alert.style.opacity = '0';
      alert.style.transition = '0.3s';
      setTimeout(() => alert.remove(), 300);
    }, 4000);
  });

  // Active nav links
  const currentPath = window.location.pathname.toLowerCase();
  document.querySelectorAll('.sidebar-link').forEach(link => {
    if (link.getAttribute('href')?.toLowerCase() === currentPath) {
      link.classList.add('active');
    }
  });
});

/* ═══════════════════════════════════════════════════════════════
   Client-side table pagination (10 rows/page)
   Usage:  initPager('myTable', 'myPager')
   Rows carrying data-empty="1" are placeholders and never counted.
   ═══════════════════════════════════════════════════════════════ */
const PAGE_SIZE = 10;
const pagers = {};

function initPager(tableId, pagerId) {
  if (!document.getElementById(tableId)) return;
  pagers[tableId] = { page: 1, filter: '', pagerId };
  renderPage(tableId);
}

function matchingRows(tableId) {
  const term = (pagers[tableId]?.filter || '').toLowerCase();
  return [...document.querySelectorAll('#' + tableId + ' tbody tr')]
    .filter(r => !r.dataset.empty && (!term || r.textContent.toLowerCase().includes(term)));
}

function renderPage(tableId) {
  const st = pagers[tableId];
  if (!st) return;
  const rows = matchingRows(tableId);
  const pages = Math.max(1, Math.ceil(rows.length / PAGE_SIZE));
  if (st.page > pages) st.page = pages;

  document.querySelectorAll('#' + tableId + ' tbody tr').forEach(r => r.style.display = 'none');
  const start = (st.page - 1) * PAGE_SIZE;
  rows.slice(start, start + PAGE_SIZE).forEach(r => r.style.display = '');

  const pager = document.getElementById(st.pagerId);
  if (!pager) return;

  if (rows.length === 0) {
    // keep the placeholder row visible so the table isn't blank
    const empty = document.querySelector('#' + tableId + ' tbody tr[data-empty]');
    if (empty) empty.style.display = '';
    pager.innerHTML = '';
    return;
  }

  let html = `<button onclick="goPage('${tableId}',${st.page - 1})" ${st.page === 1 ? 'disabled' : ''}>‹</button>`;
  const from = Math.max(1, st.page - 2), to = Math.min(pages, from + 4);
  for (let i = from; i <= to; i++) {
    html += `<button class="${i === st.page ? 'active' : ''}" onclick="goPage('${tableId}',${i})">${i}</button>`;
  }
  html += `<button onclick="goPage('${tableId}',${st.page + 1})" ${st.page === pages ? 'disabled' : ''}>›</button>`;
  html += `<span class="pager-info">${start + 1}–${Math.min(start + PAGE_SIZE, rows.length)} من ${rows.length}</span>`;
  pager.innerHTML = html;
}

function goPage(tableId, page) {
  const st = pagers[tableId];
  if (!st) return;
  const pages = Math.max(1, Math.ceil(matchingRows(tableId).length / PAGE_SIZE));
  st.page = Math.min(Math.max(1, page), pages);
  renderPage(tableId);
}

function filterPagedTable(tableId, term) {
  if (!pagers[tableId]) return;
  pagers[tableId].filter = term;
  pagers[tableId].page = 1;   // a new search always restarts at page 1
  renderPage(tableId);
}
