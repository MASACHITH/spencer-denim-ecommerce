# Hostinger Upload Guide — TU@SL Fashion

Follow these steps to publish the prototype to Hostinger.

## Steps

### 1. Zip the Project
- On Windows: right-click the `tuasl-fashion` folder → Send to → Compressed (zipped) folder
- Name it `tuasl-fashion.zip`

### 2. Open Hostinger hPanel
- Log in at [hpanel.hostinger.com](https://hpanel.hostinger.com)
- Select your hosting plan
- Click **File Manager** under the Files section

### 3. Navigate to `public_html`
- In File Manager, click the `public_html` folder to open it

### 4. Remove the Default Placeholder (if present)
- If `index.html` or `index.php` already exists (Hostinger default page), delete it
- Select the file → click Delete

### 5. Upload the ZIP
- Click **Upload Files** in File Manager
- Upload `tuasl-fashion.zip`

### 6. Extract the ZIP
- Once uploaded, right-click `tuasl-fashion.zip` → Extract
- In the extraction dialog, set the destination to `/public_html/` (not a subfolder)
- After extraction, `index.html` should be at `public_html/index.html` (not `public_html/tuasl-fashion/index.html`)

**If files landed inside a subfolder** (`public_html/tuasl-fashion/`):
- Select all files inside `tuasl-fashion/`
- Move them up one level to `public_html/`
- Delete the empty `tuasl-fashion/` folder

### 7. Verify File Structure
In `public_html/` you should see:
```
index.html
women.html
men.html
...
css/
js/
assets/
```

### 8. Connect the Domain
- In hPanel → Domains → Manage
- Point your domain to this hosting account
- DNS propagation can take up to 24–48 hours

### 9. Enable SSL (HTTPS)
- In hPanel → Security → SSL
- Enable the free Let's Encrypt SSL certificate for your domain
- Wait for activation (usually a few minutes)

### 10. Test All Pages
After the site is live, test:
- [ ] Homepage loads correctly at your domain
- [ ] All navigation links work
- [ ] Product pages load (e.g. `/product.html?id=1`)
- [ ] Search works
- [ ] Add to bag works
- [ ] Checkout demo completes
- [ ] Images load (or fallback placeholder shows)
- [ ] Mobile layout is correct
- [ ] Clear browser cache if you see old content (Ctrl+Shift+R)

## Troubleshooting

**Images not loading**: Check that the `assets/` folder was uploaded correctly and paths are relative (they should work automatically).

**CSS not loading**: Ensure `css/` folder is at the same level as `index.html`.

**JavaScript not working**: Check browser console (F12) for errors. Make sure all JS files are present in `js/`.

**Blank page**: Open browser developer tools → Console tab and check for errors. Usually a missing script file.
