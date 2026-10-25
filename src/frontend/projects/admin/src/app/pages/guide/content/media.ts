import { GuideSection } from '../guide.model';

/**
 * Photos and video. The limits in the first table are enforced by the system (read from the upload code and
 * its configuration). The pixel sizes in the second section are recommendations worked out from how large
 * each place is drawn on the shop, not rules: the system accepts any size and crops to fit.
 */
export const MEDIA_SECTIONS: readonly GuideSection[] = [
  {
    id: 'photos-and-video',
    group: 'Photos and video',
    title: 'Photos and video: formats, quality and how to prepare them',
    summary:
      'What the shop accepts, what it does to a picture after you upload it, and how to export photos and video so they look sharp and are not refused. Read this before you upload anything.',
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Two kinds of statement are made in this guide. **Rules** (file types, file sizes) are enforced by the system: a file that breaks one is refused. **Recommendations** (pixel sizes, export quality) are our advice for the sharpest result: the system accepts any size up to 60 megapixels, and the shop crops the picture to fit when it draws it. Pixel sizes for every place are in [Sizes for every place](#photo-sizes).',
      },
      { kind: 'heading', text: 'What the system accepts' },
      {
        kind: 'table',
        caption: 'Upload rules enforced by the system',
        head: ['Item', 'Rule'],
        rows: [
          ['Photo file types', 'JPEG, PNG or WebP. The type is read from inside the file, so renaming a file (for example calling a HEIC picture ".jpg") does not help.'],
          ['Photo file size', 'Up to 15 MB. Some screens stop earlier: product photographs and the Sale banner at **8 MB**, the logo at **2 MB**.'],
          ['Photo pixel size', 'No minimum. Maximum 60 megapixels (for example 8000 by 7500 pixels). A picture is never enlarged.'],
          ['Video file type', '**MP4 only**, with an **H.264** (also called AVC) video track.'],
          ['Video file size', 'Up to **50 MB**.'],
          ['Video poster', 'Every video needs a **poster image**: a still picture shown before the video plays. The system does not make one from the video.'],
          ['Video length and shape', 'No limit on length, resolution or bitrate. The system does not convert, resize or compress video. What you upload is exactly what is played, so it must already be small enough and in the right format.'],
          ['Refused photo types', 'HEIC/HEIF (the iPhone default), GIF (including animated ones), SVG, AVIF, TIFF, BMP and PDF.'],
          ['Refused video types', 'QuickTime (.mov), WebM, AVI, and any MP4 that is not H.264, for example HEVC (H.265), which iPhones record by default.'],
          ['Uploading', 'One file at a time. You need the **Media Create** permission (Store Manager and Content Editor have it by default). On the product form and the logo the file is sent as soon as you choose it. On the Homepage, Mega menu cards and Size guides, and on a Sale campaign banner, nothing is sent until you press **Upload image**, **Upload video** or **Upload the picture** after choosing the file; then save the item or campaign.'],
        ],
      },
      { kind: 'heading', text: 'What happens to a photo after you upload it' },
      {
        kind: 'list',
        items: [
          'The system keeps your original with the hidden camera information removed, including location (GPS) data. A photo that relies on a rotation tag is saved the right way up.',
          'It makes WebP copies **160, 600, 1200 and 2000 pixels wide**, never larger than your original. Each place on the shop uses the copy that suits its size, so a bigger upload never makes a page slower, but nothing wider than 2000 pixels is ever shown.',
          'It **never crops**. Each place on the shop has a fixed shape and crops your picture to fit, keeping the centre. Keep the important part of every picture in the middle.',
          'If you upload exactly the same file again, the system re-uses the picture that is already stored, together with the description it already has. The description you type the second time is ignored.',
          'There is **no media library screen**. You upload inside each place (a product, a homepage block, a banner), and the remove buttons (**Remove the image**, **Remove the video**, **Remove the logo**, **Remove the banner**, or **Remove** on a product photograph) only take the picture out of that place; they do not delete it. A replaced picture is a new file with a new address, so customers never see an old copy from their browser.',
          'A video is stored exactly as uploaded. Nothing converts it, shrinks it or picks a frame from it.',
        ],
      },
      { kind: 'heading', text: 'Exporting photos: settings that work well' },
      {
        kind: 'callout',
        tone: 'note',
        text: 'This part is general good practice for the web, not a rule of the system.',
      },
      {
        kind: 'list',
        items: [
          '**Format:** JPEG for photographs, at a quality of about 80 to 90 out of 100. WebP is also fine. Use PNG only for a logo or a picture that needs a transparent background; a PNG photograph is very large.',
          '**Colour:** export in **sRGB**. Print-style CMYK files can look wrong on screen.',
          '**Size:** export at the pixel size for the place (next section) and keep each file **under 8 MB**, so it is accepted on every screen. At the recommended sizes a photograph is usually well below that.',
          '**Keep words out of pictures** unless the picture is itself a banner design. Words inside a photograph are cropped on some screens and cannot be read by a screen reader or a search engine. Put the words in the Title, Subtitle and Text boxes.',
          '**iPhone:** in Settings, Camera, Formats, choose **Most Compatible**. It records JPEG photographs and H.264 video. For HEIC photographs you already have, export them as JPEG before uploading.',
          'Name files simply. The file name is never shown to customers.',
        ],
      },
      { kind: 'heading', text: 'Exporting video' },
      {
        kind: 'steps',
        items: [
          'Plan the shape for where it will be used (see [Sizes for every place](#photo-sizes)): the homepage video is wide (16:9), a product video is upright (3:4).',
          'Export as **MP4** with the **H.264** video codec. In most editors the format is called MP4 and the codec H.264 or AVC. Sound is optional; the homepage video always plays muted.',
          'If your editor offers "web optimised" or "fast start", switch it on, so playback can begin before the whole file has downloaded.',
          'Check the file is **under 50 MB**. If it is larger, shorten the clip, lower the resolution (for example to 1080 pixels on the long side) or lower the bitrate.',
          'Make the **poster image**: a good, sharp frame from the video, the same shape as the video, exported as a JPEG.',
          'Upload the video and its poster together.',
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'Video from an iPhone or recent Android phone is often HEVC (H.265). It is refused with a message that names the codec. Re-export it as H.264 before uploading. A .mov file is refused too: export it as MP4.',
      },
      { kind: 'heading', text: 'Alt text: the description of a picture' },
      {
        kind: 'text',
        text: '**Alt text** is a short description of the picture. People who use a screen reader hear it instead of seeing the picture, and it appears if the picture cannot load. It can be up to 250 characters (200 for the logo). Say what is in the picture in a few plain words, for example "Indigo straight-leg jeans and a white linen shirt, front view". Do not begin with "image of", and do not stuff in search words.',
      },
      {
        kind: 'list',
        title: 'Where alt text is needed',
        items: [
          '**Products:** type it for every Gallery and Hover photograph. A product cannot be put on the shop while one is missing (the library\'s own description is used if you leave a box empty and it has one). The video is exempt.',
          '**Homepage pictures, mega menu cards and size guide images:** asked for as you upload ("Description for screen readers"), and required.',
          '**Sale banner:** asked for when you upload a new one.',
          '**Logo:** if you leave it empty the shop\'s name is used.',
          '**Pictures inside a content page:** every picture needs alt text before the page can go on the shop, and editing a live page so a picture loses its alt text is refused.',
        ],
      },
      { kind: 'heading', text: 'Photography suggestions' },
      {
        kind: 'callout',
        tone: 'note',
        text: 'These are suggestions that follow from how the shop displays pictures, not rules.',
      },
      {
        kind: 'list',
        items: [
          'Shoot product photographs **upright (3:4)**, with the garment centred and room around it. Every product card and product page uses that shape.',
          'Use the **same background, light and style** across the range. The shop has an editorial look, and mixed styles show on a listing page.',
          'Give each product **at least two photographs**. The first Gallery photograph is the picture on listing cards. A **Hover photograph** shows when a customer points at the card; if you set none, the second Gallery photograph is used. A product with one photograph has no hover picture.',
          'Where you can, add a front view, a back or detail view and a worn view, and tie photographs to their colour with **Only for colour**. A product can hold up to 60 photographs and videos in all.',
          'Keep the subject in the middle of wide pictures. On the homepage, the top and bottom of a wide picture are cut on large screens.',
        ],
      },
      {
        kind: 'callout',
        tone: 'tip',
        text: 'Pictures cannot be uploaded for category tiles, category banners, a department\'s hero picture, colour swatches or the shop icon. Those places have no upload box in the admin.',
      },
    ],
  },
  {
    id: 'photo-sizes',
    group: 'Photos and video',
    title: 'Sizes for every place a picture or video can go',
    summary:
      'For each place: where to upload it, the shape the shop shows it in, and the pixel size to prepare. Sizes are width by height in pixels.',
    screens: [
      { label: 'Products', path: '/products' },
      { label: 'Homepage', path: '/content/home' },
      { label: 'Mega menu cards', path: '/content/mega-menu' },
      { label: 'Sale campaigns', path: '/marketing/sale-campaigns' },
      { label: 'Size guides', path: '/content/size-guides' },
      { label: 'Settings', path: '/settings' },
    ],
    blocks: [
      {
        kind: 'text',
        text: '**Shape and size to prepare** gives first the frame the shop crops your picture to (width : height), then the pixels to export. A different shape still uploads; the shop shows the centre of it. Each recommended size is at least twice the width the place is drawn at (so it looks sharp on a high-density screen), small tiles are given a little more room, and nothing wider than 2000 pixels is ever used. Bigger is accepted but gives no benefit. On a small screen, slide a wide table sideways; its first column stays in view.',
      },
      { kind: 'heading', text: 'Products' },
      {
        kind: 'table',
        caption: 'Product photographs and video',
        head: ['Place', 'Where to upload', 'Shape and size to prepare', 'Notes'],
        rows: [
          ['Gallery photograph (also the listing card picture)', 'Products, product form, **Photographs and video**, Role **Gallery photograph**', 'Upright, 3:4. 2000 x 2667. Smallest sensible: 1200 x 1600.', 'The first Gallery photograph in the list is the picture on listing cards. The product page shows 3:4 and a full-screen viewer shows the whole photograph with zoom to 3 times. Under 8 MB.'],
          ['Hover photograph', 'Same, Role **Hover photograph**', '3:4. Same as above', 'Shows when a customer points at a card. If you set none, the second Gallery photograph is used. A Hover photograph also appears as a slide on the product page. One per colour.'],
          ['Photograph for one colour', 'Same, **Only for colour**', '3:4. Same as above', 'A customer who picks that colour sees its photographs plus those set to "Every colour". A colour with none of its own sees just the "Every colour" photographs; if there are none of those either, it shows every photograph.'],
          ['Product video', 'Same, **Add the video (MP4)**, then its poster', 'Upright, 3:4. For example 1080 x 1440, MP4 H.264, under 50 MB. Poster: a 3:4 JPEG of the same size.', 'One video per product, for all colours. It is the last slide in the gallery, with play controls. A wide video is cropped to upright.'],
        ],
      },
      { kind: 'heading', text: 'Homepage' },
      {
        kind: 'table',
        caption: 'Homepage pictures and video',
        head: ['Place', 'Where to upload', 'Shape and size to prepare', 'Notes'],
        rows: [
          ['Hero video', 'Homepage, **Hero video** block, the item, **Video**', 'Wide, 16:9. 1920 x 1080, MP4 H.264, under 50 MB', 'Plays only from 768 pixels wide, and only when the visitor allows motion; muted, looping, with a pause button. The frame is at most 880 pixels tall, so on very wide screens the top and bottom are cut: keep faces and products in the middle.'],
          ['Hero poster', 'Same, **Poster image (shown before it plays)**', '16:9. 1920 x 1080 JPEG, kept light', 'Delivered at its original size, not resized, so keep the file small (a few hundred KB). Also the picture used when the homepage is shared.'],
          ['Hero still', 'Same, **Still image (phones, and people who prefer less motion)**', 'Wide, 16:9. 2000 x 1125', 'Required once a video is added. What phones, tablets with reduced motion and the first page load show.'],
          ['Hero phone image', 'Same, **Phone image**', 'Upright, 4:5. 1200 x 1500', 'Optional. Used below 768 pixels wide instead of the still.'],
          ['Campaign banner', 'Homepage, **Campaign banner** block, **Image** and **Phone image**', 'Wide 21:9 from 768 pixels; upright 4:5 on phones. Wide: 2000 x 857. Phone: 1200 x 1500.', 'At most 720 pixels tall. If you upload only one picture it is used at every width and cropped to fit. A banner item cannot be saved with **Show this item** ticked until it has a picture: untick it, upload the picture, then tick it.'],
          ['Activewear feature (the shop calls it "Linen and denim")', 'Homepage, **Activewear feature** block', 'Same as the campaign banner. Same as the campaign banner', 'Same behaviour as the campaign banner.'],
          ['Shop by department tile', 'Homepage, **Shop by department**, each item', 'Upright, 4:5. 1200 x 1500 (smallest sensible: 900 x 1125)', 'Up to 6 tiles. **Image** is used first; **Phone image** only if Image is empty. A tile with no picture is shown as a named panel.'],
          ['Trending items tile', 'Homepage, **Trending items** (hand-picked), each item', '3:4. 900 x 1200', 'Up to 12 tiles. Only used when "Which products" is "Hand-picked, as the items below".'],
          ['Social gallery picture', 'Homepage, **Social gallery**, each item', 'Square, 1:1. 1080 x 1080 (smallest sensible: 600 x 600)', 'Up to 12. An Image is required for every item. There is no automatic social feed: add each picture by hand and link it to your profile or post.'],
        ],
      },
      { kind: 'heading', text: 'Menu, Sale page, guides, logo and pages' },
      {
        kind: 'table',
        caption: 'Other pictures',
        head: ['Place', 'Where to upload', 'Shape and size to prepare', 'Notes'],
        rows: [
          ['Mega menu card picture', 'Content, **Mega menu cards**, **Picture**', 'Upright, 4:5. 800 x 1000 (smallest sensible: 600 x 750)', 'Shown only in the desktop menu (1024 pixels and wider). Up to 6 cards per department; one is shown at a time. Needed before a card can be switched on.'],
          ['Sale campaign banner', 'Marketing, **Sale campaigns**, the campaign\'s banner', 'Wide strip, 16:5 (about three times as wide as tall). 2000 x 625', 'Shown across the top of the Sale page only while the campaign is live. On a phone the strip is only about 110 pixels tall and its edges are cropped: keep any words large and in the middle. Under 8 MB.'],
          ['Size guide picture', 'Content, **Size guides**, **Image, e.g. how to measure**', 'Any shape. At least 720 pixels wide', 'Shown in the size guide pop-up at its own shape, up to 360 pixels wide, uncropped.'],
          ['Logo', 'Settings, **Logo** (Store group)', 'Wide, about 4:1. About 600 x 150, PNG or WebP with a transparent background', 'Under 2 MB. The header draws it 24 pixels tall on phones and tablets and 32 pixels tall on desktop. A JPEG logo shows its background as a rectangle. Use a flat logo file, not a mock-up photograph.'],
          ['Pictures inside a content page', 'Content pages, the page body, written as an image tag', 'Any. About 1440 pixels wide for a full-width picture', 'There is no upload button inside a page: the picture must already be on the internet at an address you can paste. Every picture needs alt text. Ask your developer if you need a picture hosted.'],
        ],
      },
      { kind: 'heading', text: 'Places that take no upload' },
      {
        kind: 'list',
        items: [
          '**Benefits** on the homepage: you pick one of nine icon names, which share five pictures: a lorry (delivery, shipping), arrows (returns, exchanges), a shield (quality, secure, payment), a phone (support) and an envelope (contact).',
          '**New arrivals** and **Sale** rows: they show product cards from the catalogue.',
          '**Category pictures and a department\'s hero picture, colour swatch pictures and the shop icon:** cannot be set from the admin. A colour is shown by its **Swatch colour** code.',
          '**Emails** carry no pictures.',
          '**Customer payment slips** are uploaded by customers (JPEG, PNG, WebP or PDF, up to 5 MB) and are private.',
        ],
      },
    ],
  },
  {
    id: 'upload-problems',
    group: 'Photos and video',
    title: 'When an upload is refused: messages and what to do',
    summary:
      'The messages you may see while uploading a photograph or video, what each means, and how to fix it.',
    blocks: [
      {
        kind: 'table',
        caption: 'Upload messages and fixes',
        head: ['What you see', 'What it means', 'What to do'],
        rows: [
          ['"The image is 20.3 MB. The limit is 15 MB." (numbers vary)', 'The photograph is over the 15 MB limit.', 'Export it again at a smaller pixel size or a lower JPEG quality.'],
          ['"That photograph is larger than 8 MB. Save it smaller and try again." (also "That picture is larger than 8 MB..." on the Sale banner)', 'The product form and the Sale banner stop at 8 MB.', 'Export smaller, as above.'],
          ['"That file is larger than 2 MB. A logo should be well under that."', 'The logo is over 2 MB.', 'Export a smaller PNG or WebP, about 600 x 150.'],
          ['"The file is a HEIC/HEIF image (an iPhone photograph: export it as JPEG first)..."', 'iPhone-format photograph. Renaming does not help.', 'Convert to JPEG, or set the phone to Most Compatible.'],
          ['"The video is 60.1 MB. The limit is 50 MB..." or "That video is larger than 50 MB, which is the most the library stores. Export it smaller."', 'The video is over 50 MB.', 'Shorten it, lower the resolution or lower the bitrate, and export again.'],
          ['"The file is a QuickTime (.mov) video. Product video has to be MP4 (H.264)..."', 'A .mov file.', 'Export as MP4 with H.264.'],
          ['"The MP4\'s video track is \'hvc1\', not H.264 (avc1)..."', 'The video uses the HEVC codec.', 'Re-export as H.264.'],
          ['"The MP4 has no readable video track. It may be audio only, or the file may be truncated."', 'The file is damaged, unfinished or has no picture.', 'Export it again from the original.'],
          ['"The file starts like a JPEG image but could not be opened as one, or it declares more than 60 megapixels." (or "...its image data could not be decoded")', 'The file is damaged, cut short or has too many pixels.', 'Open it in a photo program and save it again as a JPEG at a normal size.'],
          ['"Choose a poster image too. A video cannot be added without one."', 'You chose a video but no poster.', 'Choose the poster image as well, then upload.'],
          ['"The poster image was refused: ..."', 'The poster failed a photograph rule (type, size or pixels). The rest of the message says which.', 'Fix the poster as you would any photograph, then upload again.'],
          ['"The upload is larger than the 69206016 byte limit."', 'The file, or a video plus its poster, is far too big.', 'Export much smaller: photographs under 15 MB, videos under 50 MB.'],
          ['"That file was removed from the media library or is no longer ready to show. Choose it again."', 'On the Homepage, Mega menu cards and Size guides: the picture became unavailable while you saved.', 'Upload it again, then save again.'],
          ['"Describe it first, in a few words. Shoppers who use a screen reader hear this instead of seeing it."', 'The description box is empty.', 'Type a short description, then upload.'],
          ['"Your role cannot upload to the media library. Ask an administrator for the Media permission." (On the product form and the logo it reads "You do not have permission to do that."; on a Sale campaign, "You can manage campaigns but not upload pictures. Ask for the \'Media library\' permission as well.")', 'Your role lacks Media Create.', 'Roles cannot be edited in the admin. Ask a Super Admin to give you a role that has Media Create, such as Store Manager or Content Editor (see [Roles and permissions](#staff-roles)).'],
          ['"N photographs have no alt text. Describe each one..." (when putting a product on the shop)', 'A Gallery or Hover photograph has no description.', 'Type **Alt text** under each photograph, save, then try again.'],
          ['"That picture was removed from the media library, or is no longer a ready image, while this was being saved. Nothing was saved. Choose another picture and save again."', 'The picture became unavailable while you saved.', 'Upload it again and save again.'],
          ['"We could not reach the server. Check your connection and try again."', 'Your internet connection dropped. A big video on a slow connection can fail part-way.', 'Try again on a faster connection.'],
        ],
      },
      {
        kind: 'text',
        text: 'Some of these messages end with a reference to an internal document. Ignore that part; the first sentences tell you what to do.',
      },
      { kind: 'heading', text: 'It uploaded, but looks wrong' },
      {
        kind: 'list',
        items: [
          '**Cropped:** every place has a fixed shape (see [Sizes for every place](#photo-sizes)). Re-export the picture in that shape with the subject in the middle.',
          '**Blurry or soft:** the picture is smaller than the recommended size for that place, so it was stretched. Upload a larger one.',
          '**The homepage video does not play on a phone:** by design. Phones, and visitors who ask for less motion, see the still picture. Make sure the hero has a good still.',
          '**A picture still shows the old version:** a replaced picture is a new file. Make sure you saved the screen after replacing it.',
          '**The logo has a coloured rectangle behind it:** it is a JPEG. Use a PNG or WebP with a transparent background.',
        ],
      },
    ],
  },
];
