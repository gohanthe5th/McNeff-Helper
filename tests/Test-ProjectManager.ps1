param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [string]$CaptureTool,
    [string]$RenderPath
)

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($Executable))
if ('GameArt.RecipePhotoCorner.png' -notin $assembly.GetManifestResourceNames()) {
    throw 'The executable is missing the blueprint preview corner artwork.'
}
$trainerType = $assembly.GetType('TrainerForm', $true)
$editorType = $assembly.GetType('WalletEditor', $true)
$editor = [Activator]::CreateInstance($editorType, @($editorType.GetMethod('FindGameDll').Invoke($null, @())))
$live = @($editor.FindSaveSlots() | Where-Object { $_.Path })[0]
if (-not $live.Path) { throw 'A test source save is required.' }

$wallet = Get-Item -LiteralPath $live.Path
$session = $wallet.Directory.Parent
$profile = $session.Parent.Parent.Name
$originalInventoryPath = Join-Path $session.FullName 'block_inventory\block_inventory.data'
$originalWalletHash = (Get-FileHash -LiteralPath $live.Path).Hash
$originalInventoryHash = (Get-FileHash -LiteralPath $originalInventoryPath).Hash
$originalBuildsPath = Join-Path $session.FullName 'builds\builds.data'
$originalBuildsHash = (Get-FileHash -LiteralPath $originalBuildsPath).Hash
$originalProgressionPath = Join-Path $session.FullName 'progression\progression.data'
$originalProgressionHash = (Get-FileHash -LiteralPath $originalProgressionPath).Hash
$originalProgressionRecoveryPath = $originalProgressionPath + '.bak'
$originalProgressionRecoveryHash = if ([IO.File]::Exists($originalProgressionRecoveryPath)) {
    (Get-FileHash -LiteralPath $originalProgressionRecoveryPath).Hash
}
$base = Join-Path $PSScriptRoot ('q-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$target = Join-Path $base ('WeStealFromOldPeople\save\cloud\users\' + $profile + '\sessions\' + $session.Name)
$liveAppData = $env:APPDATA
$form = $null

function StringHash([string]$Path) {
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path))
}

function Read-SaveSchema([string]$Path) {
    [BitConverter]::ToUInt32([IO.File]::ReadAllBytes($Path), 8)
}

function Read-SubStatRows($List) {
    $rows = @{}
    foreach ($row in $List.Items) {
        if ($row.SubItems.Count -gt 1) { $rows[$row.Text] = $row.SubItems[1].Text }
    }
    $rows
}

function Assert-RecipeOrder($List, $RarityField, [int]$RarityDirection) {
    $comparer = [StringComparer]::CurrentCultureIgnoreCase
    $previousName = $null
    $previousRank = 0
    foreach ($row in $List.Items) {
        $rank = $RarityField.GetValue($row.Tag)
        $rankComparison = ($rank - $previousRank) * $RarityDirection
        if ($null -ne $previousName -and ($rankComparison -lt 0 -or
            ($rankComparison -eq 0 -and $comparer.Compare($previousName,$row.Text) -gt 0))) {
            throw "Recipes are out of order at '$previousName' and '$($row.Text)'."
        }
        $previousName = $row.Text
        $previousRank = $rank
    }
}

$privateInstance = [Reflection.BindingFlags]'NonPublic,Instance'
$privateStatic = [Reflection.BindingFlags]'NonPublic,Static'

function Read-InstructionFixture([string]$Path) {
    $instructions = $editorType.GetMethod('ReadRecipeInstructions',$privateInstance).Invoke(
        $editor.PSObject.BaseObject,[object[]]@($Path.PSObject.BaseObject))
    $type = $instructions.GetType()
    $raw = $type.GetField('Payload',$privateInstance).GetValue($instructions)
    $arrayStart = [int]$type.GetField('ArrayStart',$privateInstance).GetValue($instructions)
    $recordsStart = [int]$type.GetField('RecordsStart',$privateInstance).GetValue($instructions)
    $recordsEnd = [int]$type.GetField('RecordsEnd',$privateInstance).GetValue($instructions)
    [pscustomobject]@{
        Save = $type.GetField('Save',$privateInstance).GetValue($instructions)
        Compressed = $type.GetField('Compressed',$privateInstance).GetValue($instructions)
        Ids = $type.GetField('Ids',$privateInstance).GetValue($instructions)
        Prefix = [Convert]::ToBase64String($raw,0,$arrayStart)
        Records = [Convert]::ToBase64String($raw,$recordsStart,$recordsEnd-$recordsStart)
        Tail = [Convert]::ToBase64String($raw,$recordsEnd,$raw.Length-$recordsEnd)
    }
}

function Set-InstructionFixture([string]$Path, [int[]]$Ids) {
    $instructions = Read-InstructionFixture $Path
    $payload = [IO.MemoryStream]::new()
    try {
        $prefix = [Convert]::FromBase64String($instructions.Prefix)
        $tail = [Convert]::FromBase64String($instructions.Tail)
        $payload.Write($prefix,0,$prefix.Length)
        $payload.WriteByte(0x90 + $Ids.Length)
        foreach ($id in $Ids) {
            $ticks = [BitConverter]::GetBytes([UInt64][DateTime]::UtcNow.Ticks)
            [Array]::Reverse($ticks)
            # MessagePack [recipe ID, empty mod source, UTC ticks].
            $record = [byte[]]@(0x93,0xcd,($id -shr 8),($id -band 255),0xa0,0xcf)
            $payload.Write($record,0,$record.Length)
            $payload.Write($ticks,0,$ticks.Length)
        }
        $payload.Write($tail,0,$tail.Length)
        $raw = $payload.ToArray()
    } finally { $payload.Dispose() }

    $encoded = if ($instructions.Compressed) {
        $wrap = $assembly.GetType('BrickPack',$true).GetMethod('Wrap',$privateStatic)
        $wrap.Invoke($null,[object[]]@(,$raw))
    } else { $raw }
    [void]$editorType.GetMethod('ReplaceSave',$privateStatic).Invoke(
        $null,[object[]]@($instructions.Save.PSObject.BaseObject,[byte[]]$encoded))
}

try {
    foreach ($name in @('wallet', 'block_inventory', 'manifest', 'builds', 'progression')) {
        $relative = $name + '\' + $name + '.data'
        $file = Join-Path $target $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file))
        [IO.File]::Copy((Join-Path $session.FullName $relative), $file)
    }
    $env:APPDATA = $base
    $slot = @($editor.FindSaveSlots() | Where-Object { $_.Path })[0]
    $walletPath = Join-Path $target 'wallet\wallet.data'
    $inventoryPath = Join-Path $target 'block_inventory\block_inventory.data'
    if ($slot.Path -ne $walletPath -or $slot.Path -eq $live.Path) { throw 'Expected only the scratch wallet.' }

    $progressionPath = Join-Path $target 'progression\progression.data'
    $progressionRecoveryPath = $progressionPath + '.bak'
    [IO.File]::Copy($progressionPath,$progressionRecoveryPath)
    $primaryProgressionSchema = Read-SaveSchema $progressionPath
    $recoveryProgressionSchema = Read-SaveSchema $progressionRecoveryPath
    $sourceInstructions = Read-InstructionFixture $progressionPath
    $schemaSource = [IO.File]::ReadAllBytes($progressionPath)
    $crc32 = $editorType.GetMethod('Crc32',$privateStatic)
    foreach ($schema in @(2, 3)) {
        $schemaPath = Join-Path $base ('progression-schema-' + $schema + '.data')
        $schemaBytes = [byte[]]$schemaSource.Clone()
        [Buffer]::BlockCopy([BitConverter]::GetBytes([uint32]$schema),0,$schemaBytes,8,4)
        $checksum = [uint32]$crc32.Invoke($null,[object[]]@($schemaBytes,($schemaBytes.Length - 4)))
        [Buffer]::BlockCopy([BitConverter]::GetBytes($checksum),0,$schemaBytes,$schemaBytes.Length - 4,4)
        [IO.File]::WriteAllBytes($schemaPath,$schemaBytes)
        $schemaBefore = StringHash $schemaPath
        $schemaInstructions = Read-InstructionFixture $schemaPath
        if (!$schemaInstructions.Ids.SetEquals($sourceInstructions.Ids) -or
            (Read-SaveSchema $schemaPath) -ne $schema -or (StringHash $schemaPath) -ne $schemaBefore) {
            throw 'A verified progression schema did not read the same instructions without modifying the fixture.'
        }
    }
    # Dry Stone Wall starts unlocked. Castanets is a separate recovery-only
    # record to verify that each copy keeps its own prior instructions.
    Set-InstructionFixture $progressionPath @(265)
    Set-InstructionFixture $progressionRecoveryPath @(265,144)

    # A real save can already have an unfinished playmat decision. Prepare an empty
    # pending position in the scratch copy only so the grant path stays testable.
    if ($editor.ReadBuildOwnership($walletPath).HasPendingBuild) {
        $snapshot = $editorType.GetMethod('OpenBuildInventory',$privateInstance).Invoke(
            $editor.PSObject.BaseObject,[object[]]@($walletPath.PSObject.BaseObject))
        $snapshotType = $snapshot.GetType()
        $rawBytes = $snapshotType.GetField('Raw',$privateInstance).GetValue($snapshot)
        $pendingOffset = [int]$snapshotType.GetField('PendingOffset',$privateInstance).GetValue($snapshot)
        $rawWithoutPending = [byte[]]::new($pendingOffset + 1)
        [Buffer]::BlockCopy($rawBytes, 0, $rawWithoutPending, 0, $pendingOffset)
        if ($snapshotType.GetField('Compressed',$privateInstance).GetValue($snapshot)) {
            $wrap = $assembly.GetType('BrickPack',$true).GetMethod('Wrap',$privateStatic)
            $payload = $wrap.Invoke($null,[object[]]@(,$rawWithoutPending))
        } else { $payload = $rawWithoutPending }
        $saveContent = $snapshotType.GetField('Save',$privateInstance).GetValue($snapshot)
        [void]$editorType.GetMethod('ReplaceSave',$privateStatic).Invoke(
            $null,[object[]]@($saveContent.PSObject.BaseObject,$payload.PSObject.BaseObject))
    }

    $instance = [Reflection.BindingFlags]'NonPublic,Instance,DeclaredOnly'
    $reader = $trainerType.GetMethod('ReadBuildCatalog',[Reflection.BindingFlags]'NonPublic,Static')
    $catalog = @($reader.Invoke($null, @()))
    if ($catalog.Length -ne 981) { throw 'The executable must contain all 981 build recipes.' }
    $vault = @($catalog | Where-Object { $_.ToString() -eq 'Vault Door' })
    if ($vault.Length -ne 1) { throw 'Expected a unique Vault Door recipe.' }
    $raw = $vault[0].GetType().GetField('Ingredients',$instance).GetValue($vault[0])
    $expected = @{}
    foreach ($item in $raw) {
        $itemType = $item.GetType()
        $expected[$itemType.GetField('Id',$instance).GetValue($item).ToString()] =
            [int]$itemType.GetField('Count',$instance).GetValue($item)
    }
    if ($expected.Count -ne 7 -or ($expected.Values | Measure-Object -Sum).Sum -ne 34) {
        throw 'Vault Door did not match the shipped seven-variant, 34-piece recipe.'
    }

    $shortId = @($expected.Keys)[0]
    [void]$editor.SetColoredBrick($walletPath, $shortId, 0)
    $before = @{}
    foreach ($brick in $editor.ReadColoredBricks($walletPath)) { $before[$brick.Id] = $brick.Count }
    $previousInventory = StringHash $inventoryPath
    $walletBytes = StringHash $walletPath
    $inventoryFolder = [IO.Path]::GetDirectoryName($inventoryPath)
    $backupCount = @(Get-ChildItem -LiteralPath $inventoryFolder -Filter 'backup-*.bak' -File).Count

    $type = $trainerType.GetNestedType('ProjectManagerForm',[Reflection.BindingFlags]'NonPublic')
    $constructor = $type.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance')[0]
    $form = $constructor.Invoke([object[]]@($editor.PSObject.BaseObject,$slot.PSObject.BaseObject))
    if ($form.Text -ne 'Project Manager | McNeff Helper') { throw 'The Project Manager window title was not updated.' }
    $search = $type.GetField('search',$instance).GetValue($form)
    $recipes = $type.GetField('recipes',$instance).GetValue($form)
    [void]$recipes.Handle
    $materials = $type.GetField('materials',$instance).GetValue($form)
    $note = $type.GetField('note',$instance).GetValue($form)
    $addButton = $type.GetField('addMissing',$instance).GetValue($form)
    $buildButton = $type.GetField('addBuild',$instance).GetValue($form)
    $effect = $type.GetField('effectTitle',$instance).GetValue($form)
    $effectDescription = $type.GetField('effectDescription',$instance).GetValue($form)
    $heading = $type.GetField('heading',$instance).GetValue($form)
    $rarity = $type.GetField('rarity',$instance).GetValue($form)
    $earnings = $type.GetField('earnings',$instance).GetValue($form)
    $preview = $type.GetField('recipePreview',$instance).GetValue($form)
    $ownership = $type.GetField('ownershipLabel',$instance).GetValue($form)
    $recipeFilter = $type.GetField('recipeFilter',$instance).GetValue($form)
    $recipeSort = $type.GetField('recipeSort',$instance).GetValue($form)
    $rarityField = $catalog[0].GetType().GetField('Rarity',$instance)
    $subStats = $type.GetField('subStats',$instance).GetValue($form)
    if (@($subStats.Parent.Controls | Where-Object { $_ -is [System.Windows.Forms.ComboBox] }).Count) {
        throw 'The sub-stat panel still contains a layout chooser.'
    }
    $recipeStatus = $type.GetField('recipeStatus',$instance).GetValue($form)
    $selectAllButton = $type.GetField('selectAllRecipes',$instance).GetValue($form)
    $selectAllClick = $type.GetMethod('OnSelectAllRecipes',$instance)
    $unlockButton = $type.GetField('unlockRecipe',$instance).GetValue($form)
    $unlockClick = $type.GetMethod('OnUnlockRecipe',$instance)
    [void]$type.GetMethod('RefreshOwned',$instance).Invoke($form,@())
    $paintPreview = $type.GetMethod('DrawRecipePreview',$instance)
    $sizes = @($preview.Size, [Drawing.Size]::new(190,182))
    $originalDock = $preview.Dock
    try {
        $preview.Dock = [Windows.Forms.DockStyle]::None
        foreach ($recipeName in @('Armoured Train','Double Dutch')) {
            $search.Text = $recipeName
            if ($recipes.SelectedItems[0].Text -ne $recipeName) { throw "Could not select $recipeName for preview." }
            foreach ($size in $sizes) {
                $preview.Size = $size
                $surface = [Drawing.Bitmap]::new($size.Width,$size.Height)
                $canvas = [Drawing.Graphics]::FromImage($surface)
                $bounds = [Drawing.Rectangle]::new([Drawing.Point]::Empty,$size)
                $paintEvent = [Windows.Forms.PaintEventArgs]::new($canvas,$bounds)
                try {
                    # Direct invocation exposes paint failures WinForms would render as a red X.
                    [void]$paintPreview.Invoke($form,[object[]]@($preview,$paintEvent))
                    if ($surface.GetPixel([int]($size.Width / 2),[int]($size.Height / 2)).A -eq 0) {
                        throw "The $recipeName blueprint drew an empty preview."
                    }
                }
                finally {
                    $paintEvent.Dispose()
                    $canvas.Dispose()
                    $surface.Dispose()
                }
            }
        }
    }
    finally {
        $preview.Size = $sizes[0]
        $preview.Dock = $originalDock
        $form.PerformLayout()
    }
    $search.Text = 'Acrobat'
    if ($recipes.SelectedItems[0].Text -ne 'Acrobat' -or $rarity.Text -ne 'Epic' -or $earnings.Text -ne 'EARNS  $26' -or
        $heading.Font.FontFamily.Name -ne 'VT323' -or $effect.Text -notlike '+3 Jump Count*' -or
        $effectDescription.Text -ne 'How many times you can jump before landing.' -or
        $preview.AccessibleName -ne 'Acrobat build preview') {
        throw 'Acrobat did not show the shipped title font, effect, tier, and build preview.'
    }
    $search.Text = 'Double Dutch'
    if ($recipes.SelectedItems[0].Text -ne 'Double Dutch') { throw 'Could not select the Double Dutch recipe.' }
    $shownStats = Read-SubStatRows $subStats
    if ($shownStats.Count -ne 3 -or $shownStats['Attack Range'] -ne '+50%' -or
        $shownStats['Health Regeneration'] -ne '+7.15' -or $shownStats['Defense'] -ne '+7%') {
        throw 'Double Dutch sub-stats differed from the game card.'
    }
    $search.Text = ''
    if ($recipes.Items.Count -ne $catalog.Length -or $recipeSort.SelectedIndex -ne 0 -or
        $recipeSort.Items.Count -ne 3 -or $recipeSort.Items[0] -ne ('Name A{0}Z' -f [char]0x2013) -or
        $recipeSort.Items[1] -ne 'Rarity highest first' -or $recipeSort.Items[2] -ne 'Rarity lowest first') {
        throw 'The full recipe list did not offer the default name sort and both rarity orders.'
    }
    Assert-RecipeOrder $recipes $rarityField 0

    $recipeSort.SelectedIndex = 1
    if ($recipes.Items.Count -ne $catalog.Length -or $recipes.SelectedItems[0].Text -ne 'Double Dutch' -or
        $rarityField.GetValue($recipes.Items[0].Tag) -ne 4 -or
        $rarityField.GetValue($recipes.Items[$recipes.Items.Count-1].Tag) -ne 0) {
        throw 'Highest rarity sort did not keep the catalog and selected recipe.'
    }
    Assert-RecipeOrder $recipes $rarityField -1

    $recipeSort.SelectedIndex = 2
    if ($recipes.Items.Count -ne $catalog.Length -or $recipes.SelectedItems[0].Text -ne 'Double Dutch' -or
        $rarityField.GetValue($recipes.Items[0].Tag) -ne 0 -or
        $rarityField.GetValue($recipes.Items[$recipes.Items.Count-1].Tag) -ne 4) {
        throw 'Lowest rarity sort did not keep the catalog and selected recipe.'
    }
    Assert-RecipeOrder $recipes $rarityField 1

    $recipeFilter.SelectedIndex = 3
    if ($recipeFilter.SelectedItem -ne 'Unavailable' -or $recipes.Items.Count -ne 41 -or
        @($recipes.Items | Where-Object { $_.SubItems[1].Text -ne 'Unavailable' -or $_.Checked }).Count -ne 0) {
        throw 'The Unavailable filter did not show exactly the 41 disabled recipes.'
    }
    Assert-RecipeOrder $recipes $rarityField 1
    $search.Text = 'Albatross'
    if ($recipes.Items.Count -ne 1 -or $recipes.SelectedItems[0].Text -ne 'Albatross' -or
        $unlockButton.Enabled -or $selectAllButton.Enabled -or $buildButton.Enabled) {
        throw 'Search and edit controls did not respect the Unavailable filter.'
    }
    $recipeFilter.SelectedIndex = 0
    $recipeSort.SelectedIndex = 0
    $search.Text = 'an impossible recipe name zzzzzz'
    if ($recipes.Items.Count -ne 0 -or $materials.Rows.Count -ne 0 -or $addButton.Enabled -or
        $selectAllButton.Enabled -or $unlockButton.Enabled) {
        throw 'Empty search results did not clear the project.'
    }
    $search.Text = 'Vault Door'
    if ($recipes.Items.Count -le 0 -or $recipes.SelectedItems[0].Text -ne 'Vault Door' -or
        $materials.Rows.Count -ne 7 -or -not $addButton.Enabled) {
        throw "Project search or missing-brick display failed. $($note.Text)"
    }
    if ($effect.Text -notlike '*Safe Cracker*' -or $effect.Text -notlike '*5 s*') {
        throw 'Vault Door effect did not match the game data.'
    }
    $expectedMissing = 0
    foreach ($id in $expected.Keys) { $expectedMissing += [Math]::Max(0, $expected[$id] - $before[$id]) }
    $shownMissing = ($materials.Rows | ForEach-Object { [int]$_.Cells['Missing'].Value } | Measure-Object -Sum).Sum
    if ($shownMissing -ne $expectedMissing) { throw 'Grid shortage does not match the saved inventory.' }

    $click = $type.GetMethod('OnAddMissing',$instance)
    [void]$click.Invoke($form, @($addButton,[EventArgs]::Empty))
    if ($addButton.Enabled -or $note.Text -notlike 'Added the missing bricks*') {
        throw "The project did not become ready. $($note.Text)"
    }
    foreach ($brick in $editor.ReadColoredBricks($walletPath)) {
        $wanted = $before[$brick.Id]
        if ($expected.ContainsKey($brick.Id)) { $wanted = [Math]::Max($wanted, $expected[$brick.Id]) }
        if ($brick.Count -ne $wanted) { throw 'Project changed a surplus or unrelated brick.' }
    }
    $backups = @(Get-ChildItem -LiteralPath $inventoryFolder -Filter 'backup-*.bak' -File)
    if ($backups.Count -ne ($backupCount + 1) -or
        @($backups | Where-Object { (StringHash $_.FullName) -eq $previousInventory }).Count -eq 0) {
        throw 'Project edit failed to create exactly one original-byte backup.'
    }
    [void]$click.Invoke($form, @($addButton,[EventArgs]::Empty))
    if (@(Get-ChildItem -LiteralPath $inventoryFolder -Filter 'backup-*.bak' -File).Count -ne $backups.Count -or
        (StringHash $walletPath) -ne $walletBytes) {
        throw 'A ready project performed a redundant write or changed the wallet.'
    }
    $buildsPath = Join-Path $target 'builds\builds.data'
    $buildsFolder = [IO.Path]::GetDirectoryName($buildsPath)
    $beforeBuild = StringHash $buildsPath
    $beforeColored = StringHash $inventoryPath
    $beforeBuildBackups = @(Get-ChildItem -LiteralPath $buildsFolder -Filter 'backup-*.bak' -File).Count
    $buildClick = $type.GetMethod('OnAddBuild',$instance)
    $beforeProgression = StringHash $progressionPath
    $beforeRecovery = StringHash $progressionRecoveryPath
    $search.Text = 'Back Scratcher'
    if ($recipes.SelectedItems[0].Text -ne 'Back Scratcher' -or $selectAllButton.Enabled -or
        $buildButton.Enabled -or $unlockButton.Enabled -or $recipeStatus.Text -ne 'Unavailable in this game version') {
        throw 'Disabled recipe can be added or unlocked.'
    }
    [void]$selectAllClick.Invoke($form,@($selectAllButton,[EventArgs]::Empty))
    $recipes.SelectedItems[0].Checked = $true
    if ($recipes.SelectedItems[0].Checked -or $unlockButton.Enabled) {
        throw 'Disabled recipe could be queued.'
    }
    [void]$buildClick.Invoke($form,@($buildButton,[EventArgs]::Empty))
    [void]$unlockClick.Invoke($form,@($unlockButton,[EventArgs]::Empty))
    if ((StringHash $buildsPath) -ne $beforeBuild) { throw 'Disabled recipe changed the build save.' }
    if ((StringHash $progressionPath) -ne $beforeProgression -or
        (StringHash $progressionRecoveryPath) -ne $beforeRecovery) {
        throw 'Disabled recipe changed progression.'
    }

    $search.Text = 'Aardvark'
    if ($recipes.SelectedItems[0].Text -ne 'Aardvark' -or $effect.Text -notlike '+60% Detect Old People*' -or
        -not $buildButton.Enabled) {
        throw "Aardvark did not show its effect and unowned status. $($ownership.Text)"
    }
    $shownStats = Read-SubStatRows $subStats
    if ($shownStats.Count -ne 2 -or $shownStats['Health Regeneration'] -ne '+8.45' -or $shownStats['Defense'] -ne '+2%') {
        throw 'Unowned Aardvark did not show its recipe sub-stats.'
    }
    [void]$buildClick.Invoke($form,@($buildButton,[EventArgs]::Empty))
    $newOwnership = $editor.ReadBuildOwnership($walletPath)
    if (!$newOwnership.HasPendingBuild -or $newOwnership.PendingRecipeId -ne 1 -or
        !$newOwnership.OwnedRecipeIds.Contains(1) -or $buildButton.Enabled -or
        $ownership.Text -ne 'Awaiting placement at the lair playmat') {
        throw "Add Build did not queue ownership and show progress. $($note.Text)"
    }
    $shownStats = Read-SubStatRows $subStats
    if ($shownStats.Count -ne 2 -or $shownStats['Health Regeneration'] -ne '+8.45' -or $shownStats['Defense'] -ne '+2%') {
        throw 'The pending Aardvark did not show its computed sub-stats.'
    }

    # A generated copy initially matches its recipe. Change a brick in memory
    # so this check distinguishes rendering the saved copy from the catalog.
    $pendingPlacements = $type.GetField('builds',$instance).GetValue($form).GetOwnedCopies(1)[0].Placements
    $firstPlacement = $pendingPlacements[0]
    $renderRecipe = $type.GetMethod('RenderRecipe',$instance)
    try {
        $pendingPlacements[0] = [Activator]::CreateInstance($firstPlacement.GetType(),
            [object[]]@(1,8,$firstPlacement.X,$firstPlacement.Y,$firstPlacement.Z,$firstPlacement.Rotation))
        [void]$renderRecipe.Invoke($form,@())
        if ((Read-SubStatRows $subStats)['Attack Range'] -ne '+4%') {
            throw 'The panel did not automatically use the pending copy when its bricks differed from the recipe.'
        }
    } finally {
        $pendingPlacements[0] = $firstPlacement
        [void]$renderRecipe.Invoke($form,@())
    }

    $afterBuild = StringHash $buildsPath
    [void]$buildClick.Invoke($form,@($buildButton,[EventArgs]::Empty))
    $buildBackups = @(Get-ChildItem -LiteralPath $buildsFolder -Filter 'backup-*.bak' -File)
    if ($buildBackups.Count -ne ($beforeBuildBackups + 1) -or
        @($buildBackups | Where-Object { (StringHash $_.FullName) -eq $beforeBuild }).Count -ne 1 -or
        (StringHash $buildsPath) -ne $afterBuild -or
        (StringHash $inventoryPath) -ne $beforeColored -or (StringHash $walletPath) -ne $walletBytes) {
        throw 'Build grant or retry changed unrelated data or created an incorrect backup.'
    }

    $search.Text = ''
    $recipeFilter.SelectedIndex = 2
    if ($recipes.Items.Count -ne 1 -or $recipes.SelectedItems[0].Text -ne 'Dry Stone Wall' -or
        $recipeStatus.Text -ne 'Recipe unlocked' -or -not $recipes.SelectedItems[0].Checked -or
        $selectAllButton.Enabled -or $unlockButton.Enabled) {
        throw "Unlocked filter did not show the pre-existing recipe as checked. $($recipeStatus.Text)"
    }
    [void]$selectAllClick.Invoke($form,@($selectAllButton,[EventArgs]::Empty))
    $recipes.SelectedItems[0].Checked = $false
    if (-not $recipes.SelectedItems[0].Checked -or $unlockButton.Enabled) {
        throw 'The check for an already known recipe was mutable.'
    }
    $recipeFilter.SelectedIndex = 1
    $search.Text = 'Dry Stone Wall'
    if ($recipes.Items.Count -ne 0 -or $selectAllButton.Enabled -or $unlockButton.Enabled) {
        throw 'Locked filter included a known recipe.'
    }
    $search.Text = 'Aardvark'
    if ($recipes.Items.Count -ne 1 -or $recipes.SelectedItems[0].Text -ne 'Aardvark' -or
        $recipeStatus.Text -ne 'Recipe locked' -or $recipes.SelectedItems[0].Checked -or
        $unlockButton.Text -ne 'Quick Unlock' -or -not $unlockButton.Enabled) {
        throw "The locked recipe should stay unchecked even though its build is pending. $($recipeStatus.Text)"
    }

    $oldInstruction = Read-InstructionFixture $progressionPath
    $oldRecoveryInstruction = Read-InstructionFixture $progressionRecoveryPath
    $progressionFolder = [IO.Path]::GetDirectoryName($progressionPath)
    $beforeProgressionBackups = @(Get-ChildItem -LiteralPath $progressionFolder -Filter 'backup-*.bak' -File).Count
    $recipes.SelectedItems[0].Checked = $true
    if ($unlockButton.Text -ne 'Unlock 1 Checked') { throw 'Checking Aardvark did not queue it.' }
    $search.Text = 'Castanets'
    if ($recipes.Items.Count -ne 1 -or $recipes.SelectedItems[0].Text -ne 'Castanets' -or
        $recipes.SelectedItems[0].Checked -or -not $selectAllButton.Enabled) {
        throw 'Select All should be available for an unchecked locked recipe in the current search.'
    }
    [void]$selectAllClick.Invoke($form,@($selectAllButton,[EventArgs]::Empty))
    if (-not $recipes.SelectedItems[0].Checked -or $unlockButton.Text -ne 'Unlock 2 Checked') {
        throw 'Select All did not queue the visible recipe while preserving the hidden Aardvark check.'
    }
    $search.Text = 'Aardvark'
    if (-not $recipes.SelectedItems[0].Checked -or $unlockButton.Text -ne 'Unlock 2 Checked') {
        throw 'Checked recipe IDs were lost when switching search results.'
    }
    $search.Text = ''
    $recipeSort.SelectedIndex = 1
    if ($recipeFilter.SelectedItem -ne 'Locked' -or $recipes.SelectedItems[0].Text -ne 'Aardvark' -or
        -not $recipes.SelectedItems[0].Checked -or $unlockButton.Text -ne 'Unlock 2 Checked' -or
        -not $selectAllButton.Enabled -or
        @($recipes.Items | Where-Object { $_.SubItems[1].Text -ne 'Locked' }).Count -ne 0 -or
        @($recipes.Items | Where-Object { $_.Checked -and $_.Text -eq 'Castanets' }).Count -ne 1) {
        throw 'Rarity sorting changed the active filter, selected recipe, or queued checks.'
    }
    Assert-RecipeOrder $recipes $rarityField -1
    $recipeSort.SelectedIndex = 0
    $search.Text = 'Acrobat'
    if ($recipes.SelectedItems[0].Text -ne 'Acrobat' -or $recipes.SelectedItems[0].Checked -or
        $unlockButton.Text -ne 'Unlock 2 Checked' -or -not $unlockButton.Enabled) {
        throw 'The active row should stay unchecked without replacing the queued recipes.'
    }
    if ((StringHash $progressionPath) -ne $beforeProgression -or (StringHash $progressionRecoveryPath) -ne $beforeRecovery -or
        @(Get-ChildItem -LiteralPath $progressionFolder -Filter 'backup-*.bak' -File).Count -ne $beforeProgressionBackups) {
        throw 'Select All or an individual check wrote to the copied save before Quick Unlock.'
    }

    [void]$unlockClick.Invoke($form,@($unlockButton,[EventArgs]::Empty))
    if ($recipeFilter.SelectedIndex -ne 0 -or $recipes.SelectedItems[0].Text -ne 'Acrobat' -or
        $recipeStatus.Text -ne 'Recipe locked' -or $recipes.SelectedItems[0].Checked -or
        $unlockButton.Text -ne 'Quick Unlock' -or -not $unlockButton.Enabled -or $note.Text -notlike '2 recipes unlocked*') {
        throw "Batch unlock should keep the unrelated selected recipe locked and clear the queue. $($note.Text)"
    }

    $newInstruction = Read-InstructionFixture $progressionPath
    $newRecoveryInstruction = Read-InstructionFixture $progressionRecoveryPath
    if ($newInstruction.Ids.Count -ne 3 -or !$newInstruction.Ids.Contains(1) -or
        !$newInstruction.Ids.Contains(265) -or !$newInstruction.Ids.Contains(144) -or
        $newRecoveryInstruction.Ids.Count -ne 3 -or
        !$newRecoveryInstruction.Ids.Contains(1) -or !$newRecoveryInstruction.Ids.Contains(265) -or
        !$newRecoveryInstruction.Ids.Contains(144)) {
        throw 'Unlock did not preserve and extend each progression copy independently.'
    }
    foreach ($pair in @(
        @{ Old = $oldInstruction; New = $newInstruction },
        @{ Old = $oldRecoveryInstruction; New = $newRecoveryInstruction }
    )) {
        $priorRecords = [Convert]::FromBase64String($pair.Old.Records)
        $newRecords = [Convert]::FromBase64String($pair.New.Records)
        if ($pair.Old.Prefix -ne $pair.New.Prefix -or $pair.Old.Tail -ne $pair.New.Tail -or
            $newRecords.Length -le $priorRecords.Length -or
            [Convert]::ToBase64String($newRecords,0,$priorRecords.Length) -ne $pair.Old.Records) {
            throw 'Unlock changed existing recipe records or unrelated progression bytes.'
        }
    }

    $afterProgression = StringHash $progressionPath
    $afterRecovery = StringHash $progressionRecoveryPath
    $progressionBackups = @(Get-ChildItem -LiteralPath $progressionFolder -Filter 'backup-*.bak' -File)
    if ($progressionBackups.Count -ne ($beforeProgressionBackups + 2) -or
        @($progressionBackups | Where-Object { (StringHash $_.FullName) -eq $beforeProgression }).Count -ne 1 -or
        @($progressionBackups | Where-Object { (StringHash $_.FullName) -eq $beforeRecovery }).Count -ne 1 -or
        (StringHash $buildsPath) -ne $afterBuild -or (StringHash $inventoryPath) -ne $beforeColored -or
        (StringHash $walletPath) -ne $walletBytes) {
        throw 'Unlock failed to preserve unrelated saves or make exact backups.'
    }
    $recipeFilter.SelectedIndex = 2
    if ($recipes.Items.Count -ne 0 -or $unlockButton.Enabled) {
        throw 'Unmarked Acrobat was unexpectedly unlocked.'
    }
    $search.Text = 'Aardvark'
    if ($recipes.Items.Count -ne 1 -or $recipes.SelectedItems[0].Text -ne 'Aardvark' -or
        -not $recipes.SelectedItems[0].Checked -or $recipeStatus.Text -ne 'Recipe unlocked' -or $unlockButton.Enabled) {
        throw 'Newly unlocked recipe did not appear checked in the Unlocked filter.'
    }
    [void]$unlockClick.Invoke($form,@($unlockButton,[EventArgs]::Empty))
    if ((StringHash $progressionPath) -ne $afterProgression -or
        (StringHash $progressionRecoveryPath) -ne $afterRecovery -or
        @(Get-ChildItem -LiteralPath $progressionFolder -Filter 'backup-*.bak' -File).Count -ne $progressionBackups.Count) {
        throw 'Re-unlocking a known recipe performed a redundant edit.'
    }
    $recipeFilter.SelectedIndex = 1
    $search.Text = 'Acrobat'
    if ($recipes.Items.Count -ne 1 -or $recipes.SelectedItems[0].Text -ne 'Acrobat' -or
        $recipes.SelectedItems[0].Checked -or $unlockButton.Text -ne 'Quick Unlock' -or -not $unlockButton.Enabled) {
        throw 'Quick Unlock did not offer the selected-row shortcut without checked recipes.'
    }
    [void]$unlockClick.Invoke($form,@($unlockButton,[EventArgs]::Empty))
    $finalInstruction = Read-InstructionFixture $progressionPath
    $finalRecovery = Read-InstructionFixture $progressionRecoveryPath
    if ($recipeFilter.SelectedIndex -ne 0 -or $recipes.SelectedItems[0].Text -ne 'Acrobat' -or
        -not $recipes.SelectedItems[0].Checked -or $recipeStatus.Text -ne 'Recipe unlocked' -or $unlockButton.Enabled -or
        $finalInstruction.Ids.Count -ne 4 -or !$finalInstruction.Ids.Contains(2) -or
        $finalRecovery.Ids.Count -ne 4 -or !$finalRecovery.Ids.Contains(2)) {
        throw "Quick Unlock did not unlock only the active recipe. $($note.Text)"
    }

    $recipeFilter.SelectedIndex = 0
    $search.Text = ''
    $recipeType = $catalog[0].GetType()
    $enabledField = $recipeType.GetField('Enabled',$instance)
    $idField = $recipeType.GetField('Id',$instance)
    [int[]]$enabledIds = @(
        foreach ($recipe in $catalog) {
            if ($enabledField.GetValue($recipe)) { $idField.GetValue($recipe) }
        }
    )
    $lockedCount = @($recipes.Items | Where-Object { $_.SubItems[1].Text -eq 'Locked' }).Count
    if ($recipes.Items.Count -ne $catalog.Length -or $lockedCount -eq 0 -or
        $lockedCount -ne ($enabledIds.Length - $finalInstruction.Ids.Count) -or -not $selectAllButton.Enabled) {
        throw 'The full recipe view did not show every eligible locked recipe.'
    }

    $beforeAllPrimary = StringHash $progressionPath
    $beforeAllRecovery = StringHash $progressionRecoveryPath
    $beforeAllBackups = @(Get-ChildItem -LiteralPath $progressionFolder -Filter 'backup-*.bak' -File).Count
    [void]$selectAllClick.Invoke($form,@($selectAllButton,[EventArgs]::Empty))
    foreach ($row in $recipes.Items) {
        if ($row.Checked -ne [bool]$enabledField.GetValue($row.Tag)) {
            throw "Select All left the wrong check state for '$($row.Text)'."
        }
    }
    if ($selectAllButton.Enabled -or -not $unlockButton.Enabled -or
        $unlockButton.Text -ne "Unlock $lockedCount Checked" -or
        (StringHash $progressionPath) -ne $beforeAllPrimary -or
        (StringHash $progressionRecoveryPath) -ne $beforeAllRecovery) {
        throw 'Full-catalog Select All did not queue the visible locked recipes without editing the save.'
    }

    [void]$unlockClick.Invoke($form,@($unlockButton,[EventArgs]::Empty))
    $allPrimary = Read-InstructionFixture $progressionPath
    $allRecovery = Read-InstructionFixture $progressionRecoveryPath
    $allBackups = @(Get-ChildItem -LiteralPath $progressionFolder -Filter 'backup-*.bak' -File)
    if (!$allPrimary.Ids.SetEquals($enabledIds) -or !$allRecovery.Ids.SetEquals($enabledIds) -or
        $allBackups.Count -ne ($beforeAllBackups + 2) -or
        $selectAllButton.Enabled -or $unlockButton.Enabled -or $unlockButton.Text -ne 'Quick Unlock') {
        throw "Full-catalog unlock did not unlock exactly the enabled recipes in both saves. $($note.Text)"
    }
    if ((Read-SaveSchema $progressionPath) -ne $primaryProgressionSchema -or
        (Read-SaveSchema $progressionRecoveryPath) -ne $recoveryProgressionSchema) {
        throw 'Recipe unlocks changed a primary or recovery progression schema.'
    }

    if ((Get-FileHash -LiteralPath $live.Path).Hash -ne $originalWalletHash -or
        (Get-FileHash -LiteralPath $originalInventoryPath).Hash -ne $originalInventoryHash -or
        (Get-FileHash -LiteralPath $originalBuildsPath).Hash -ne $originalBuildsHash -or
        (Get-FileHash -LiteralPath $originalProgressionPath).Hash -ne $originalProgressionHash -or
        ([IO.File]::Exists($originalProgressionRecoveryPath) -and
            (Get-FileHash -LiteralPath $originalProgressionRecoveryPath).Hash -ne $originalProgressionRecoveryHash)) {
        throw 'The live save changed during verification.'
    }

    if ($CaptureTool) {
        [IO.File]::Copy($originalBuildsPath, $buildsPath, $true)
        [IO.File]::Copy($originalProgressionPath, $progressionPath, $true)
        $first = $catalog[0]
        $firstIngredient = $first.GetType().GetField('Ingredients',$instance).GetValue($first)[0]
        [void]$editor.SetColoredBrick($walletPath, $firstIngredient.GetType().GetField('Id',$instance).GetValue($firstIngredient).ToString(), 0)
        $env:APPDATA = $liveAppData
        & $CaptureTool $Executable $base $RenderPath --project-manager
        if ($LASTEXITCODE -ne 0) { throw 'The Project Manager could not be captured.' }
    }
    if ($RenderPath -and -not $CaptureTool) {
        $recipeFilter.SelectedIndex = 0
        $search.Text = 'Double Dutch'
        [void]$type.GetMethod('RefreshOwned',$instance).Invoke($form,@())
        $form.PerformLayout()
        $image = [Drawing.Bitmap]::new($form.Width, $form.Height)
        try {
            $form.DrawToBitmap($image, [Drawing.Rectangle]::new(0, 0, $image.Width, $image.Height))
            function Draw-ProjectControls($parent, [int]$x, [int]$y) {
                $parent.PerformLayout()
                foreach ($child in $parent.Controls) {
                    $left = $x + $child.Left
                    $top = $y + $child.Top
                    $bounds = [Drawing.Rectangle]::new($left, $top, $child.Width, $child.Height)
                    if ($bounds.Width -gt 0 -and $bounds.Height -gt 0) {
                        $child.DrawToBitmap($image, $bounds)
                    }
                    if ($child -is [Windows.Forms.Panel]) { Draw-ProjectControls $child $left $top }
                }
            }
            Draw-ProjectControls $form 8 31
            $image.Save($RenderPath,[Drawing.Imaging.ImageFormat]::Png)
        } finally { $image.Dispose() }
    }
    'Game recipe art and text, Double Dutch sub-stats, rarity sorting, Add Build, Select All, recipe unlocks, filters, exact backups, and duplicate protection passed on a copied save.'
} finally {
    if ($null -ne $form) { $form.Dispose() }
    $env:APPDATA = $liveAppData
    if (Test-Path -LiteralPath $base) {
        $scratch = (Resolve-Path -LiteralPath $base).ProviderPath
        if ($scratch -eq $base -and (Split-Path -Parent $scratch) -eq $PSScriptRoot) {
            Remove-Item -LiteralPath $scratch -Recurse
        }
    }
}
