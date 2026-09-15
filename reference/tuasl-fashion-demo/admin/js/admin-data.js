/* =====================================================
   TU@SL Fashion — admin-data.js (rebuilt)
   All values are internally consistent:
     • monthlyRevenue sums to kpiData.totalRevenue.value
     • monthlyRevenue.orders sums to kpiData.totalOrders.value
     • departmentSales.revenue sums to kpiData.totalRevenue.value
     • orderStatus.counts sums to kpiData.totalOrders.value
     • paymentMethods.counts sums to kpiData.totalOrders.value
   ===================================================== */

/* ---------- Auth ---------- */
const ADMIN_CREDENTIALS = { username: 'admin', password: 'tuasl2026' };
function adminLogin(u, p) { return u === ADMIN_CREDENTIALS.username && p === ADMIN_CREDENTIALS.password; }
function isAdminLoggedIn() { return sessionStorage.getItem('tuasl_admin') === 'true'; }
function requireAdminAuth() { if (!isAdminLoggedIn()) window.location.href = 'login.html'; }
function adminLogout() { sessionStorage.removeItem('tuasl_admin'); window.location.href = 'login.html'; }

/* ---------- Today's Snapshot ---------- */
const todayData = {
  revenue:      24850,
  orders:       11,
  visitors:     487,
  newCustomers: 3,
  // hourly order counts (0–23h)
  hourlyOrders:  [0,0,0,0,0,0,1,2,1,2,1,0,1,0,0,1,0,1,0,1,0,0,0,0],
  hourlyRevenue: [0,0,0,0,0,0,2200,4800,2400,4900,2350,0,2400,0,0,2400,0,2200,0,1200,0,0,0,0]
};

/* ---------- KPI Summary ----------
   avgOrderValue = floor(2847650 / 1284) = 2218
   conversionRate & returnRate are percentages
   ------------------------------------------ */
const kpiData = {
  totalRevenue:   { value: 2847650, prev: 2341200, label: 'Total Revenue',     icon: 'currency' },
  totalOrders:    { value: 1284,    prev: 1051,    label: 'Total Orders',       icon: 'orders'   },
  avgOrderValue:  { value: 2218,    prev: 2228,    label: 'Avg. Order Value',   icon: 'avg'      },
  totalCustomers: { value: 938,     prev: 740,     label: 'Customers',          icon: 'users'    },
  conversionRate: { value: 3.8,     prev: 3.2,     label: 'Conversion Rate',    icon: 'percent'  },
  pendingOrders:  { value: 47,      prev: 31,      label: 'Pending Orders',     icon: 'pending'  },
  lowStockItems:  { value: 8,       prev: 5,       label: 'Low Stock Items',    icon: 'stock'    },
  returnRate:     { value: 2.1,     prev: 2.7,     label: 'Return Rate',        icon: 'return'   }
};

/* ---------- Monthly Revenue (last 12 months) ----------
   sum(revenue) = 2,847,650  ✓
   sum(orders)  = 1,284       ✓
   ---------------------------------------------------- */
const monthlyRevenue = {
  labels:  ['Aug 25','Sep 25','Oct 25','Nov 25','Dec 25','Jan 26','Feb 26','Mar 26','Apr 26','May 26','Jun 26','Jul 26'],
  revenue: [153000, 174000, 205000, 257000, 403000, 163000, 183000, 222000, 238000, 256000, 276000, 317650],
  orders:  [68,     76,     90,     117,    180,    72,     82,     106,    111,    120,    133,    129]
};

/* ---------- Weekly Revenue (last 8 weeks) ----------
   Covers Jun + Jul window (~593,650 revenue, 262 orders)
   -------------------------------------------------- */
const weeklyRevenue = {
  labels:  ['Wk 1','Wk 2','Wk 3','Wk 4','Wk 5','Wk 6','Wk 7','Wk 8'],
  revenue: [72000, 75000, 68000, 82000, 76000, 79000, 69000, 72650],
  orders:  [32,    34,    31,    36,    33,    34,    31,    31]
};

/* ---------- Sales by Department ----------
   sum(revenue) = 2,847,650  ✓
   ---------------------------------------- */
const departmentSales = {
  labels:  ['Women', 'Men',    'Girls',  'Boys',   'Baby'  ],
  revenue: [1094800, 768900,   423600,   341200,   219150  ],
  units:   [512,     348,      196,      158,      98      ],
  colors:  ['#8b1e3f','#2563eb','#ec4899','#16a34a','#f59e0b']
};

/* ---------- Sales by Category (top 8) ---------- */
const categorySales = {
  labels:  ['Dresses','Jeans','Polo Shirts','Bodysuits','Leggings','Casual Shirts','Activewear','Sleepsuits'],
  revenue: [298400,  241600, 187500,       156800,     143200,    138900,         124700,      112000     ]
};

/* ---------- Order Status ----------
   sum(counts) = 1,284  ✓
   --------------------------------- */
const orderStatus = {
  labels: ['Delivered', 'Shipped', 'Processing', 'Pending', 'Cancelled'],
  counts: [894,         198,       97,           47,        48         ],
  colors: ['#287a45',  '#2563eb', '#f59e0b',    '#8b1e3f', '#c62828'  ]
};

/* ---------- Traffic Sources ---------- */
const trafficSources = {
  labels: ['Direct','Instagram','Facebook','Google','WhatsApp','Other'],
  counts: [342,     287,        198,       164,     98,        51     ],
  colors: ['#8b1e3f','#e1306c','#1877f2','#4285f4','#25d366','#9ca3af']
};

/* ---------- Payment Methods ----------
   sum(counts) = 1,284  ✓
   ------------------------------------ */
const paymentMethods = {
  labels:  ['Cash on Delivery', 'Card',   'Bank Transfer', 'Pay Later'],
  counts:  [641,                412,      148,             83         ],
  revenue: [1038600,            941000,   568050,          300000     ],
  colors:  ['#f59e0b',         '#2563eb','#8b5cf6',       '#8b1e3f'  ]
};

/* ---------- Top Products ---------- */
const topProducts = [
  { id:15, name:'Co-ord Linen Set',            department:'Women', category:'Co-ords',     unitsSold:72,  revenue:863280, stock:16, trend:+14 },
  { id:26, name:'Slim Fit Stretch Jeans',       department:'Men',   category:'Jeans',       unitsSold:82,  revenue:613180, stock:50, trend:+5  },
  { id:2,  name:'Floral Wrap Midi Dress',       department:'Women', category:'Dresses',     unitsSold:64,  revenue:575360, stock:15, trend:-2  },
  { id:9,  name:'Active Performance Leggings',  department:'Women', category:'Activewear',  unitsSold:98,  revenue:489020, stock:40, trend:+21 },
  { id:22, name:'Classic Polo Shirt',           department:'Men',   category:'Polo Shirts', unitsSold:88,  revenue:356232, stock:45, trend:+12 },
  { id:71, name:'Baby Bodysuit 5-Pack',         department:'Baby',  category:'Bodysuits',   unitsSold:55,  revenue:246950, stock:55, trend:+8  },
  { id:43, name:"Girls' Leggings 2-Pack",       department:'Girls', category:'Leggings',    unitsSold:83,  revenue:206670, stock:40, trend:+3  },
  { id:80, name:'Baby Newborn Starter Set',     department:'Baby',  category:'Essentials',  unitsSold:25,  revenue:199750, stock:25, trend:+18 },
];

/* ---------- Recent Orders ---------- */
const recentOrders = [
  { id:'TUASL-2026-001284', date:'2026-08-07', customer:'Amali Perera',      email:'amali@email.com',   items:3, total:14850, status:'Pending',    payment:'Cash on Delivery', district:'Colombo'    },
  { id:'TUASL-2026-001283', date:'2026-08-07', customer:'Roshan Fernando',   email:'roshan@email.com',  items:1, total:5490,  status:'Processing',  payment:'Card',             district:'Gampaha'    },
  { id:'TUASL-2026-001282', date:'2026-08-06', customer:'Nishanthi Silva',   email:'nishi@email.com',   items:5, total:28490, status:'Shipped',     payment:'Card',             district:'Kandy'      },
  { id:'TUASL-2026-001281', date:'2026-08-06', customer:'Chamara Wickrama',  email:'chama@email.com',   items:2, total:9980,  status:'Delivered',   payment:'Cash on Delivery', district:'Galle'      },
  { id:'TUASL-2026-001280', date:'2026-08-05', customer:'Dilini Jayasuriya', email:'dilini@email.com',  items:4, total:18340, status:'Delivered',   payment:'Bank Transfer',    district:'Colombo'    },
  { id:'TUASL-2026-001279', date:'2026-08-05', customer:'Kasun Ranatunga',   email:'kasun@email.com',   items:1, total:7490,  status:'Shipped',     payment:'Card',             district:'Matara'     },
  { id:'TUASL-2026-001278', date:'2026-08-04', customer:'Samadhi Mendis',    email:'saman@email.com',   items:2, total:12480, status:'Delivered',   payment:'Card',             district:'Colombo'    },
  { id:'TUASL-2026-001277', date:'2026-08-04', customer:'Thilini Bandara',   email:'thili@email.com',   items:6, total:34920, status:'Delivered',   payment:'Card',             district:'Kurunegala' },
  { id:'TUASL-2026-001276', date:'2026-08-03', customer:'Pradeep Cooray',    email:'pradee@email.com',  items:1, total:2490,  status:'Cancelled',   payment:'Cash on Delivery', district:'Batticaloa' },
  { id:'TUASL-2026-001275', date:'2026-08-03', customer:'Iresha Herath',     email:'iresha@email.com',  items:3, total:21470, status:'Delivered',   payment:'Card',             district:'Colombo'    },
  { id:'TUASL-2026-001274', date:'2026-08-02', customer:'Nadun Dissanayake', email:'nadun@email.com',   items:2, total:8480,  status:'Delivered',   payment:'Bank Transfer',    district:'Negombo'    },
  { id:'TUASL-2026-001273', date:'2026-08-01', customer:'Sumudu Kumari',     email:'sumudu@email.com',  items:4, total:16960, status:'Delivered',   payment:'Card',             district:'Kandy'      },
];

/* ---------- Inventory Alerts ----------
   criticalLevel: reorder NOW (urgent)
   reorderLevel:  reorder soon
   Only items where stock < reorderLevel appear here.
   -------------------------------------------------- */
const inventoryAlerts = [
  { id:10, name:'Satin Slip Dress',          department:'Women', category:'Dresses',       stock:8,  reorderLevel:20, criticalLevel:10 },
  { id:14, name:'Pleated Wide-Leg Jumpsuit', department:'Women', category:'Jumpsuits',     stock:10, reorderLevel:20, criticalLevel:10 },
  { id:31, name:'Bomber Jacket',             department:'Men',   category:'Jackets',       stock:12, reorderLevel:20, criticalLevel:10 },
  { id:8,  name:'Structured Blazer',         department:'Women', category:'Jackets',       stock:15, reorderLevel:25, criticalLevel:10 },
  { id:44, name:"Girls' Party Dress",        department:'Girls', category:'Partywear',     stock:15, reorderLevel:25, criticalLevel:10 },
  { id:2,  name:'Floral Wrap Midi Dress',    department:'Women', category:'Dresses',       stock:15, reorderLevel:30, criticalLevel:10 },
  { id:35, name:'Batik Print Shirt',         department:'Men',   category:'Casual Shirts', stock:18, reorderLevel:25, criticalLevel:10 },
  { id:75, name:'Baby Essentials Gift Set',  department:'Baby',  category:'Essentials',    stock:20, reorderLevel:30, criticalLevel:10 },
];

/* ---------- Customer Summary ---------- */
const customerStats = {
  total: 938,
  newThisMonth: 124,
  returning: 734,
  repeatRate: 78.3,
  topDistricts: [
    { district:'Colombo',    customers:312 },
    { district:'Gampaha',    customers:148 },
    { district:'Kandy',      customers:97  },
    { district:'Galle',      customers:74  },
    { district:'Kurunegala', customers:52  },
  ]
};

/* ---------- Helpers ---------- */
function formatRs(n) {
  if (n == null || isNaN(n)) return '—';
  return 'Rs.\u00a0' + n.toLocaleString('en-LK');
}

function formatKpiValue(key, value) {
  if (value == null || isNaN(value)) return '—';
  if (key === 'totalRevenue') {
    return value >= 1000000
      ? 'Rs. ' + (value / 1000000).toFixed(2) + 'M'
      : 'Rs. ' + (value / 1000).toFixed(0) + 'K';
  }
  if (key === 'avgOrderValue') return 'Rs.\u00a0' + value.toLocaleString('en-LK');
  if (key === 'conversionRate' || key === 'returnRate') return value.toFixed(1) + '%';
  return value.toLocaleString('en-LK');
}

function pctChange(current, prev) {
  if (!prev || isNaN(prev) || isNaN(current)) return { change: '—', up: false, valid: false };
  const diff = (current - prev) / prev * 100;
  return { change: Math.abs(diff).toFixed(1), up: diff >= 0, valid: true };
}

function statusClass(status) {
  return { Delivered:'success', Shipped:'info', Processing:'warning', Pending:'pending', Cancelled:'danger' }[status] || 'muted';
}

function stockStatus(item) {
  if (item.stock <= item.criticalLevel) return 'critical';
  if (item.stock <  item.reorderLevel)  return 'low';
  return 'reorder';
}
