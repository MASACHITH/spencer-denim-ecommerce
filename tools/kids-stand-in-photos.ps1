<#
.SYNOPSIS
    Gives the ten kids products a stand-in photograph borrowed from the nearest delivered adult
    garment, so the Kids section can be seen and tested before the real photographs arrive.

.DESCRIPTION
    TEST DATA. The client has not supplied kids photographs and they are still outstanding; this is
    the owner's decision of 2026-10-02 to stop waiting, not a delivery.

    What it does, and why in this order:

      1. Reads `tools/kids-stand-in-photos.csv` — the single authoritative mapping — and resolves
         each donor's delivered files in the client drop, undoing the two kinds of filename damage
         the delivery carries (a doubled `.jpg.jpg`, and a character lost off the front, as in
         `ourt-canvas-sneakers-2.jpg.jpg`). It resolves the same files `ClientAssetNaming` would.

      2. Runs `import-client-assets` over the **client's own drop** first. This is not a courtesy:
         it is the ordering guarantee. The media library deduplicates by SHA-256 under a real unique
         index, so a photograph is one asset however many files carry it — and the alt text lives on
         that one asset. Whichever import creates the asset writes its alt text, so if the stand-in
         drop went in first the shared asset would carry "Girls Cotton Sundress, front view" and the
         women's dress card would read it out too. Importing the delivery first means every donor
         asset already exists, every stand-in file comes back `Duplicate`, and the sentence on the
         asset stays the one that is true of the photograph. The run is idempotent, so doing it again
         costs nothing.

      3. Copies each donor file into a generated drop under the kids product's slug, and runs
         `import-client-assets` over that. Same bytes means the same checksum means the same asset:
         the outcome is one asset with two usages — the adult product and the kids product both
         pointing at the same photograph — which is what the library is built to do (doc 08 §3.3,
         `UX_MediaUsages_Asset_Owner_Role`).

    Nothing here changes the catalogue, adds a migration, or writes a row by hand. Every byte goes
    through `IMediaService` exactly as an admin upload does.

.PARAMETER ClientAssets
    The client's delivered drop — the folder holding `products/`, `departments/`, `home/`, `social/`.
    Probed next to and one level above the repository when not given.

.PARAMETER Out
    Where to build the generated stand-in drop. Defaults to `.local-kids-stand-ins/` in the
    repository, which is git-ignored. It is disposable: delete it any time, or pass -Remove.

.PARAMETER DryRun
    Passes `--dry-run` to both imports. Resolves and reports; stores nothing.

.PARAMETER NoImport
    Build the drop and print the mapping, but run neither import. For inspecting the files.

.PARAMETER Remove
    Delete the generated drop and stop. It does not undo an import — see README §2b for that.

.EXAMPLE
    ./tools/kids-stand-in-photos.ps1 -ClientAssets ../client-assets -DryRun
    ./tools/kids-stand-in-photos.ps1 -ClientAssets ../client-assets
#>
[CmdletBinding()]
param(
    [string] $ClientAssets,
    [string] $Out,
    [switch] $DryRun,
    [switch] $NoImport,
    [switch] $Remove
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$mappingFile = Join-Path $PSScriptRoot 'kids-stand-in-photos.csv'

<#
    **Every operator-supplied path is made absolute here, once, and nothing downstream sees the original.**

    `[System.IO.Path]::GetFullPath` resolves a relative path against `[Environment]::CurrentDirectory`.
    `Test-Path -LiteralPath` and `Remove-Item -LiteralPath` resolve it against PowerShell's `$PWD`. **
    `Set-Location` does not update `[Environment]::CurrentDirectory`**, so after a single `cd` those are
    two different folders — and a relative `-Out` then had the guard inspect one folder while the delete
    took another. Worse, when the folder the guard was shown did not exist it returned early at its
    "nothing there yet" check, so no content check ran at all and the delete took the real one. That is
    the round-1 defect with the guard walked straight past, and the trigger is the ordinary state of any
    interactive shell after one `cd`.

    `GetUnresolvedProviderPathFromPSPath` is PowerShell's own resolver, so it agrees with `-LiteralPath`
    by construction, and unlike `Resolve-Path` it works for a path that **does not exist yet** — which
    `-Out` usually does not. One value, used by the guard and by both deletes.
#>
function Resolve-OperatorPath {
    param([string] $Path)

    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

if (-not $Out) { $Out = Join-Path $repoRoot '.local-kids-stand-ins' }

$Out = Resolve-OperatorPath $Out
if ($ClientAssets) { $ClientAssets = Resolve-OperatorPath $ClientAssets }

# ---------------------------------------------------------------- the mapping

if (-not (Test-Path -LiteralPath $mappingFile)) {
    throw "The mapping is missing: $mappingFile"
}

# `#` comments carry the warning that this is test data, so they are stripped rather than banned.
$rows = Get-Content -LiteralPath $mappingFile |
    Where-Object { $_ -notmatch '^\s*#' -and $_.Trim().Length -gt 0 } |
    ConvertFrom-Csv

if (-not $rows) { throw "The mapping names no products: $mappingFile" }

# ---------------------------------------------------------------- the client drop

if (-not $ClientAssets) {
    foreach ($candidate in @(
            (Join-Path $repoRoot 'client-assets'),
            (Join-Path (Split-Path -Parent $repoRoot) 'client-assets'),
            (Join-Path (Split-Path -Parent (Split-Path -Parent $repoRoot)) 'client-assets'))) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'products')) { $ClientAssets = $candidate; break }
    }
}

# -Remove deletes a generated drop and reads no photograph, so it does not need the delivery. It is
# still probed for, because the guard below has to know where the delivery is in order to refuse to
# delete it -- which is the whole point.
# Already absolute (`Resolve-OperatorPath`, above). Resolving it a second time with `Resolve-Path`
# would reintroduce a second resolver for the same string, which is the bug this file just had.

if (-not $Remove -and (-not $ClientAssets -or -not (Test-Path -LiteralPath (Join-Path $ClientAssets 'products')))) {
    throw "Could not find the client's drop. Pass -ClientAssets <folder holding products/, home/, …>."
}

# ---------------------------------------------------------------- guard the destructive path

<#
    `-Out` is deleted recursively before the drop is rebuilt, and `-Remove` deletes it outright. That is
    the only destructive operation in this tool, and it takes an operator-supplied path.

    **Why it is guarded rather than trusted.** `client-assets/` is untracked in every repository here,
    so it is the project's ONLY copy of the photographs the client delivered. `-ClientAssets` and `-Out`
    are adjacent parameters, both take a path, and `-ClientAssets` is the one an operator always types.
    One transposition would have deleted the delivery with no prompt and exited 0 -- reproduced against
    a decoy folder before this existed. The file next door refuses to overwrite "a picture somebody
    chose" and exits 3 rather than guess, under its own comment that "a missed file is litter; a deleted
    file is data loss"; this had no guard at all.

    The rule is "everything here is mine", not "something here is mine": a folder somebody else has put
    a single file into is a folder somebody else is using. Refusing costs an operator one retyped
    argument; the alternative is unrecoverable.
#>

$contractFolders = @('products', 'departments', 'home', 'social')

# Every file name this mapping can produce. Both indexes for every row, because a donor without a
# second photograph yields only `-1` and that drop is still one of ours.
$mineFileNames = @(
    foreach ($row in $rows) { "$($row.KidsSlug)-1.jpg"; "$($row.KidsSlug)-2.jpg" }
)

function Get-ComparablePath {
    param([string] $Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

<#
    Whether $Inner is $Outer or sits beneath it. Case-insensitively even on Linux, which can only ever
    refuse a delete it might have allowed -- the safe direction for this particular question.
#>
function Test-PathWithin {
    param([string] $Outer, [string] $Inner)

    $o = Get-ComparablePath $Outer
    $i = Get-ComparablePath $Inner

    if ($i -eq $o) { return $true }
    return $i.StartsWith($o + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-SafeOutputFolder {
    param([string] $Path, [string] $Delivery, [string[]] $Mine)

    # The invariant that keeps the guard and the delete looking at the same folder. It cannot fail while
    # `Resolve-OperatorPath` runs first; it is asserted anyway, because the two drifting apart silently
    # is exactly how a relative `-Out` got past this function once already.
    if (-not [System.IO.Path]::IsPathRooted($Path)) {
        throw "Internal: Assert-SafeOutputFolder was given the relative path '$Path'. It must be resolved with Resolve-OperatorPath first, or the guard and the delete can disagree about which folder they mean."
    }

    $full = Get-ComparablePath $Path
    $refusal = "Refusing to delete '$full'"

    if ([string]::IsNullOrEmpty($full) -or $full -eq (Get-ComparablePath ([System.IO.Path]::GetPathRoot($full)))) {
        throw "$refusal`: it is a filesystem root."
    }

    if (Test-PathWithin -Outer $full -Inner $repoRoot) {
        throw "$refusal`: it is the repository, or contains it. Pass an -Out inside the repository, not over it."
    }

    if ($Delivery) {
        if (Test-PathWithin -Outer $full -Inner $Delivery) {
            throw "$refusal`: the client's delivery ('$Delivery') is inside it. That delivery is the only copy of the client's photographs and it is in no repository. Were -Out and -ClientAssets transposed?"
        }

        if (Test-PathWithin -Outer $Delivery -Inner $full) {
            throw "$refusal`: it is inside the client's delivery ('$Delivery'), the only copy of the client's photographs. Were -Out and -ClientAssets transposed?"
        }
    }

    if (-not (Test-Path -LiteralPath $full)) { return }

    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        throw "$refusal`: it is a file, not a folder."
    }

    # Everything directly inside must be one of the four folders the importer's contract names.
    foreach ($entry in Get-ChildItem -LiteralPath $full -Force) {
        if (-not $entry.PSIsContainer) {
            throw "$refusal`: it holds a file this tool never writes there ('$($entry.Name)'), so it is not a generated drop. Nothing was deleted."
        }

        if ($contractFolders -notcontains $entry.Name) {
            throw "$refusal`: it holds a folder this tool never creates ('$($entry.Name)'), so it is not a generated drop. Nothing was deleted."
        }
    }

    # `products/` may hold only the stand-ins this mapping names, and nothing nested.
    $products = Join-Path $full 'products'
    if (Test-Path -LiteralPath $products) {
        foreach ($entry in Get-ChildItem -LiteralPath $products -Force) {
            if ($entry.PSIsContainer) {
                throw "$refusal`: 'products/' holds a folder ('$($entry.Name)'), which no generated drop has. Nothing was deleted."
            }

            if ($Mine -notcontains $entry.Name) {
                throw "$refusal`: 'products/$($entry.Name)' is not a file this mapping produces, so this folder is not mine to delete. Nothing was deleted."
            }
        }
    }

    # The other three are created empty and stay empty.
    foreach ($folder in 'departments', 'home', 'social') {
        $path = Join-Path $full $folder
        if ((Test-Path -LiteralPath $path) -and (Get-ChildItem -LiteralPath $path -Force)) {
            throw "$refusal`: '$folder/' is not empty, and a generated drop leaves it empty. Nothing was deleted."
        }
    }
}

Assert-SafeOutputFolder -Path $Out -Delivery $ClientAssets -Mine $mineFileNames

if ($Remove) {
    if (Test-Path -LiteralPath $Out) {
        Remove-Item -LiteralPath $Out -Recurse -Force
        Write-Host "Removed $Out" -ForegroundColor Yellow
    }
    else {
        Write-Host "Nothing to remove at $Out"
    }
    Write-Host 'The import is not undone. To put the real photographs in, see README section 2b.'
    return
}

$deliveredProducts = Join-Path $ClientAssets 'products'

# Every delivered product file, with its media extensions stripped — the same stem the importer
# resolves against. `.jpg.jpg` and `.jpg` both reduce to the same name.
$delivered = @{}
foreach ($file in Get-ChildItem -LiteralPath $deliveredProducts -File) {
    $stem = $file.Name
    while ($stem -match '\.(jpe?g|png|webp|mp4)$') { $stem = $stem -replace '\.(jpe?g|png|webp|mp4)$', '' }
    if (-not $delivered.ContainsKey($stem)) { $delivered[$stem] = @() }
    $delivered[$stem] += $file
}

<#
    The delivered file for a donor's photograph N, undoing the delivery's damage the same way
    `ClientAssetNaming.ParseProductFile` does — because the file the importer placed on the adult
    product is the file whose bytes the kids product has to borrow, and nothing else will do.

    `products` run into the slug, and up to four characters lost off the front: `court-canvas-
    sneakers-2` was delivered as `ourt-canvas-sneakers-2.jpg.jpg`, and that is the hover image of
    the one donor that is an exact match. An ambiguous near-miss is refused rather than guessed,
    which is the importer's rule too.
#>
function Resolve-DonorFile {
    param([string] $DonorSlug, [int] $Index)

    $wanted = "$DonorSlug-$Index"

    foreach ($stem in @($wanted, "products$wanted")) {
        if ($delivered.ContainsKey($stem)) {
            if ($delivered[$stem].Count -gt 1) {
                throw "'$stem' was delivered more than once under different extensions; nothing is guessed."
            }
            return $delivered[$stem][0]
        }
    }

    $near = @($delivered.Keys | Where-Object {
            $_.Length -lt $wanted.Length -and ($wanted.Length - $_.Length) -le 4 -and $wanted.EndsWith($_)
        })

    if ($near.Count -eq 1) { return $delivered[$near[0]][0] }
    if ($near.Count -gt 1) {
        throw "'$wanted' is the end of $($near.Count) delivered names ($($near -join ', ')). A near miss with more than one answer is a guess."
    }

    return $null
}

# ---------------------------------------------------------------- build the drop

# Guarded by Assert-SafeOutputFolder above, which has already refused anything that is not either
# absent or a generated drop of this mapping's own files.
if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }

# The importer fails on a missing folder rather than importing half a drop, so all four exist. The
# three that are empty import nothing: a missing home or department file is a note, never a
# withdrawal, and `ApplySectionSwitches` only ever switches a section *on*.
foreach ($folder in @('products', 'departments', 'home', 'social')) {
    New-Item -ItemType Directory -Path (Join-Path $Out $folder) -Force | Out-Null
}

$plan = [System.Collections.Generic.List[object]]::new()
$missing = [System.Collections.Generic.List[string]]::new()

foreach ($row in $rows) {
    $copied = @()

    foreach ($index in 1, 2) {
        $source = Resolve-DonorFile -DonorSlug $row.DonorSlug -Index $index

        if (-not $source) {
            if ($index -eq 1) {
                $missing.Add("$($row.KidsSlug): the delivery has no $($row.DonorSlug)-1, so it has no card image to borrow.")
            }
            continue
        }

        # One `.jpg`, not the delivered `.jpg.jpg`: the doubling was damage, not the contract. The
        # pipeline reads the signature and ignores the name either way.
        $target = Join-Path (Join-Path $Out 'products') "$($row.KidsSlug)-$index.jpg"
        Copy-Item -LiteralPath $source.FullName -Destination $target -Force
        $copied += [pscustomobject]@{ Index = $index; From = $source.Name; To = Split-Path -Leaf $target }
    }

    $plan.Add([pscustomobject]@{ Row = $row; Files = $copied })
}

# ---------------------------------------------------------------- say what it is

$line = '-' * 100
Write-Host ''
Write-Host 'KIDS STAND-IN PHOTOGRAPHS — TEST DATA. The real kids photographs are still outstanding.' -ForegroundColor Yellow
Write-Host $line
Write-Host ("{0,-26} {1,-28} {2,-6} {3}" -f 'KIDS PRODUCT', 'BORROWS', 'MATCH', 'FILES')
Write-Host $line

foreach ($entry in $plan) {
    $files = if ($entry.Files) { ($entry.Files | ForEach-Object { "$($_.From) -> $($_.To)" }) -join '; ' } else { '(none)' }
    $colour = switch ($entry.Row.Quality) {
        'none' { 'Red' }
        'poor' { 'Red' }
        'fair' { 'Yellow' }
        default { 'Gray' }
    }
    Write-Host ("{0,-26} {1,-28} {2,-6} {3}" -f $entry.Row.KidsSlug, $entry.Row.DonorSlug, $entry.Row.Quality, $files) -ForegroundColor $colour
}

Write-Host $line
Write-Host ''
Write-Host 'Why each one, and which ones are a poor match: tools/kids-stand-in-photos.csv.'
Write-Host 'Alt text is NOT rewritten. The photograph is one asset shared by both products, and the'
Write-Host 'one sentence on it describes the garment that is actually in the picture — so a kids'
Write-Host 'PRODUCT PAGE reads out the adult garment''s name. That is true of the image, and the'
Write-Host 'mismatch with the heading is the disclosure. The LISTING says nothing either way: a'
Write-Host 'product card''s image is decorative (alt="" under aria-hidden, because it repeats the link'
Write-Host 'on the product name), so no alt text of any kind reaches it. See README §2b.'

$worst = @($plan | Where-Object { $_.Row.Quality -in @('poor', 'none') })
if ($worst) {
    Write-Host ''
    Write-Host 'NOT A GOOD MATCH — recorded as a gap, not as a photograph:' -ForegroundColor Red
    foreach ($entry in $worst) {
        Write-Host "  $($entry.Row.KidsProduct) <- $($entry.Row.DonorProduct) [$($entry.Row.Quality)]" -ForegroundColor Red
        Write-Host "      $($entry.Row.Why)"
    }
}

if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host 'NOT STOOD IN FOR — the delivery has no file for the donor:' -ForegroundColor Red
    foreach ($note in $missing) { Write-Host "  $note" -ForegroundColor Red }
}

Write-Host ''
Write-Host "Generated drop: $Out"

if ($NoImport) {
    Write-Host 'Nothing imported (-NoImport).'
    return
}

# ---------------------------------------------------------------- import, delivery first

$api = Join-Path $repoRoot 'src/backend/SpencerDenim.Api'
$common = @('--project', $api, '--no-launch-profile', '--', 'import-client-assets')
$flags = @()
if ($DryRun) { $flags += '--dry-run' }

# Announced rather than run from a function: a function that both prints the import's output and
# returns its exit code returns *both* on the success stream, so `$code` came back as an array of
# ninety report lines and `$code -ne 0` was a filter rather than a test. It threw on a clean import.
function Write-ImportHeader {
    param([string] $From, [string] $Label)

    Write-Host ''
    Write-Host "==> $Label" -ForegroundColor Cyan
    Write-Host "    import-client-assets --from $From $($flags -join ' ')"
}

# Step 2: the delivery. Idempotent, and it is what guarantees every donor asset — and its alt text —
# already exists before a stand-in file is offered to the library.
Write-ImportHeader -From $ClientAssets -Label "The client's delivery (so the donor assets exist first)"
& dotnet run @common '--from' $ClientAssets @flags
$code = $LASTEXITCODE

# 3 is "a slot already held a picture somebody chose", which is about the delivery's own slots and
# does not stop the donors from being in the library. 2 and 4 mean nothing landed.
if ($code -ne 0 -and $code -ne 3) {
    throw "Importing the client's drop exited $code. The stand-ins were not imported: without the donor assets in the library, the stand-in run would create them under the kids products' alt text."
}

Write-ImportHeader -From $Out -Label 'The kids stand-ins'
& dotnet run @common '--from' $Out @flags
$code = $LASTEXITCODE

if ($code -ne 0 -and $code -ne 3) {
    throw "Importing the stand-ins exited $code."
}

Write-Host ''
if ($code -eq 3) {
    Write-Host 'Exit 3: a kids slot already held a picture somebody chose — most likely these stand-ins' -ForegroundColor Yellow
    Write-Host 'already applied, or the real photographs already in. Re-run with --replace only if you' -ForegroundColor Yellow
    Write-Host 'mean to overwrite what is there.' -ForegroundColor Yellow
}
elseif ($DryRun) {
    # Said separately, because a dry run has changed nothing and must not claim a page now shows
    # anything. "0 planned" above is also a count of refusals NOT looked for, as the importer says.
    Write-Host 'DRY RUN: nothing was stored. The report above says where each file would land.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Both steps were dry runs, so step 1 committed nothing for step 2 to find. Step 2 read the' -ForegroundColor Yellow
    Write-Host 'library as it stands today: where the donor photograph is not in it yet, the alt text is' -ForegroundColor Yellow
    Write-Host 'marked PROVISIONAL, because which sentence the shared asset ends up carrying is decided by' -ForegroundColor Yellow
    Write-Host 'whichever run creates it. A real step 1 changes that answer. Run this again afterwards to' -ForegroundColor Yellow
    Write-Host 'see the sentences the shop would actually carry.' -ForegroundColor Yellow
}
else {
    Write-Host 'Done. The Kids listing and the ten kids product pages now show borrowed photographs.' -ForegroundColor Green
}

Write-Host ''
Write-Host 'WHEN THE REAL KIDS PHOTOGRAPHS ARRIVE:' -ForegroundColor Cyan
Write-Host '  1. Put them in the client drop as <kids-slug>-1.jpg and <kids-slug>-2.jpg.'
Write-Host "  2. dotnet run --project src/backend/SpencerDenim.Api --no-launch-profile -- ``"
Write-Host "       import-client-assets --from $ClientAssets --replace"
Write-Host '     --replace is required and is the whole reversal: a stand-in is a picture somebody'
Write-Host '     chose, so the importer refuses to overwrite it silently (exit 3) until you ask.'
Write-Host '  3. Delete this folder and this script''s rows for the products that now have their own:'
Write-Host "       ./tools/kids-stand-in-photos.ps1 -Remove"
