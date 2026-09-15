const products = [
  // ==================== WOMEN ====================
  {
    id: 1, name: "Relaxed Linen Blend Shirt", slug: "relaxed-linen-blend-shirt",
    department: "Women", category: "Shirts and Blouses",
    price: 5490, originalPrice: 6990, discount: 21, rating: 4.6, reviewCount: 38,
    colours: ["White", "Beige", "Sage"], sizes: ["XS","S","M","L","XL"],
    material: "Linen blend", fit: "Relaxed", occasion: "Casual", badge: "New", stock: 20,
    image: "https://images.pexels.com/photos/1536619/pexels-photo-1536619.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1462637/pexels-photo-1462637.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1536619/pexels-photo-1536619.jpeg?auto=compress&cs=tinysrgb&w=800","https://images.pexels.com/photos/1462637/pexels-photo-1462637.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A lightweight linen-blend shirt perfect for warm Sri Lankan days. Relaxed fit with a classic collar and easy button-down front.", tags: ["casual","summer","linen"]
  },
  {
    id: 2, name: "Floral Wrap Midi Dress", slug: "floral-wrap-midi-dress",
    department: "Women", category: "Dresses",
    price: 8990, originalPrice: 11500, discount: 22, rating: 4.8, reviewCount: 64,
    colours: ["Floral Blue", "Floral Red"], sizes: ["XS","S","M","L","XL"],
    material: "Viscose", fit: "Regular", occasion: "Casual", badge: "Best Seller", stock: 15,
    image: "https://images.pexels.com/photos/2220316/pexels-photo-2220316.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1375736/pexels-photo-1375736.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/2220316/pexels-photo-2220316.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A feminine wrap midi dress featuring a vibrant floral print. The adjustable tie-waist creates a flattering silhouette for any occasion.", tags: ["dress","floral","midi","summer"]
  },
  {
    id: 3, name: "High-Rise Slim Fit Jeans", slug: "high-rise-slim-fit-jeans",
    department: "Women", category: "Jeans",
    price: 7490, originalPrice: 7490, discount: 0, rating: 4.5, reviewCount: 52,
    colours: ["Dark Blue", "Light Blue", "Black"], sizes: ["26","28","30","32","34"],
    material: "Denim", fit: "Slim", occasion: "Casual", badge: "New", stock: 30,
    image: "https://images.pexels.com/photos/1040945/pexels-photo-1040945.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1055691/pexels-photo-1055691.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1040945/pexels-photo-1040945.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Modern high-rise slim fit jeans crafted from durable stretch denim. Comfortable all-day wear with a sleek, tailored look.", tags: ["jeans","denim","casual"]
  },
  {
    id: 4, name: "Printed Cotton Kurti", slug: "printed-cotton-kurti",
    department: "Women", category: "Traditional Wear",
    price: 4990, originalPrice: 6500, discount: 23, rating: 4.7, reviewCount: 89,
    colours: ["Red", "Navy", "Green"], sizes: ["S","M","L","XL","XXL"],
    material: "Cotton", fit: "Regular", occasion: "Traditional", badge: "Sale", stock: 25,
    image: "https://images.pexels.com/photos/1126993/pexels-photo-1126993.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/3622608/pexels-photo-3622608.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1126993/pexels-photo-1126993.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A beautifully printed cotton kurti with intricate block-print patterns. Breathable fabric ideal for daily wear and festive occasions.", tags: ["kurti","traditional","cotton"]
  },
  {
    id: 5, name: "Essential Crew-Neck T-Shirt", slug: "essential-crew-neck-tshirt-women",
    department: "Women", category: "Tops and T-Shirts",
    price: 2490, originalPrice: 2490, discount: 0, rating: 4.3, reviewCount: 120,
    colours: ["White", "Black", "Navy", "Pink", "Grey"], sizes: ["XS","S","M","L","XL"],
    material: "100% Cotton", fit: "Regular", occasion: "Casual", badge: null, stock: 50,
    image: "https://images.pexels.com/photos/3622615/pexels-photo-3622615.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1769676/pexels-photo-1769676.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/3622615/pexels-photo-3622615.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A wardrobe staple crew-neck T-shirt in soft 100% cotton. Available in a range of everyday colours, cut for a comfortable regular fit.", tags: ["tshirt","basic","cotton","casual"]
  },
  {
    id: 6, name: "Wide-Leg Palazzo Trousers", slug: "wide-leg-palazzo-trousers",
    department: "Women", category: "Trousers",
    price: 5990, originalPrice: 7990, discount: 25, rating: 4.4, reviewCount: 31,
    colours: ["Black", "Beige", "Dusty Rose"], sizes: ["XS","S","M","L","XL"],
    material: "Polyester blend", fit: "Wide Leg", occasion: "Smart Casual", badge: "Sale", stock: 18,
    image: "https://images.pexels.com/photos/2613260/pexels-photo-2613260.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1036623/pexels-photo-1036623.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/2613260/pexels-photo-2613260.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Elegant wide-leg palazzo trousers with a flowy silhouette. Pairs beautifully with fitted tops or relaxed blouses.", tags: ["trousers","palazzo","wide-leg"]
  },
  {
    id: 7, name: "Ruffle-Hem Mini Skirt", slug: "ruffle-hem-mini-skirt",
    department: "Women", category: "Skirts",
    price: 3990, originalPrice: 5490, discount: 27, rating: 4.5, reviewCount: 27,
    colours: ["White", "Pink", "Yellow"], sizes: ["XS","S","M","L"],
    material: "Cotton blend", fit: "Regular", occasion: "Casual", badge: "Sale", stock: 12,
    image: "https://images.pexels.com/photos/1055691/pexels-photo-1055691.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/2220316/pexels-photo-2220316.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1055691/pexels-photo-1055691.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A playful ruffle-hem mini skirt that brings a feminine touch to any summer outfit. Light and breezy for warm-weather days.", tags: ["skirt","mini","ruffle","summer"]
  },
  {
    id: 8, name: "Structured Blazer", slug: "structured-blazer-women",
    department: "Women", category: "Jackets",
    price: 10990, originalPrice: 10990, discount: 0, rating: 4.7, reviewCount: 43,
    colours: ["Black", "Camel", "Navy"], sizes: ["XS","S","M","L","XL"],
    material: "Polyester blend", fit: "Tailored", occasion: "Work", badge: "New", stock: 15,
    image: "https://images.pexels.com/photos/2234266/pexels-photo-2234266.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1036623/pexels-photo-1036623.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/2234266/pexels-photo-2234266.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A sharp structured blazer for the modern professional. Single-button closure with notched lapels and a clean tailored finish.", tags: ["blazer","work","formal","jacket"]
  },
  {
    id: 9, name: "Active Performance Leggings", slug: "active-performance-leggings",
    department: "Women", category: "Activewear",
    price: 4990, originalPrice: 6490, discount: 23, rating: 4.8, reviewCount: 98,
    colours: ["Black", "Grey", "Cobalt Blue"], sizes: ["XS","S","M","L","XL"],
    material: "Nylon-spandex", fit: "Compression", occasion: "Sport", badge: "Best Seller", stock: 40,
    image: "https://images.pexels.com/photos/2905238/pexels-photo-2905238.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1462637/pexels-photo-1462637.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/2905238/pexels-photo-2905238.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "High-performance leggings with four-way stretch and moisture-wicking fabric. Ideal for yoga, gym, and everyday active use.", tags: ["activewear","leggings","sport","gym"]
  },
  {
    id: 10, name: "Satin Slip Dress", slug: "satin-slip-dress",
    department: "Women", category: "Dresses",
    price: 7990, originalPrice: 9990, discount: 20, rating: 4.6, reviewCount: 56,
    colours: ["Champagne", "Black", "Dusty Mauve"], sizes: ["XS","S","M","L"],
    material: "Satin", fit: "Regular", occasion: "Evening", badge: "Online Exclusive", stock: 8,
    image: "https://images.pexels.com/photos/1375736/pexels-photo-1375736.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1536619/pexels-photo-1536619.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1375736/pexels-photo-1375736.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A luxurious satin slip dress with a bias cut for an effortlessly elegant silhouette. Perfect for evenings and special occasions.", tags: ["dress","satin","evening","occasion"]
  },
  {
    id: 11, name: "Broderie Anglaise Blouse", slug: "broderie-anglaise-blouse",
    department: "Women", category: "Shirts and Blouses",
    price: 4490, originalPrice: 5990, discount: 25, rating: 4.4, reviewCount: 29,
    colours: ["White", "Off White"], sizes: ["XS","S","M","L","XL"],
    material: "Cotton", fit: "Relaxed", occasion: "Casual", badge: "Sale", stock: 20,
    image: "https://images.pexels.com/photos/1769676/pexels-photo-1769676.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1126993/pexels-photo-1126993.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1769676/pexels-photo-1769676.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A charming broderie anglaise blouse with delicate embroidery detail. Lightweight and airy for the Sri Lankan climate.", tags: ["blouse","embroidery","casual","summer"]
  },
  {
    id: 12, name: "Ribbed Knit Cardigan", slug: "ribbed-knit-cardigan",
    department: "Women", category: "Knitwear",
    price: 6490, originalPrice: 8990, discount: 28, rating: 4.5, reviewCount: 41,
    colours: ["Cream", "Sage", "Terracotta"], sizes: ["XS","S","M","L","XL"],
    material: "Acrylic blend", fit: "Oversized", occasion: "Casual", badge: "Sale", stock: 22,
    image: "https://images.pexels.com/photos/3622608/pexels-photo-3622608.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/3622615/pexels-photo-3622615.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/3622608/pexels-photo-3622608.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A cosy ribbed-knit cardigan with an oversized relaxed fit. Versatile for layering on cooler evenings.", tags: ["cardigan","knitwear","casual"]
  },
  {
    id: 13, name: "Printed Midi Wrap Skirt", slug: "printed-midi-wrap-skirt",
    department: "Women", category: "Skirts",
    price: 4990, originalPrice: 4990, discount: 0, rating: 4.6, reviewCount: 35,
    colours: ["Tropical Print", "Paisley"], sizes: ["XS","S","M","L","XL"],
    material: "Viscose", fit: "Regular", occasion: "Casual", badge: "New", stock: 18,
    image: "https://images.pexels.com/photos/2220316/pexels-photo-2220316.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1055691/pexels-photo-1055691.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/2220316/pexels-photo-2220316.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A flowy printed midi wrap skirt perfect for weekend outings. Adjustable tie waist for a custom fit.", tags: ["skirt","midi","print","summer"]
  },
  {
    id: 14, name: "Pleated Wide-Leg Jumpsuit", slug: "pleated-wide-leg-jumpsuit",
    department: "Women", category: "Jumpsuits",
    price: 9490, originalPrice: 12990, discount: 27, rating: 4.7, reviewCount: 48,
    colours: ["Olive", "Black", "Rust"], sizes: ["XS","S","M","L"],
    material: "Crepe", fit: "Wide Leg", occasion: "Smart Casual", badge: "Online Exclusive", stock: 10,
    image: "https://images.pexels.com/photos/1036623/pexels-photo-1036623.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/2613260/pexels-photo-2613260.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1036623/pexels-photo-1036623.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A sophisticated pleated wide-leg jumpsuit that transitions effortlessly from day to evening. V-neck with adjustable belt.", tags: ["jumpsuit","wide-leg","occasion"]
  },
  {
    id: 15, name: "Co-ord Linen Set", slug: "coord-linen-set",
    department: "Women", category: "Co-ords",
    price: 11990, originalPrice: 14990, discount: 20, rating: 4.9, reviewCount: 72,
    colours: ["White", "Sand", "Sky Blue"], sizes: ["XS","S","M","L","XL"],
    material: "Linen", fit: "Regular", occasion: "Casual", badge: "Best Seller", stock: 16,
    image: "https://images.pexels.com/photos/2905238/pexels-photo-2905238.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1769676/pexels-photo-1769676.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/2905238/pexels-photo-2905238.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A chic matching linen co-ord set including a button-front blouse and straight-leg trousers. The ultimate summer power outfit.", tags: ["coord","linen","set","summer"]
  },
  {
    id: 16, name: "Sports Bra – Medium Support", slug: "sports-bra-medium-support",
    department: "Women", category: "Activewear",
    price: 2990, originalPrice: 3990, discount: 25, rating: 4.5, reviewCount: 67,
    colours: ["Black", "Coral", "Mint"], sizes: ["XS","S","M","L","XL"],
    material: "Nylon-spandex", fit: "Compression", occasion: "Sport", badge: "Sale", stock: 35,
    image: "https://images.pexels.com/photos/3622615/pexels-photo-3622615.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/2905238/pexels-photo-2905238.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/3622615/pexels-photo-3622615.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A comfortable medium-support sports bra with racerback design and moisture-wicking fabric for active lifestyles.", tags: ["sports bra","activewear","gym"]
  },
  {
    id: 17, name: "Soft-Touch Lounge Set", slug: "soft-touch-lounge-set",
    department: "Women", category: "Loungewear",
    price: 7490, originalPrice: 9490, discount: 21, rating: 4.6, reviewCount: 53,
    colours: ["Dusty Pink", "Grey Marl", "Navy"], sizes: ["XS","S","M","L","XL"],
    material: "Modal blend", fit: "Relaxed", occasion: "Lounge", badge: "New", stock: 28,
    image: "https://images.pexels.com/photos/1462637/pexels-photo-1462637.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1375736/pexels-photo-1375736.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1462637/pexels-photo-1462637.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Ultra-soft modal-blend lounge set with relaxed-fit top and jogger trousers. Perfect for relaxing at home in style.", tags: ["loungewear","set","modal","home"]
  },
  {
    id: 18, name: "Plus Size Floral Dress", slug: "plus-size-floral-dress",
    department: "Women", category: "Plus Size",
    price: 8490, originalPrice: 8490, discount: 0, rating: 4.7, reviewCount: 44,
    colours: ["Floral Lilac", "Floral Teal"], sizes: ["1X","2X","3X","4X"],
    material: "Viscose", fit: "A-Line", occasion: "Casual", badge: "New", stock: 20,
    image: "https://images.pexels.com/photos/1536619/pexels-photo-1536619.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/2220316/pexels-photo-2220316.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1536619/pexels-photo-1536619.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A flattering A-line floral dress designed for plus-size figures. Empire waist and flowing skirt for a comfortable, confident look.", tags: ["plus size","dress","floral"]
  },
  {
    id: 19, name: "Straw Tote Bag", slug: "straw-tote-bag",
    department: "Women", category: "Bags",
    price: 4490, originalPrice: 5990, discount: 25, rating: 4.3, reviewCount: 22,
    colours: ["Natural", "Black trim"], sizes: ["One Size"],
    material: "Straw & canvas", fit: "N/A", occasion: "Beach", badge: "Sale", stock: 14,
    image: "https://images.pexels.com/photos/1183267/pexels-photo-1183267.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1462637/pexels-photo-1462637.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1183267/pexels-photo-1183267.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A stylish woven straw tote bag, perfect for beach trips and summer outings. Spacious interior with canvas lining.", tags: ["bag","tote","beach","summer","accessories"]
  },
  {
    id: 20, name: "Strappy Block-Heel Sandals", slug: "strappy-block-heel-sandals",
    department: "Women", category: "Footwear",
    price: 6990, originalPrice: 9490, discount: 26, rating: 4.5, reviewCount: 37,
    colours: ["Nude", "Black", "White"], sizes: ["36","37","38","39","40","41"],
    material: "Faux leather", fit: "N/A", occasion: "Smart Casual", badge: "Sale", stock: 20,
    image: "https://images.pexels.com/photos/2220316/pexels-photo-2220316.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1536619/pexels-photo-1536619.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/2220316/pexels-photo-2220316.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Elegant strappy block-heel sandals that pair beautifully with dresses and trousers alike. Cushioned insole for all-day comfort.", tags: ["shoes","sandals","heels","footwear"]
  },
  {
    id: 21, name: "Maternity Bump-Band Jeans", slug: "maternity-bump-band-jeans",
    department: "Women", category: "Maternity",
    price: 7990, originalPrice: 9990, discount: 20, rating: 4.8, reviewCount: 31,
    colours: ["Dark Blue", "Black"], sizes: ["S","M","L","XL"],
    material: "Stretch denim", fit: "Slim", occasion: "Casual", badge: "New", stock: 15,
    image: "https://images.pexels.com/photos/1040945/pexels-photo-1040945.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/2613260/pexels-photo-2613260.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1040945/pexels-photo-1040945.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Comfortable maternity jeans with an over-bump elastic band for gentle support throughout pregnancy. Stylish slim-fit silhouette.", tags: ["maternity","jeans","pregnancy"]
  },
  // ==================== MEN ====================
  {
    id: 22, name: "Classic Polo Shirt", slug: "classic-polo-shirt",
    department: "Men", category: "Polo Shirts",
    price: 3990, originalPrice: 4990, discount: 20, rating: 4.5, reviewCount: 88,
    colours: ["Navy", "White", "Olive", "Burgundy"], sizes: ["S","M","L","XL","XXL"],
    material: "Pique cotton", fit: "Regular", occasion: "Smart Casual", badge: "Best Seller", stock: 45,
    image: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A timeless pique cotton polo shirt with ribbed collar and two-button placket. The essential smart-casual top for every man.", tags: ["polo","casual","cotton","smart"]
  },
  {
    id: 23, name: "Slim Fit Formal Shirt", slug: "slim-fit-formal-shirt",
    department: "Men", category: "Formal Shirts",
    price: 5490, originalPrice: 5490, discount: 0, rating: 4.6, reviewCount: 55,
    colours: ["White", "Light Blue", "Pale Pink"], sizes: ["S","M","L","XL","XXL"],
    material: "Cotton blend", fit: "Slim", occasion: "Formal", badge: "New", stock: 30,
    image: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A crisp slim-fit formal shirt in easy-care cotton blend. Wrinkle-resistant finish keeps you looking sharp all day.", tags: ["shirt","formal","slim","office"]
  },
  {
    id: 24, name: "Regular Fit Chino Trousers", slug: "regular-fit-chino-trousers",
    department: "Men", category: "Trousers",
    price: 5990, originalPrice: 7990, discount: 25, rating: 4.4, reviewCount: 63,
    colours: ["Khaki", "Navy", "Olive", "Black"], sizes: ["28","30","32","34","36","38"],
    material: "Cotton stretch", fit: "Regular", occasion: "Smart Casual", badge: "Sale", stock: 35,
    image: "https://images.pexels.com/photos/842811/pexels-photo-842811.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/842811/pexels-photo-842811.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Versatile regular-fit chino trousers in stretch cotton. An office-to-weekend essential in classic neutral shades.", tags: ["chinos","trousers","casual","smart"]
  },
  {
    id: 25, name: "Graphic Print T-Shirt", slug: "graphic-print-tshirt",
    department: "Men", category: "T-Shirts",
    price: 2990, originalPrice: 3990, discount: 25, rating: 4.2, reviewCount: 77,
    colours: ["White", "Black", "Grey"], sizes: ["S","M","L","XL","XXL"],
    material: "Cotton", fit: "Regular", occasion: "Casual", badge: "Sale", stock: 40,
    image: "https://images.pexels.com/photos/1183266/pexels-photo-1183266.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1183266/pexels-photo-1183266.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A bold graphic print T-shirt in soft cotton jersey. Makes a statement on weekend outings and casual get-togethers.", tags: ["tshirt","graphic","casual","print"]
  },
  {
    id: 26, name: "Slim Fit Stretch Jeans", slug: "slim-fit-stretch-jeans-men",
    department: "Men", category: "Jeans",
    price: 7490, originalPrice: 9490, discount: 21, rating: 4.6, reviewCount: 82,
    colours: ["Dark Blue", "Medium Blue", "Black"], sizes: ["28","30","32","34","36"],
    material: "Stretch denim", fit: "Slim", occasion: "Casual", badge: "Best Seller", stock: 50,
    image: "https://images.pexels.com/photos/1043473/pexels-photo-1043473.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/842811/pexels-photo-842811.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1043473/pexels-photo-1043473.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Modern slim-fit jeans in comfortable stretch denim. A flexible waistband and tapered leg give a sharp, contemporary look.", tags: ["jeans","denim","slim","casual"]
  },
  {
    id: 27, name: "Casual Oxford Shirt", slug: "casual-oxford-shirt",
    department: "Men", category: "Casual Shirts",
    price: 5990, originalPrice: 5990, discount: 0, rating: 4.5, reviewCount: 39,
    colours: ["Blue", "Red Check", "White"], sizes: ["S","M","L","XL","XXL"],
    material: "Oxford cotton", fit: "Regular", occasion: "Casual", badge: "New", stock: 25,
    image: "https://images.pexels.com/photos/2379004/pexels-photo-2379004.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/2379004/pexels-photo-2379004.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A classic Oxford cloth casual shirt with button-down collar. Easy to dress up or down for any occasion.", tags: ["shirt","oxford","casual","smart"]
  },
  {
    id: 28, name: "Cargo Shorts", slug: "cargo-shorts-men",
    department: "Men", category: "Shorts",
    price: 4490, originalPrice: 5990, discount: 25, rating: 4.3, reviewCount: 44,
    colours: ["Khaki", "Olive", "Black", "Navy"], sizes: ["28","30","32","34","36"],
    material: "Cotton", fit: "Regular", occasion: "Casual", badge: "Sale", stock: 30,
    image: "https://images.pexels.com/photos/936229/pexels-photo-936229.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/936229/pexels-photo-936229.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Practical cargo shorts with multiple pockets for everyday adventures. Durable cotton fabric in versatile neutral shades.", tags: ["shorts","cargo","casual","summer"]
  },
  {
    id: 29, name: "Fleece Hoodie", slug: "fleece-hoodie-men",
    department: "Men", category: "Hoodies",
    price: 6990, originalPrice: 8990, discount: 22, rating: 4.7, reviewCount: 71,
    colours: ["Charcoal", "Navy", "Forest Green"], sizes: ["S","M","L","XL","XXL"],
    material: "Fleece", fit: "Regular", occasion: "Casual", badge: "Sale", stock: 25,
    image: "https://images.pexels.com/photos/1183266/pexels-photo-1183266.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/936229/pexels-photo-936229.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1183266/pexels-photo-1183266.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A cosy fleece hoodie with kangaroo pocket and drawstring hood. Great for cooler evenings and casual weekend wear.", tags: ["hoodie","fleece","casual","winter"]
  },
  {
    id: 30, name: "Men's Jogger Trousers", slug: "mens-jogger-trousers",
    department: "Men", category: "Joggers",
    price: 4990, originalPrice: 6490, discount: 23, rating: 4.5, reviewCount: 58,
    colours: ["Grey Marl", "Black", "Navy"], sizes: ["S","M","L","XL","XXL"],
    material: "Cotton blend", fit: "Tapered", occasion: "Casual", badge: null, stock: 35,
    image: "https://images.pexels.com/photos/1043473/pexels-photo-1043473.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/842811/pexels-photo-842811.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1043473/pexels-photo-1043473.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Comfortable tapered jogger trousers with elasticated waistband and cuffed hems. Perfect for gym sessions or lounging at home.", tags: ["joggers","casual","sport","lounge"]
  },
  {
    id: 31, name: "Bomber Jacket", slug: "bomber-jacket-men",
    department: "Men", category: "Jackets",
    price: 10990, originalPrice: 14990, discount: 27, rating: 4.6, reviewCount: 33,
    colours: ["Olive", "Black", "Navy"], sizes: ["S","M","L","XL","XXL"],
    material: "Nylon", fit: "Regular", occasion: "Casual", badge: "Sale", stock: 12,
    image: "https://images.pexels.com/photos/2379004/pexels-photo-2379004.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1183266/pexels-photo-1183266.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/2379004/pexels-photo-2379004.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A classic bomber jacket in lightweight nylon with ribbed collar, cuffs and hem. A versatile layer for any casual outfit.", tags: ["jacket","bomber","casual","outerwear"]
  },
  {
    id: 32, name: "Men's Activewear Set", slug: "mens-activewear-set",
    department: "Men", category: "Activewear",
    price: 7490, originalPrice: 9990, discount: 25, rating: 4.7, reviewCount: 46,
    colours: ["Black", "Navy", "Grey"], sizes: ["S","M","L","XL","XXL"],
    material: "Polyester blend", fit: "Athletic", occasion: "Sport", badge: "New", stock: 28,
    image: "https://images.pexels.com/photos/936229/pexels-photo-936229.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1043473/pexels-photo-1043473.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/936229/pexels-photo-936229.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Performance activewear set with moisture-wicking T-shirt and training shorts. Built for movement and comfort during workouts.", tags: ["activewear","sport","gym","set"]
  },
  {
    id: 33, name: "Leather Belt", slug: "leather-belt-men",
    department: "Men", category: "Belts",
    price: 2990, originalPrice: 3990, discount: 25, rating: 4.4, reviewCount: 28,
    colours: ["Brown", "Black", "Tan"], sizes: ["S","M","L","XL"],
    material: "Genuine leather", fit: "N/A", occasion: "Smart Casual", badge: "Sale", stock: 20,
    image: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A quality genuine leather belt with polished metal buckle. Available in classic shades to complement any outfit.", tags: ["belt","leather","accessories","formal"]
  },
  {
    id: 34, name: "Men's Canvas Sneakers", slug: "mens-canvas-sneakers",
    department: "Men", category: "Footwear",
    price: 5990, originalPrice: 7490, discount: 20, rating: 4.5, reviewCount: 62,
    colours: ["White", "Black", "Navy"], sizes: ["40","41","42","43","44","45"],
    material: "Canvas", fit: "N/A", occasion: "Casual", badge: "Best Seller", stock: 40,
    image: "https://images.pexels.com/photos/842811/pexels-photo-842811.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1043473/pexels-photo-1043473.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/842811/pexels-photo-842811.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Classic canvas low-top sneakers with vulcanised rubber sole. A casual everyday shoe that pairs with jeans, shorts and chinos.", tags: ["shoes","sneakers","canvas","casual","footwear"]
  },
  {
    id: 35, name: "Batik Print Shirt", slug: "batik-print-shirt-men",
    department: "Men", category: "Casual Shirts",
    price: 4490, originalPrice: 5990, discount: 25, rating: 4.6, reviewCount: 36,
    colours: ["Blue Batik", "Red Batik"], sizes: ["S","M","L","XL","XXL"],
    material: "Cotton", fit: "Regular", occasion: "Casual", badge: "Online Exclusive", stock: 18,
    image: "https://images.pexels.com/photos/2379004/pexels-photo-2379004.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1183266/pexels-photo-1183266.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/2379004/pexels-photo-2379004.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A vibrant batik-print cotton shirt celebrating Sri Lankan design. Ideal for casual events, beach days and cultural occasions.", tags: ["shirt","batik","print","casual","cultural"]
  },
  {
    id: 36, name: "Traditional Sarong", slug: "traditional-sarong-men",
    department: "Men", category: "Traditional Wear",
    price: 2490, originalPrice: 2990, discount: 17, rating: 4.7, reviewCount: 92,
    colours: ["Blue Check", "White Check", "Green Check"], sizes: ["One Size"],
    material: "Cotton", fit: "N/A", occasion: "Traditional", badge: "Best Seller", stock: 60,
    image: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/936229/pexels-photo-936229.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A classic Sri Lankan check sarong in soft cotton. Available in traditional colour combinations for everyday and ceremonial use.", tags: ["sarong","traditional","cotton","cultural"]
  },
  {
    id: 37, name: "Bi-fold Leather Wallet", slug: "bi-fold-leather-wallet",
    department: "Men", category: "Wallets",
    price: 2490, originalPrice: 3490, discount: 29, rating: 4.3, reviewCount: 51,
    colours: ["Brown", "Black"], sizes: ["One Size"],
    material: "Genuine leather", fit: "N/A", occasion: "Everyday", badge: "Sale", stock: 25,
    image: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/842811/pexels-photo-842811.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A slim bi-fold wallet in genuine leather with multiple card slots and a note compartment. Compact and durable everyday carry.", tags: ["wallet","leather","accessories"]
  },
  {
    id: 38, name: "Men's V-Neck T-Shirt Pack", slug: "mens-vneck-tshirt-pack",
    department: "Men", category: "T-Shirts",
    price: 3490, originalPrice: 4490, discount: 22, rating: 4.4, reviewCount: 103,
    colours: ["White + Grey + Navy"], sizes: ["S","M","L","XL","XXL"],
    material: "Cotton", fit: "Regular", occasion: "Casual", badge: null, stock: 50,
    image: "https://images.pexels.com/photos/1183266/pexels-photo-1183266.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1183266/pexels-photo-1183266.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A value 3-pack of V-neck T-shirts in soft cotton. Classic colours that work as basics under shirts or on their own.", tags: ["tshirt","vneck","pack","basic","cotton"]
  },
  {
    id: 39, name: "Linen Casual Shirt", slug: "linen-casual-shirt-men",
    department: "Men", category: "Casual Shirts",
    price: 5490, originalPrice: 6990, discount: 21, rating: 4.6, reviewCount: 47,
    colours: ["White", "Sky Blue", "Beige"], sizes: ["S","M","L","XL","XXL"],
    material: "Linen", fit: "Regular", occasion: "Casual", badge: "New", stock: 22,
    image: "https://images.pexels.com/photos/2379004/pexels-photo-2379004.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1043474/pexels-photo-1043474.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/2379004/pexels-photo-2379004.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A breathable linen casual shirt ideal for Sri Lanka's tropical climate. Half-rolled sleeve styling for a relaxed, summer-ready look.", tags: ["shirt","linen","casual","summer"]
  },
  {
    id: 40, name: "Men's Sports Running Shoes", slug: "mens-sports-running-shoes",
    department: "Men", category: "Footwear",
    price: 7990, originalPrice: 10990, discount: 27, rating: 4.7, reviewCount: 74,
    colours: ["Black/White", "Navy/Orange", "Grey/Blue"], sizes: ["40","41","42","43","44","45"],
    material: "Mesh upper", fit: "N/A", occasion: "Sport", badge: "Sale", stock: 30,
    image: "https://images.pexels.com/photos/936229/pexels-photo-936229.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1043473/pexels-photo-1043473.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/936229/pexels-photo-936229.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Lightweight mesh-upper running shoes with cushioned midsole and rubber outsole. Engineered for comfort on every run.", tags: ["shoes","running","sport","footwear"]
  },
  {
    id: 41, name: "Men's Swim Shorts", slug: "mens-swim-shorts",
    department: "Men", category: "Shorts",
    price: 3490, originalPrice: 4490, discount: 22, rating: 4.5, reviewCount: 39,
    colours: ["Blue Print", "Red Check", "Black"], sizes: ["S","M","L","XL","XXL"],
    material: "Polyester", fit: "Regular", occasion: "Beach", badge: "New", stock: 35,
    image: "https://images.pexels.com/photos/842811/pexels-photo-842811.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/936229/pexels-photo-936229.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/842811/pexels-photo-842811.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Quick-dry swim shorts with elasticated waist and side pockets. Perfect for beach trips and resort holidays across Sri Lanka.", tags: ["swim shorts","beach","summer","casual"]
  },
  // ==================== GIRLS ====================
  {
    id: 42, name: "Girls' Floral Frock", slug: "girls-floral-frock",
    department: "Girls", category: "Dresses",
    price: 3490, originalPrice: 4490, discount: 22, rating: 4.8, reviewCount: 56,
    colours: ["Pink Floral", "Blue Floral"], sizes: ["2-3Y","3-4Y","4-5Y","5-6Y","6-7Y"],
    material: "Cotton", fit: "Regular", occasion: "Casual", badge: "New", stock: 25,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "A cheerful floral frock in soft cotton for little girls. Smocked bodice detail with a gathered skirt and bow at the back.", tags: ["girls","dress","frock","floral","kids"]
  },
  {
    id: 43, name: "Girls' Leggings 2-Pack", slug: "girls-leggings-2pack",
    department: "Girls", category: "Leggings",
    price: 2490, originalPrice: 3490, discount: 29, rating: 4.7, reviewCount: 83,
    colours: ["Black+Navy", "Pink+Grey"], sizes: ["2-3Y","3-4Y","4-5Y","5-6Y","6-7Y","7-8Y"],
    material: "Cotton blend", fit: "Regular", occasion: "Casual", badge: "Best Seller", stock: 40,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A value 2-pack of soft cotton-blend leggings for girls. Comfortable elasticated waist and durable fabric for active play.", tags: ["girls","leggings","kids","pack"]
  },
  {
    id: 44, name: "Girls' Party Dress", slug: "girls-party-dress",
    department: "Girls", category: "Partywear",
    price: 5490, originalPrice: 6990, discount: 21, rating: 4.9, reviewCount: 62,
    colours: ["Lilac", "Rose Gold", "Ivory"], sizes: ["3-4Y","4-5Y","5-6Y","6-7Y","7-8Y","8-9Y"],
    material: "Tulle and satin", fit: "A-Line", occasion: "Party", badge: "Online Exclusive", stock: 15,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1624496/pexels-photo-1624496.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "A magical tulle-and-satin party dress with a full skirt and satin bodice. Perfect for birthdays, celebrations and special occasions.", tags: ["girls","party","dress","occasion"]
  },
  {
    id: 45, name: "Girls' Graphic Tee", slug: "girls-graphic-tee",
    department: "Girls", category: "Tops",
    price: 1990, originalPrice: 2490, discount: 20, rating: 4.5, reviewCount: 47,
    colours: ["White", "Pink", "Yellow"], sizes: ["4-5Y","5-6Y","6-7Y","7-8Y","8-9Y","9-10Y"],
    material: "Cotton", fit: "Regular", occasion: "Casual", badge: "New", stock: 35,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A fun graphic T-shirt in soft cotton for girls. Cheerful prints make it the perfect everyday top for school or play.", tags: ["girls","tshirt","graphic","kids"]
  },
  {
    id: 46, name: "Girls' School Skirt", slug: "girls-school-skirt",
    department: "Girls", category: "Schoolwear",
    price: 2490, originalPrice: 2990, discount: 17, rating: 4.6, reviewCount: 74,
    colours: ["Grey", "Navy", "White"], sizes: ["4-5Y","5-6Y","6-7Y","7-8Y","8-9Y","9-10Y","10-11Y"],
    material: "Polyester blend", fit: "A-Line", occasion: "School", badge: "Best Seller", stock: 45,
    image: "https://images.pexels.com/photos/1624496/pexels-photo-1624496.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1624496/pexels-photo-1624496.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A neat pleated school skirt in durable polyester blend. Easy-care fabric that holds its shape through busy school days.", tags: ["girls","school","skirt","uniform"]
  },
  {
    id: 47, name: "Girls' Denim Shorts", slug: "girls-denim-shorts",
    department: "Girls", category: "Shorts",
    price: 2990, originalPrice: 3990, discount: 25, rating: 4.4, reviewCount: 35,
    colours: ["Light Blue", "Dark Blue"], sizes: ["3-4Y","4-5Y","5-6Y","6-7Y","7-8Y","8-9Y"],
    material: "Denim", fit: "Regular", occasion: "Casual", badge: "Sale", stock: 28,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "Classic denim shorts for active girls. Soft-washed finish and adjustable waistband for a comfortable, carefree summer look.", tags: ["girls","denim","shorts","casual","summer"]
  },
  {
    id: 48, name: "Girls' Sleepsuit Set", slug: "girls-sleepsuit-set",
    department: "Girls", category: "Sleepwear",
    price: 2990, originalPrice: 3990, discount: 25, rating: 4.8, reviewCount: 58,
    colours: ["Pink Stars", "Mint Clouds"], sizes: ["2-3Y","3-4Y","4-5Y","5-6Y","6-7Y","7-8Y"],
    material: "Brushed cotton", fit: "Regular", occasion: "Sleepwear", badge: "New", stock: 30,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1624496/pexels-photo-1624496.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A cosy brushed-cotton sleepsuit set with long-sleeve top and bottoms. Fun printed patterns for sweet dreams every night.", tags: ["girls","sleepwear","pyjama","kids"]
  },
  {
    id: 49, name: "Girls' Canvas Shoes", slug: "girls-canvas-shoes",
    department: "Girls", category: "Footwear",
    price: 3490, originalPrice: 4490, discount: 22, rating: 4.5, reviewCount: 42,
    colours: ["White", "Pink", "Blue"], sizes: ["24","25","26","27","28","29","30","31","32"],
    material: "Canvas", fit: "N/A", occasion: "Casual", badge: null, stock: 30,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "Lightweight canvas shoes for girls with elastic lace detail for easy slip-on and off. Durable rubber sole for active play.", tags: ["girls","shoes","canvas","kids","footwear"]
  },
  {
    id: 50, name: "Girls' Clothing Set – Top & Skirt", slug: "girls-clothing-set-top-skirt",
    department: "Girls", category: "Clothing Sets",
    price: 4490, originalPrice: 5990, discount: 25, rating: 4.7, reviewCount: 49,
    colours: ["Pink Set", "Blue Set", "Yellow Set"], sizes: ["2-3Y","3-4Y","4-5Y","5-6Y","6-7Y"],
    material: "Cotton", fit: "Regular", occasion: "Casual", badge: "Sale", stock: 22,
    image: "https://images.pexels.com/photos/1624496/pexels-photo-1624496.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1624496/pexels-photo-1624496.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A coordinated top and skirt set for little girls in soft cotton. Bright, cheerful colours for a fun and playful look.", tags: ["girls","set","top","skirt","kids"]
  },
  {
    id: 51, name: "Girls' Hoodie", slug: "girls-hoodie",
    department: "Girls", category: "Tops",
    price: 3990, originalPrice: 4990, discount: 20, rating: 4.6, reviewCount: 33,
    colours: ["Pink", "Purple", "Grey"], sizes: ["5-6Y","6-7Y","7-8Y","8-9Y","9-10Y","10-11Y"],
    material: "Fleece", fit: "Regular", occasion: "Casual", badge: null, stock: 25,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A soft fleece hoodie for girls with kangaroo front pocket and drawstring hood. Perfect for cooler days and casual outings.", tags: ["girls","hoodie","fleece","casual"]
  },
  {
    id: 52, name: "Girls' Jeggings", slug: "girls-jeggings",
    department: "Girls", category: "Leggings",
    price: 2490, originalPrice: 3490, discount: 29, rating: 4.5, reviewCount: 58,
    colours: ["Dark Blue", "Black", "Light Blue"], sizes: ["3-4Y","4-5Y","5-6Y","6-7Y","7-8Y","8-9Y"],
    material: "Denim blend", fit: "Slim", occasion: "Casual", badge: "Sale", stock: 32,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1624496/pexels-photo-1624496.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "Stretch denim jeggings for girls with comfort elasticated waist. The ease of leggings with the look of jeans.", tags: ["girls","jeggings","denim","kids"]
  },
  {
    id: 53, name: "Girls' Hair Accessories Set", slug: "girls-hair-accessories-set",
    department: "Girls", category: "Accessories",
    price: 990, originalPrice: 1490, discount: 34, rating: 4.6, reviewCount: 89,
    colours: ["Pink Set", "Rainbow Set"], sizes: ["One Size"],
    material: "Mixed", fit: "N/A", occasion: "Everyday", badge: "Sale", stock: 50,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A fun set of hair accessories for girls including scrunchies, clips and headbands. Mix and match for every outfit.", tags: ["girls","accessories","hair","kids"]
  },
  {
    id: 54, name: "Girls' Blouse – School", slug: "girls-school-blouse",
    department: "Girls", category: "Schoolwear",
    price: 1990, originalPrice: 2490, discount: 20, rating: 4.7, reviewCount: 95,
    colours: ["White"], sizes: ["4-5Y","5-6Y","6-7Y","7-8Y","8-9Y","9-10Y","10-11Y","11-12Y"],
    material: "Polyester blend", fit: "Regular", occasion: "School", badge: "Best Seller", stock: 60,
    image: "https://images.pexels.com/photos/1624496/pexels-photo-1624496.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1624496/pexels-photo-1624496.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A crisp white school blouse in easy-care polyester blend. Stays fresh and neat throughout the school day.", tags: ["girls","school","blouse","uniform"]
  },
  {
    id: 55, name: "Girls' Tracksuit Set", slug: "girls-tracksuit-set",
    department: "Girls", category: "Clothing Sets",
    price: 4990, originalPrice: 6490, discount: 23, rating: 4.5, reviewCount: 44,
    colours: ["Pink", "Purple", "Sky Blue"], sizes: ["4-5Y","5-6Y","6-7Y","7-8Y","8-9Y","9-10Y"],
    material: "Cotton blend", fit: "Regular", occasion: "Sport", badge: "New", stock: 20,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "A matching tracksuit set for active girls with zip-up jacket and jogger bottoms. Great for PE, play and casual days.", tags: ["girls","tracksuit","sport","active","kids"]
  },
  // ==================== BOYS ====================
  {
    id: 56, name: "Boys' Cotton T-Shirt 3-Pack", slug: "boys-cotton-tshirt-3pack",
    department: "Boys", category: "T-Shirts",
    price: 2990, originalPrice: 3990, discount: 25, rating: 4.6, reviewCount: 112,
    colours: ["Blue+White+Grey", "Red+Navy+Black"], sizes: ["2-3Y","3-4Y","4-5Y","5-6Y","6-7Y","7-8Y"],
    material: "Cotton", fit: "Regular", occasion: "Casual", badge: "Best Seller", stock: 55,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "A great-value 3-pack of cotton T-shirts for boys in coordinating colours. Soft, durable and ideal for everyday wear.", tags: ["boys","tshirt","pack","cotton","kids"]
  },
  {
    id: 57, name: "Boys' Denim Jeans", slug: "boys-denim-jeans",
    department: "Boys", category: "Jeans",
    price: 3990, originalPrice: 4990, discount: 20, rating: 4.5, reviewCount: 68,
    colours: ["Dark Blue", "Light Blue", "Black"], sizes: ["3-4Y","4-5Y","5-6Y","6-7Y","7-8Y","8-9Y","9-10Y"],
    material: "Denim", fit: "Regular", occasion: "Casual", badge: "New", stock: 38,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Tough-wearing denim jeans for active boys with adjustable waistband for the perfect fit. Durable through the most adventurous days.", tags: ["boys","jeans","denim","kids"]
  },
  {
    id: 58, name: "Boys' School Shorts", slug: "boys-school-shorts",
    department: "Boys", category: "Schoolwear",
    price: 2190, originalPrice: 2990, discount: 27, rating: 4.6, reviewCount: 88,
    colours: ["Grey", "Navy", "Khaki"], sizes: ["4-5Y","5-6Y","6-7Y","7-8Y","8-9Y","9-10Y","10-11Y"],
    material: "Polyester blend", fit: "Regular", occasion: "School", badge: "Best Seller", stock: 50,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "Neat and durable school shorts for boys in easy-care polyester blend. Adjustable waistband for a comfortable all-day fit.", tags: ["boys","school","shorts","uniform"]
  },
  {
    id: 59, name: "Boys' Graphic Hoodie", slug: "boys-graphic-hoodie",
    department: "Boys", category: "Tops",
    price: 4490, originalPrice: 5990, discount: 25, rating: 4.7, reviewCount: 54,
    colours: ["Grey", "Navy", "Red"], sizes: ["5-6Y","6-7Y","7-8Y","8-9Y","9-10Y","10-11Y"],
    material: "Fleece", fit: "Regular", occasion: "Casual", badge: "Sale", stock: 25,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A cool graphic fleece hoodie for boys. Soft and warm with a fun printed design on the chest. Great for school or play.", tags: ["boys","hoodie","graphic","fleece","kids"]
  },
  {
    id: 60, name: "Boys' Cargo Trousers", slug: "boys-cargo-trousers",
    department: "Boys", category: "Trousers",
    price: 3490, originalPrice: 4490, discount: 22, rating: 4.4, reviewCount: 41,
    colours: ["Khaki", "Olive", "Black"], sizes: ["4-5Y","5-6Y","6-7Y","7-8Y","8-9Y","9-10Y"],
    material: "Cotton", fit: "Regular", occasion: "Casual", badge: "New", stock: 28,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "Practical cargo trousers for boys with multiple pockets and a reinforced knee area. Built for busy, adventurous kids.", tags: ["boys","cargo","trousers","kids","casual"]
  },
  {
    id: 61, name: "Boys' Clothing Set – Shirt & Shorts", slug: "boys-clothing-set-shirt-shorts",
    department: "Boys", category: "Clothing Sets",
    price: 3990, originalPrice: 4990, discount: 20, rating: 4.6, reviewCount: 47,
    colours: ["Blue Set", "Red Set", "Olive Set"], sizes: ["2-3Y","3-4Y","4-5Y","5-6Y","6-7Y"],
    material: "Cotton", fit: "Regular", occasion: "Casual", badge: "Sale", stock: 22,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A coordinated shirt-and-shorts set for boys in breathable cotton. Perfect for outings, casual occasions and everyday fun.", tags: ["boys","set","shirt","shorts","kids"]
  },
  {
    id: 62, name: "Boys' Pyjama Set", slug: "boys-pyjama-set",
    department: "Boys", category: "Sleepwear",
    price: 2990, originalPrice: 3990, discount: 25, rating: 4.7, reviewCount: 65,
    colours: ["Dino Print", "Space Print", "Cars Print"], sizes: ["2-3Y","3-4Y","4-5Y","5-6Y","6-7Y","7-8Y"],
    material: "Brushed cotton", fit: "Regular", occasion: "Sleepwear", badge: "New", stock: 35,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "A cosy brushed-cotton pyjama set for boys with fun prints. Long-sleeve top and full-length bottoms for comfortable nights.", tags: ["boys","pyjama","sleepwear","kids"]
  },
  {
    id: 63, name: "Boys' School Shirt", slug: "boys-school-shirt",
    department: "Boys", category: "Schoolwear",
    price: 1990, originalPrice: 2490, discount: 20, rating: 4.6, reviewCount: 102,
    colours: ["White"], sizes: ["4-5Y","5-6Y","6-7Y","7-8Y","8-9Y","9-10Y","10-11Y","11-12Y"],
    material: "Polyester blend", fit: "Regular", occasion: "School", badge: "Best Seller", stock: 65,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A crisp white school shirt for boys with easy-care polyester blend. Stay-neat fabric for a smart appearance all day long.", tags: ["boys","school","shirt","uniform"]
  },
  {
    id: 64, name: "Boys' Sneakers", slug: "boys-sneakers",
    department: "Boys", category: "Footwear",
    price: 4490, originalPrice: 5990, discount: 25, rating: 4.5, reviewCount: 57,
    colours: ["White/Blue", "Black/Red", "Grey/Green"], sizes: ["26","27","28","29","30","31","32","33","34"],
    material: "Mesh upper", fit: "N/A", occasion: "Casual", badge: "Sale", stock: 32,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "Sporty mesh-upper sneakers for boys with cushioned sole. Lightweight and flexible for running, play and school.", tags: ["boys","sneakers","shoes","kids","footwear"]
  },
  {
    id: 65, name: "Boys' Swim Shorts", slug: "boys-swim-shorts",
    department: "Boys", category: "Shorts",
    price: 2490, originalPrice: 2990, discount: 17, rating: 4.4, reviewCount: 38,
    colours: ["Blue Surf", "Tropical Print", "Grey"], sizes: ["2-3Y","3-4Y","4-5Y","5-6Y","6-7Y","7-8Y"],
    material: "Polyester", fit: "Regular", occasion: "Beach", badge: "New", stock: 30,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Quick-drying swim shorts for boys with elasticated waist and fun prints. Perfect for beach days and pool time.", tags: ["boys","swim shorts","beach","summer","kids"]
  },
  {
    id: 66, name: "Boys' Polo Shirt", slug: "boys-polo-shirt",
    department: "Boys", category: "Tops",
    price: 2490, originalPrice: 3490, discount: 29, rating: 4.5, reviewCount: 49,
    colours: ["White","Navy","Red","Green"], sizes: ["3-4Y","4-5Y","5-6Y","6-7Y","7-8Y","8-9Y"],
    material: "Pique cotton", fit: "Regular", occasion: "Smart Casual", badge: "Sale", stock: 35,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "A smart pique cotton polo shirt for boys. Ribbed collar and two-button placket for a neat, presentable look at any occasion.", tags: ["boys","polo","kids","smart"]
  },
  {
    id: 67, name: "Boys' Active Shorts Set", slug: "boys-active-shorts-set",
    department: "Boys", category: "Clothing Sets",
    price: 3490, originalPrice: 4490, discount: 22, rating: 4.6, reviewCount: 43,
    colours: ["Black/Red","Navy/White","Grey/Green"], sizes: ["4-5Y","5-6Y","6-7Y","7-8Y","8-9Y","9-10Y"],
    material: "Polyester blend", fit: "Athletic", occasion: "Sport", badge: null, stock: 28,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A performance active set for boys including a T-shirt and matching shorts. Moisture-wicking fabric for sports and outdoor activities.", tags: ["boys","active","sport","set","kids"]
  },
  {
    id: 68, name: "Boys' Winter Jacket", slug: "boys-winter-jacket",
    department: "Boys", category: "Tops",
    price: 5990, originalPrice: 7990, discount: 25, rating: 4.7, reviewCount: 36,
    colours: ["Navy", "Olive", "Red"], sizes: ["4-5Y","5-6Y","6-7Y","7-8Y","8-9Y","9-10Y"],
    material: "Polyester", fit: "Regular", occasion: "Casual", badge: "New", stock: 20,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "A lightweight padded jacket for boys with zip closure and ribbed cuffs. Keeps young adventurers warm on cooler days.", tags: ["boys","jacket","winter","kids","outerwear"]
  },
  {
    id: 69, name: "Boys' Accessories Set – Cap & Bag", slug: "boys-accessories-set",
    department: "Boys", category: "Accessories",
    price: 1990, originalPrice: 2990, discount: 33, rating: 4.4, reviewCount: 28,
    colours: ["Blue Set", "Black Set"], sizes: ["One Size"],
    material: "Cotton/Canvas", fit: "N/A", occasion: "Casual", badge: "Sale", stock: 20,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A fun accessories set for boys including a cotton cap and small canvas backpack. Perfect for school trips and weekend outings.", tags: ["boys","accessories","cap","bag","kids"]
  },
  {
    id: 70, name: "Boys' Tracksuit Set", slug: "boys-tracksuit-set",
    department: "Boys", category: "Clothing Sets",
    price: 4990, originalPrice: 6490, discount: 23, rating: 4.6, reviewCount: 51,
    colours: ["Navy","Black","Grey"], sizes: ["4-5Y","5-6Y","6-7Y","7-8Y","8-9Y","9-10Y","10-11Y"],
    material: "Cotton blend", fit: "Regular", occasion: "Sport", badge: "New", stock: 25,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "A coordinated cotton-blend tracksuit for boys with zip-front jacket and jogger trousers. Great for sport, school and casual days.", tags: ["boys","tracksuit","sport","active","kids"]
  },
  // ==================== BABY ====================
  {
    id: 71, name: "Baby Bodysuit 5-Pack", slug: "baby-bodysuit-5pack",
    department: "Baby", category: "Bodysuits",
    price: 4490, originalPrice: 5990, discount: 25, rating: 4.9, reviewCount: 143,
    colours: ["Pastel Mix", "White Mix"], sizes: ["0-3M","3-6M","6-9M","9-12M"],
    material: "100% cotton", fit: "Regular", occasion: "Everyday", badge: "Best Seller", stock: 55,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "A value 5-pack of soft 100% cotton bodysuits for babies. Envelope neckline for easy dressing and poppers at the base for quick nappy changes.", tags: ["baby","bodysuit","newborn","pack","essentials"]
  },
  {
    id: 72, name: "Baby Sleepsuit Set of 3", slug: "baby-sleepsuit-set-3",
    department: "Baby", category: "Sleepsuits",
    price: 3990, originalPrice: 4990, discount: 20, rating: 4.9, reviewCount: 118,
    colours: ["Pastel Stars","Animal Print","Stripes"], sizes: ["Newborn","0-3M","3-6M","6-9M"],
    material: "100% cotton", fit: "Regular", occasion: "Sleepwear", badge: "New", stock: 40,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A set of 3 all-in-one sleepsuits in soft cotton with zip-front for night-time ease. Enclosed feet to keep little toes warm.", tags: ["baby","sleepsuit","newborn","essentials"]
  },
  {
    id: 73, name: "Baby Girl Dress Set", slug: "baby-girl-dress-set",
    department: "Baby", category: "Dresses",
    price: 3490, originalPrice: 4490, discount: 22, rating: 4.8, reviewCount: 76,
    colours: ["Pink Floral","Lilac","Mint"], sizes: ["3-6M","6-9M","9-12M","12-18M"],
    material: "Cotton blend", fit: "Regular", occasion: "Casual", badge: "New", stock: 28,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "An adorable cotton-blend dress set for baby girls with matching bloomers. Sweet prints and comfortable fabric for all-day happiness.", tags: ["baby","girl","dress","newborn"]
  },
  {
    id: 74, name: "Baby Boy Dungarees Set", slug: "baby-boy-dungarees-set",
    department: "Baby", category: "Clothing Sets",
    price: 3990, originalPrice: 4990, discount: 20, rating: 4.8, reviewCount: 64,
    colours: ["Blue Denim","Olive","Grey"], sizes: ["3-6M","6-9M","9-12M","12-18M","18-24M"],
    material: "Cotton", fit: "Regular", occasion: "Casual", badge: "New", stock: 25,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Charming cotton dungarees for baby boys with adjustable braces and press-stud opening at the leg. Comes with a matching bodysuit.", tags: ["baby","boy","dungarees","set"]
  },
  {
    id: 75, name: "Baby Essentials Gift Set", slug: "baby-essentials-gift-set",
    department: "Baby", category: "Essentials",
    price: 5990, originalPrice: 7990, discount: 25, rating: 5.0, reviewCount: 89,
    colours: ["Pink Gift","Blue Gift","Neutral Gift"], sizes: ["Newborn","0-3M"],
    material: "100% cotton", fit: "Regular", occasion: "Gift", badge: "Online Exclusive", stock: 20,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "A thoughtfully curated baby essentials gift set with bodysuit, sleepsuit, hat and scratch mitts in 100% soft cotton. The perfect new-arrival gift.", tags: ["baby","gift","essentials","newborn"]
  },
  {
    id: 76, name: "Baby Soft-Sole Pre-Walker Shoes", slug: "baby-soft-sole-shoes",
    department: "Baby", category: "Footwear",
    price: 1990, originalPrice: 2490, discount: 20, rating: 4.7, reviewCount: 58,
    colours: ["Pink","Blue","Cream"], sizes: ["0-6M","6-12M","12-18M"],
    material: "Cotton upper", fit: "N/A", occasion: "Everyday", badge: "New", stock: 35,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "Adorable soft-sole pre-walker shoes for babies in cotton fabric. Gentle elastic for easy on-and-off with a non-slip soft sole.", tags: ["baby","shoes","footwear","pre-walker"]
  },
  {
    id: 77, name: "Baby Knitted Hat & Mittens Set", slug: "baby-hat-mittens-set",
    department: "Baby", category: "Accessories",
    price: 1490, originalPrice: 1990, discount: 25, rating: 4.8, reviewCount: 92,
    colours: ["Cream","Pink","Blue","Mint"], sizes: ["0-6M","6-12M"],
    material: "Acrylic knit", fit: "N/A", occasion: "Everyday", badge: "Sale", stock: 45,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "A sweet knitted hat and scratch mittens set for babies. Soft, warm and gentle on delicate newborn skin.", tags: ["baby","hat","mittens","accessories","newborn"]
  },
  {
    id: 78, name: "Baby Tops Set of 3", slug: "baby-tops-set-3",
    department: "Baby", category: "Tops",
    price: 2990, originalPrice: 3990, discount: 25, rating: 4.7, reviewCount: 67,
    colours: ["Pastel Mix","Brights Mix"], sizes: ["3-6M","6-9M","9-12M","12-18M","18-24M"],
    material: "100% cotton", fit: "Regular", occasion: "Casual", badge: null, stock: 35,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A value set of 3 cotton tops for babies in coordinating colours. Envelope necks for fuss-free dressing and soft fabric on sensitive skin.", tags: ["baby","tops","set","cotton","essentials"]
  },
  {
    id: 79, name: "Baby Bottoms Set of 2", slug: "baby-bottoms-set-2",
    department: "Baby", category: "Bottoms",
    price: 2490, originalPrice: 3490, discount: 29, rating: 4.7, reviewCount: 54,
    colours: ["Navy+Grey","Pink+Mint"], sizes: ["3-6M","6-9M","9-12M","12-18M","18-24M"],
    material: "Cotton blend", fit: "Regular", occasion: "Casual", badge: "Sale", stock: 38,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "A pack of 2 comfortable cotton-blend baby bottoms with elasticated waist. Soft and practical for nappy days.", tags: ["baby","bottoms","trousers","essentials","pack"]
  },
  {
    id: 80, name: "Baby Newborn Starter Set", slug: "baby-newborn-starter-set",
    department: "Baby", category: "Essentials",
    price: 7990, originalPrice: 9990, discount: 20, rating: 5.0, reviewCount: 112,
    colours: ["Pink Set","Blue Set","Neutral Set"], sizes: ["Newborn","0-3M"],
    material: "100% cotton", fit: "Regular", occasion: "Everyday", badge: "Best Seller", stock: 25,
    image: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=800"],
    description: "A comprehensive newborn starter set with bodysuits, sleepsuit, hat, scratch mitts and bib – everything needed for the first weeks. Made from 100% soft cotton.", tags: ["baby","newborn","starter","essentials","gift"]
  },
  {
    id: 81, name: "Baby Muslin Swaddle Blanket", slug: "baby-muslin-swaddle",
    department: "Baby", category: "Essentials",
    price: 1990, originalPrice: 2490, discount: 20, rating: 4.9, reviewCount: 98,
    colours: ["White","Mint","Peach"], sizes: ["One Size"],
    material: "Muslin cotton", fit: "N/A", occasion: "Everyday", badge: "New", stock: 50,
    image: "https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    hoverImage: "https://images.pexels.com/photos/1620760/pexels-photo-1620760.jpeg?auto=compress&cs=tinysrgb&w=500&h=650&fit=crop",
    gallery: ["https://images.pexels.com/photos/35537/child-children-girl-happy.jpg?auto=compress&cs=tinysrgb&w=800"],
    description: "A large soft muslin swaddle blanket for babies. Breathable and lightweight, ideal for swaddling, nursing and play mats.", tags: ["baby","muslin","swaddle","blanket","essentials"]
  }
];

// Helper functions
function getProductById(id) {
  return products.find(p => p.id === parseInt(id));
}
function getProductsByDepartment(dept) {
  return products.filter(p => p.department.toLowerCase() === dept.toLowerCase());
}
function getProductsByCategory(cat) {
  return products.filter(p => p.category.toLowerCase() === cat.toLowerCase());
}
function getSaleProducts() {
  return products.filter(p => p.discount > 0);
}
function getNewArrivals() {
  return products.filter(p => p.badge === "New");
}
function getBestSellers() {
  return products.filter(p => p.badge === "Best Seller");
}
function searchProducts(query) {
  const q = query.toLowerCase();
  return products.filter(p =>
    p.name.toLowerCase().includes(q) ||
    p.category.toLowerCase().includes(q) ||
    p.department.toLowerCase().includes(q) ||
    (p.colours && p.colours.some(c => c.toLowerCase().includes(q))) ||
    (p.material && p.material.toLowerCase().includes(q)) ||
    (p.tags && p.tags.some(t => t.toLowerCase().includes(q)))
  );
}
