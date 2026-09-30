/*
 * Progress page content.
 *   status   : "planned" | "in-progress" | "done"
 *   progress : 0–100, used when status is "in-progress"
 *              (Done always counts as 100, Planned always counts as 0)
 *   dates    : "YYYY-MM-DD"
 *   Text values go in "double quotes"; progress is a plain number (40, not "40").
 */
const progressData = {
  project: {
    title: "Spencer Denim — Website Build Progress",
    client: "Spencer Denim Industries Pvt Ltd",
    agency: "TU2SL",
    startDate: "2026-09-15",
    endDate: "2026-10-15"
  },

  lastUpdated: "2026-10-01",

  milestones: [
    { week: "Week 0", title: "Setup & design", start: "2026-09-15", end: "2026-09-18" },
    { week: "Week 1", title: "Foundation & admin catalogue", start: "2026-09-19", end: "2026-09-25" },
    { week: "Week 2", title: "Shopping website", start: "2026-09-26", end: "2026-10-02" },
    { week: "Week 3", title: "Checkout & orders", start: "2026-10-03", end: "2026-10-09" },
    { week: "Week 4", title: "Payments, dashboard & launch", start: "2026-10-10", end: "2026-10-15" }
  ],

  groups: [
    {
      name: "Project Planning & Design",
      features: [
        { name: "Requirements & scope finalised", status: "done", progress: 100 },
        { name: "Technical plan & architecture", status: "done", progress: 100 },
        { name: "Design style guide", status: "in-progress", progress: 90 },
        { name: "Hosting, domain & security setup", status: "in-progress", progress: 25 }
      ]
    },
    {
      name: "Homepage",
      features: [
        { name: "Hero video banner", status: "in-progress", progress: 70 },
        { name: "Shop by department (Women, Men, Kids)", status: "done", progress: 100 },
        { name: "New arrivals", status: "done", progress: 100 },
        { name: "Trending items", status: "in-progress", progress: 70 },
        { name: "Campaign banner", status: "in-progress", progress: 70 },
        { name: "Activewear feature", status: "in-progress", progress: 70 },
        { name: "Sale section", status: "done", progress: 100 },
        { name: "Social media gallery", status: "in-progress", progress: 70 },
        { name: "Customer benefits", status: "done", progress: 100 },
        { name: "Newsletter signup", status: "done", progress: 100 }
      ]
    },
    {
      name: "Shopping Pages",
      features: [
        { name: "Site header with announcement bar, footer, WhatsApp chat button & delivery district selector", status: "done", progress: 100 },
        { name: "Main menu (Women · Men · Kids · New Arrivals · Sale) with mega menu & mobile menu", status: "done", progress: 100 },
        { name: "Department pages", status: "done", progress: 100 },
        { name: "Category listings with filters & sorting", status: "done", progress: 100 },
        { name: "Product cards with quick view", status: "in-progress", progress: 80 },
        { name: "Search", status: "in-progress", progress: 40 },
        { name: "Store pages: About, Contact, Returns, Shipping, Privacy & Terms", status: "planned", progress: 0 }
      ]
    },
    {
      name: "Product Page",
      features: [
        { name: "Image gallery, zoom & video", status: "done", progress: 100 },
        { name: "Colour & size selection with size guide", status: "in-progress", progress: 80 },
        { name: "Product details & delivery information", status: "in-progress", progress: 60 },
        { name: "Related & recently viewed products", status: "done", progress: 100 },
        { name: "Reviews & ratings", status: "planned", progress: 0 }
      ]
    },
    {
      name: "Wishlist, Bag & Checkout",
      features: [
        { name: "Wishlist", status: "planned", progress: 0 },
        { name: "Shopping bag & mini bag", status: "planned", progress: 0 },
        { name: "Promo codes", status: "planned", progress: 0 },
        { name: "Checkout & guest checkout", status: "planned", progress: 0 },
        { name: "Delivery options", status: "planned", progress: 0 },
        { name: "Bank transfer payment", status: "planned", progress: 0 },
        { name: "PayPal payment", status: "planned", progress: 0 },
        { name: "Order confirmation page", status: "planned", progress: 0 }
      ]
    },
    {
      name: "Customer Account",
      features: [
        { name: "Register & login", status: "planned", progress: 0 },
        { name: "Order history with tracking", status: "planned", progress: 0 },
        { name: "Order & shipping emails", status: "planned", progress: 0 }
      ]
    },
    {
      name: "Admin Panel",
      features: [
        { name: "Admin login with user roles & permissions", status: "in-progress", progress: 80 },
        { name: "Product management (images & video)", status: "in-progress", progress: 50 },
        { name: "Category management", status: "in-progress", progress: 35 },
        { name: "Inventory", status: "in-progress", progress: 25 },
        { name: "Order management & payment approval", status: "planned", progress: 0 },
        { name: "Promo codes & sale campaigns", status: "planned", progress: 0 },
        { name: "Customers", status: "planned", progress: 0 },
        { name: "Review moderation", status: "planned", progress: 0 },
        { name: "Newsletter subscriber list", status: "planned", progress: 0 },
        { name: "Homepage & content management", status: "in-progress", progress: 35 },
        { name: "Dashboard", status: "planned", progress: 0 },
        { name: "Reports & traffic", status: "planned", progress: 0 },
        { name: "Settings", status: "planned", progress: 0 }
      ]
    },
    {
      name: "Launch",
      features: [
        { name: "Preview website for your review", status: "planned", progress: 0 },
        { name: "Mobile, tablet & desktop testing", status: "planned", progress: 0 },
        { name: "Website live on the domain with SSL", status: "planned", progress: 0 },
        { name: "Admin training & handover", status: "planned", progress: 0 }
      ]
    }
  ],

  // Newest first.
  updates: [
    {
      date: "2026-10-01",
      text: "Staff sign-in is now protected by two-step verification using an authenticator app on the phone, and a forced password change on first sign-in. Every action an administrator takes is recorded, and there is a screen to browse and filter that history — useful if you ever need to see who changed a price or approved a payment. Work continues on the photo and video library."
    },
    {
      date: "2026-09-29",
      text: "Admin sign-in security tightened: when a staff account is switched off it loses access immediately rather than staying signed in, and every change an administrator makes is now recorded against their name. The photo and video library is being built and tested — that is the part that lets you upload your own product pictures, the hero video and the campaign banners, and it is what the remaining picture-led home page sections are waiting for."
    },
    {
      date: "2026-09-24",
      text: "The home page and the product page are built. The home page now shows Shop by Department, New Arrivals, Trending, Sale, why-shop-with-us and newsletter sign-up, and every part of it is stored so you can edit it yourself later. The product page has the photo gallery with zoom and full screen, colour and size choice with prices per size, product details and related items. The picture-led blocks (hero video, campaign banner, activewear, social wall) are built and switched off until your photographs and video are uploaded."
    },
    {
      date: "2026-09-24",
      text: "Shopping pages are working. You can now open Women, Men or Kids, narrow the products by size, colour, price, material and more, sort them, and load more as you scroll. New Arrivals and Sale pages are live too. Product cards show the photo, a second photo on hover, the price with any discount, the rating and the available colours. A filtered page keeps its own web address, so you can share or bookmark it. Tested on phone, tablet and desktop, and with keyboard and screen-reader use."
    },
    {
      date: "2026-09-23",
      text: "The shop frame is built and tested: the top announcement bar, the header with your logo area, search, wishlist and bag, the main menu with drop-down categories on desktop and a slide-out menu on phones, the delivery district selector, the footer, and the WhatsApp chat button. Checked on phone, tablet and desktop sizes."
    },
    {
      date: "2026-09-22",
      text: "Secure admin sign-in is built and tested: staff accounts, roles and permissions, and an activity log that records every sign-in and sign-out. The remaining admin screens (managing staff users, viewing the activity log) come next. Work also continued on the product catalogue."
    },
    {
      date: "2026-09-18",
      text: "Development environment and project foundation completed and tested. The product catalogue is now built in the database: departments, categories, products, colours, sizes, stock and product images, with sample products loaded for testing. This is the groundwork behind the shop pages, the product page and the admin product screens, which are built next."
    },
    {
      date: "2026-09-17",
      text: "Hosting purchased. Development environment setup is under way; server security and domain configuration are next."
    },
    {
      date: "2026-09-16",
      text: "Project code repository set up and design decisions recorded for the build. A short list of items for your confirmation (logo, delivery fees and couriers, PayPal currency) will be shared with you."
    },
    {
      date: "2026-09-15",
      text: "Database design reviewed and finalised, covering products, stock, orders, payments, delivery zones, user roles and security."
    },
    {
      date: "2026-09-15",
      text: "Design style guide prepared (colours, fonts, buttons, menus, product cards, product page, shopping bag) and ready for your review. Database design drafted."
    },
    {
      date: "2026-09-15",
      text: "Project kick-off. Requirements and scope finalised from the agreed proposal. Technical plan prepared. Next: design style guide and project setup."
    }
  ]
};
