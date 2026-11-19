param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$RenderPath
)

$ErrorActionPreference = 'Stop'
$source = Get-ChildItem -LiteralPath (Join-Path $env:APPDATA 'WeStealFromOldPeople\save\cloud\users') -Filter wallet.data -File -Recurse |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $source) { throw 'Expected one or more local save wallets.' }
$originalWallet = $source.FullName
$originalInventory = Join-Path $source.Directory.Parent.FullName 'block_inventory\block_inventory.data'
$originalManifest = Join-Path $source.Directory.Parent.FullName 'manifest\manifest.data'
function Read-BytesHash([string]$Path) {
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path))
}
$oldWallet = Read-BytesHash $originalWallet
$oldInventory = Read-BytesHash $originalInventory
$oldManifest = Read-BytesHash $originalManifest

$liveAppData = $env:APPDATA
$base = Join-Path $PSScriptRoot ('p-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$profileId = $source.Directory.Parent.Parent.Parent.Name
$sessions = Join-Path $base "WeStealFromOldPeople\save\cloud\users\$profileId\sessions"
$copyRoot = Join-Path $sessions $source.Directory.Parent.Name
$walletDirectory = Join-Path $copyRoot 'wallet'
$inventoryDirectory = Join-Path $copyRoot 'block_inventory'
[void][IO.Directory]::CreateDirectory($walletDirectory)
[void][IO.Directory]::CreateDirectory($inventoryDirectory)
$wallet = Join-Path $walletDirectory 'wallet.data'
$inventory = Join-Path $inventoryDirectory 'block_inventory.data'
[IO.File]::Copy($originalWallet, $wallet, $true)
[IO.File]::Copy($originalInventory, $inventory, $true)
$manifest = Join-Path $copyRoot 'manifest\manifest.data'
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($manifest))
[IO.File]::Copy($originalManifest, $manifest, $true)
$env:APPDATA = $base

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($Executable))
$type = $assembly.GetType('TrainerForm', $true)
$flags = [Reflection.BindingFlags]'NonPublic, Instance, DeclaredOnly'
$form = [Activator]::CreateInstance($type)
$arguments = @($form, [EventArgs]::Empty)
$editorType = $assembly.GetType('WalletEditor', $true)

function Copy-WithSessionId([string]$From, [string]$To, [string]$SessionId, $Crc32) {
    $bytes = [IO.File]::ReadAllBytes($From)
    $headerEnd = 36 + [int][BitConverter]::ToUInt32($bytes, 12) + [int][BitConverter]::ToUInt32($bytes, 16)
    $idBytes = [Text.Encoding]::UTF8.GetBytes($SessionId)
    if ([BitConverter]::ToUInt64($bytes, $headerEnd) -ne $idBytes.Length) {
        throw 'The copy does not have the expected fixed-width session ID.'
    }
    [Buffer]::BlockCopy($idBytes, 0, $bytes, $headerEnd + 48, $idBytes.Length)
    $sum = [uint32]$Crc32.Invoke($null, [object[]]@($bytes, ($bytes.Length - 4)))
    [Buffer]::BlockCopy([BitConverter]::GetBytes($sum), 0, $bytes, $bytes.Length - 4, 4)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($To))
    [IO.File]::WriteAllBytes($To, $bytes)
}

function Write-PackString([IO.Stream]$Stream, [string]$Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    if ($bytes.Length -lt 32) {
        $Stream.WriteByte([byte](0xa0 + $bytes.Length))
    } else {
        $Stream.WriteByte(0xd9)
        $Stream.WriteByte([byte]$bytes.Length)
    }
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Set-FixtureManifest($Editor, [string]$Path, [string]$SessionId, [uint64]$Created, [uint64]$LastPlayed) {
    $reader = $editorType.GetMethod('ReadSave', [Reflection.BindingFlags]'NonPublic, Instance')
    $writer = $editorType.GetMethod('ReplaceSave', [Reflection.BindingFlags]'NonPublic, Static')
    $numberWriter = $assembly.GetType('BrickPack', $true).GetMethod('WriteUInt', [Reflection.BindingFlags]'NonPublic, Static')
    $save = $reader.Invoke($Editor, [object[]]@($Path, 'manifest'))
    $plain = [IO.MemoryStream]::new()
    try {
        $plain.WriteByte(0x97)
        Write-PackString $plain $SessionId
        Write-PackString $plain 'Fixture'
        [void]$numberWriter.Invoke($null, [object[]]@($plain, $Created))
        [void]$numberWriter.Invoke($null, [object[]]@($plain, $LastPlayed))
        [byte[]]$playtime = @(0xcb, 0, 0, 0, 0, 0, 0, 0, 0)
        $plain.Write($playtime, 0, $playtime.Length)
        Write-PackString $plain '0.1.3'
        $plain.WriteByte(0x90)
        [void]$writer.Invoke($null, [object[]]@($save, $plain.ToArray()))
    } finally {
        $plain.Dispose()
    }
}

function Test-AdditionalSlots($Editor) {
    $crc32 = $editorType.GetMethod('Crc32', [Reflection.BindingFlags]'NonPublic, Static')
    # Vary ID, modification, and play order independently from the creation ranks.
    $secondId = 'fffffffffffffffffffffffffffffff2'
    $thirdId = '00000000000000000000000000000003'
    $created = [DateTime]::UtcNow.AddDays(1)
    foreach ($spec in @(
        @{ Id = $secondId; Created = $created.Ticks; LastPlayed = $created.AddHours(4).Ticks },
        @{ Id = $thirdId; Created = $created.AddHours(1).Ticks; LastPlayed = $created.AddHours(2).Ticks }
    )) {
        $root = Join-Path $sessions $spec.Id
        $cloneManifest = Join-Path $root 'manifest\manifest.data'
        Copy-WithSessionId $originalWallet (Join-Path $root 'wallet\wallet.data') $spec.Id $crc32
        Copy-WithSessionId $originalInventory (Join-Path $root 'block_inventory\block_inventory.data') $spec.Id $crc32
        Copy-WithSessionId $originalManifest $cloneManifest $spec.Id $crc32
        Set-FixtureManifest $Editor $cloneManifest $spec.Id $spec.Created $spec.LastPlayed
    }
    $secondRoot = Join-Path $sessions $secondId
    $thirdRoot = Join-Path $sessions $thirdId
    $secondWallet = Join-Path $secondRoot 'wallet\wallet.data'
    $secondInventory = Join-Path $secondRoot 'block_inventory\block_inventory.data'
    $thirdWallet = Join-Path $thirdRoot 'wallet\wallet.data'
    $thirdInventory = Join-Path $thirdRoot 'block_inventory\block_inventory.data'
    [IO.File]::SetLastWriteTimeUtc($secondWallet, [DateTime]::UtcNow.AddHours(-3))
    [IO.File]::SetLastWriteTimeUtc($thirdWallet, [DateTime]::UtcNow)
    $beforeFirstWallet = Read-BytesHash $wallet
    $beforeFirstInventory = Read-BytesHash $inventory
    $beforeSecondInventory = Read-BytesHash $secondInventory
    $beforeThirdWallet = Read-BytesHash $thirdWallet

    [void]$type.GetMethod('OnRefreshSaves', $flags).Invoke($form, $arguments)
    $refreshedPicker = $type.GetField('savePicker', $flags).GetValue($form)
    if ($refreshedPicker.Items.Count -lt 3 -or
        $refreshedPicker.Items[1].Path -ne $secondWallet -or $refreshedPicker.Items[2].Path -ne $thirdWallet) {
        throw 'Reload did not discover Save 2 and Save 3 on the open trainer.'
    }

    $additionalForm = [Activator]::CreateInstance($type)
    $additionalEvents = @($additionalForm, [EventArgs]::Empty)
    try {
        [void]$type.GetMethod('OnShown', $flags).Invoke($additionalForm, $additionalEvents)
        $picker = $type.GetField('savePicker', $flags).GetValue($additionalForm)
        $currentStatus = $type.GetField('status', $flags).GetValue($additionalForm)
        $expected = @($wallet, $secondWallet, $thirdWallet)
        if ($picker.Items.Count -lt $expected.Count) {
            throw "Expected three editable saves. $($currentStatus.Text)"
        }
        for ($index = 0; $index -lt $expected.Count; $index++) {
            $item = $picker.Items[$index]
            if ($item.Number -ne ($index + 1) -or !$item.Exists -or $item.Path -ne $expected[$index]) {
                throw "Save $($index + 1) did not use its manifest creation order. $($currentStatus.Text)"
            }
        }

        $picker.SelectedIndex = 1
        $greyField = $type.GetField('greyCount', $flags).GetValue($additionalForm)
        $beforeSecondGrey = $Editor.Read($secondWallet).Count
        $nextGrey = if ($beforeSecondGrey -eq 54321) { 67890 } else { 54321 }
        $greyField.Text = [string]$nextGrey
        [void]$type.GetMethod('OnSetGrey', $flags).Invoke($additionalForm, $additionalEvents)
        if ($Editor.Read($secondWallet).Count -ne $nextGrey) {
            throw "Save 2 did not get the Grey Blocks edit. $($currentStatus.Text)"
        }
        $afterSecondWallet = Read-BytesHash $secondWallet

        $picker.SelectedIndex = 2
        $fillInput = $type.GetField('allBrickCount', $flags).GetValue($additionalForm)
        $fillInput.Value = 217
        [void]$type.GetMethod('OnFillBricks', $flags).Invoke($additionalForm, $additionalEvents)
        $thirdBricks = $Editor.ReadColoredBricks($thirdWallet)
        if ($thirdBricks.Count -ne 100 -or @($thirdBricks | Where-Object Count -ne 217).Count -ne 0) {
            throw "Save 3 did not get the colored brick edit. $($currentStatus.Text)"
        }
        if ((Read-BytesHash $wallet) -ne $beforeFirstWallet -or
            (Read-BytesHash $inventory) -ne $beforeFirstInventory -or
            (Read-BytesHash $secondInventory) -ne $beforeSecondInventory -or
            (Read-BytesHash $thirdWallet) -ne $beforeThirdWallet -or
            (Read-BytesHash $secondWallet) -ne $afterSecondWallet) {
            throw 'A numbered save action changed a wallet or inventory outside its selection.'
        }

        $needed = $thirdBricks[0]
        $surplus = $thirdBricks[1]
        $readInventory = $editorType.GetMethod('ReadInventory', [Reflection.BindingFlags]'NonPublic, Instance')
        $writeInventory = $editorType.GetMethod('WriteInventory', [Reflection.BindingFlags]'NonPublic, Static')
        $fixture = $readInventory.Invoke($Editor.PSObject.BaseObject, [object[]]@($thirdWallet.PSObject.BaseObject))
        $held = $fixture.GetType().GetField('Held', [Reflection.BindingFlags]'NonPublic, Instance').GetValue($fixture)
        if (!$held.Remove([uint64]$needed.Id)) { throw 'Could not prepare the missing project brick fixture.' }
        [void]$writeInventory.Invoke($null, [object[]]@($fixture.PSObject.BaseObject))
        $beforeGrant = Read-BytesHash $thirdInventory
        $thirdInventoryFolder = [IO.Path]::GetDirectoryName($thirdInventory)
        $beforeGrantBackups = @(Get-ChildItem -LiteralPath $thirdInventoryFolder -Filter '*.bak' -File).Count
        $requirements = [Collections.Generic.Dictionary[uint64, int]]::new()
        $requirements.Add([uint64]$needed.Id, 8)
        $requirements.Add([uint64]$surplus.Id, 2)
        $requirements.Add([uint64]1, 1)
        try {
            [void]$Editor.EnsureColoredBricks($thirdWallet, $requirements)
            throw 'An unknown project brick ID was accepted.'
        } catch {
            if ($_.Exception.InnerException -isnot [ArgumentException]) { throw }
        }
        if ((Read-BytesHash $thirdInventory) -ne $beforeGrant -or
            @(Get-ChildItem -LiteralPath $thirdInventoryFolder -Filter '*.bak' -File).Count -ne $beforeGrantBackups) {
            throw 'Invalid project requirements changed the inventory.'
        }
        [void]$requirements.Remove([uint64]1)
        $grantBackup = $Editor.EnsureColoredBricks($thirdWallet, $requirements)
        if (!$grantBackup -or (Read-BytesHash $grantBackup) -ne $beforeGrant) {
            throw 'The project grant did not preserve the original inventory in its backup.'
        }
        $granted = $Editor.ReadColoredBricks($thirdWallet)
        foreach ($brick in $granted) {
            $expectedCount = if ($brick.Id -eq $needed.Id) { 8 } else { 217 }
            if ($brick.Count -ne $expectedCount) {
                throw 'The project grant did not fill only the missing brick quantity.'
            }
        }
        $afterGrant = Read-BytesHash $thirdInventory
        if ($Editor.EnsureColoredBricks($thirdWallet, $requirements) -or
            (Read-BytesHash $thirdInventory) -ne $afterGrant -or
            @(Get-ChildItem -LiteralPath $thirdInventoryFolder -Filter '*.bak' -File).Count -ne ($beforeGrantBackups + 1) -or
            (Read-BytesHash $inventory) -ne $beforeFirstInventory -or
            (Read-BytesHash $secondInventory) -ne $beforeSecondInventory -or
            (Read-BytesHash $thirdWallet) -ne $beforeThirdWallet) {
            throw 'A repeated project grant or an unrelated save changed.'
        }

        $afterThirdInventory = Read-BytesHash $thirdInventory
        Set-FixtureManifest $Editor (Join-Path $thirdRoot 'manifest\manifest.data') $thirdId 1 $created.Ticks
        $greyField.Text = if ($Editor.Read($thirdWallet).Count -eq 31415) { '27182' } else { '31415' }
        [void]$type.GetMethod('OnSetGrey', $flags).Invoke($additionalForm, $additionalEvents)
        if ($currentStatus.Text -notmatch 'Save slots changed' -or
            (Read-BytesHash $thirdWallet) -ne $beforeThirdWallet -or
            (Read-BytesHash $secondWallet) -ne $afterSecondWallet -or
            (Read-BytesHash $wallet) -ne $beforeFirstWallet -or
            (Read-BytesHash $thirdInventory) -ne $afterThirdInventory) {
            throw "A stale numbered selection was not rejected safely. $($currentStatus.Text)"
        }
    } finally {
        $additionalForm.Dispose()
    }
    'Save 1/2/3 manifest ranks, isolated edits, recipe shortage grants, and stale slot rejection verified.'
}

try {
    [void]$type.GetMethod('OnShown', $flags).Invoke($form, $arguments)
    $slotPicker = $type.GetField('savePicker', $flags).GetValue($form)
    $slot = $slotPicker.SelectedItem
    if ($null -eq $slot -or $slot.Number -ne 1 -or !$slot.Exists -or $slot.Path -ne $wallet) {
        throw 'Expected the scratch save to be selected as Save 1.'
    }
    if ($slotPicker.Items.Count -ne 3) { throw 'Expected exactly three numbered save slots.' }
    for ($index = 1; $index -lt 3; $index++) {
        $item = $slotPicker.Items[$index]
        if ($item.Number -ne ($index + 1) -or $item.Exists -or $null -ne $item.Path) {
            throw 'Expected Save 2 and Save 3 to be marked empty.'
        }
    }

    $editor = $type.GetField('editor', $flags).GetValue($form)
    $greyInput = $type.GetField('greyCount', $flags).GetValue($form)
    $colorPicker = $type.GetField('colorPicker', $flags).GetValue($form)
    $brickPicker = $type.GetField('brickPicker', $flags).GetValue($form)
    $brickInput = $type.GetField('brickCount', $flags).GetValue($form)
    $allBrickInput = $type.GetField('allBrickCount', $flags).GetValue($form)
    $status = $type.GetField('status', $flags).GetValue($form)
    foreach ($empty in @(1, 2)) {
        $slotPicker.SelectedIndex = $empty
        if ($greyInput.Enabled -or $brickInput.Enabled -or $allBrickInput.Enabled) {
            throw "An empty save slot enabled edits. $($status.Text)"
        }
    }
    $slotPicker.SelectedIndex = 0
    $catalog = $editor.ReadColoredBricks($wallet)
    if ($catalog.Count -ne 100 -or $brickPicker.Items.Count -ne 100) {
        throw 'Expected a 100-piece colored inventory. ' + $status.Text
    }

    $maxGrey = 9999999
    if ($greyInput -isnot [System.Windows.Forms.TextBox]) {
        throw 'Expected a text box for entering the exact Grey Blocks count.'
    }
    $originalGrey = $editor.Read($wallet)
    try {
        [void]$editor.Set($originalGrey, $maxGrey + 1)
        throw 'The editor accepted a Grey Blocks count above 9,999,999.'
    } catch {
        if ($_.Exception.InnerException -isnot [ArgumentOutOfRangeException]) { throw }
    }
    if ((Read-BytesHash $wallet) -ne $oldWallet) { throw 'The rejected count changed the wallet.' }

    $backupsBeforeInvalid = @(Get-ChildItem -LiteralPath $walletDirectory -Filter '*.bak' -File).Count
    foreach ($invalidGreyText in @('', ' ', 'invalid', '1.5', '-1', '10000000')) {
        $greyInput.Text = $invalidGreyText
        [void]$type.GetMethod('OnSetGrey', $flags).Invoke($form, $arguments)
        if ((Read-BytesHash $wallet) -ne $oldWallet -or
            (Read-BytesHash $inventory) -ne $oldInventory -or
            @(Get-ChildItem -LiteralPath $walletDirectory -Filter '*.bak' -File).Count -ne $backupsBeforeInvalid) {
            throw "Invalid Grey Blocks input [$invalidGreyText] changed a save or wrote a backup. $($status.Text)"
        }
    }

    if ($originalGrey.Count -eq 1000) {
        $greyInput.Text = '1001'
        [void]$type.GetMethod('OnSetGrey', $flags).Invoke($form, $arguments)
        if ($editor.Read($wallet).Count -ne 1001) { throw 'Could not prepare a distinct Grey Blocks count for the test.' }
    }
    $customGrey = 1000
    $greyInput.Text = '1000'
    [void]$type.GetMethod('OnSetGrey', $flags).Invoke($form, $arguments)
    if ($editor.Read($wallet).Count -ne $customGrey -or (Read-BytesHash $inventory) -ne $oldInventory) {
        throw 'The custom Grey Blocks count did not read back or changed the colored inventory. ' + $status.Text
    }
    $walletBackup = Get-ChildItem -LiteralPath $walletDirectory -Filter '*.bak' -File |
        Where-Object { (Read-BytesHash $_.FullName) -eq $oldWallet } | Select-Object -First 1
    if ($null -eq $walletBackup) { throw 'Original wallet backup was missing.' }

    $customWallet = Read-BytesHash $wallet
    [void]$type.GetMethod('OnMaxGrey', $flags).Invoke($form, $arguments)
    if ($editor.Read($wallet).Count -ne $maxGrey -or (Read-BytesHash $inventory) -ne $oldInventory) {
        throw 'The max Grey Blocks count did not read back or changed the colored inventory. ' + $status.Text
    }
    $customWalletBackup = Get-ChildItem -LiteralPath $walletDirectory -Filter '*.bak' -File |
        Where-Object { (Read-BytesHash $_.FullName) -eq $customWallet } | Select-Object -First 1
    if ($null -eq $customWalletBackup) { throw 'Wallet backup from before the max edit was missing.' }
    $maxWallet = Read-BytesHash $wallet

    if ($colorPicker.Items[0] -ne 'All colors' -or $colorPicker.Items.IndexOf('Blue') -lt 0) {
        throw 'Expected the color filter to offer All colors and Blue.'
    }
    $colorPicker.SelectedItem = 'Blue'
    $blueBricks = @($brickPicker.Items)
    if ($blueBricks.Count -ne 10 -or @($blueBricks | Where-Object ColorName -ne 'Blue').Count -ne 0) {
        throw 'Filtering to Blue did not display exactly ten blue shapes.'
    }
    $chosen = $blueBricks | Where-Object { $_.Count -eq 0 } | Select-Object -First 1
    if ($null -eq $chosen) { $chosen = $blueBricks[0] }
    for ($index = 0; $index -lt $brickPicker.Items.Count; $index++) {
        if ($brickPicker.Items[$index].Id -eq $chosen.Id) {
            $brickPicker.SelectedIndex = $index
            break
        }
    }
    if ($brickInput.Maximum -ne 999) { throw 'Expected a per-brick maximum of 999.' }
    $setCount = if ($chosen.Count -eq 999) { 998 } else { 999 }
    try {
        $editor.SetColoredBrick($wallet, $chosen.Id, 1000)
        throw 'The editor accepted a count above 999.'
    } catch {
        if ($_.Exception.InnerException -isnot [ArgumentOutOfRangeException]) { throw }
    }
    $brickInput.Value = $setCount
    [void]$type.GetMethod('OnSetBrick', $flags).Invoke($form, $arguments)
    $specific = $editor.ReadColoredBricks($wallet)
    if (($specific | Where-Object Id -eq $chosen.Id).Count -ne $setCount) {
        throw 'Selected brick count did not read back. ' + $status.Text
    }
    foreach ($other in $catalog | Where-Object Id -ne $chosen.Id) {
        if (($specific | Where-Object Id -eq $other.Id).Count -ne $other.Count) {
            throw 'A different colored brick changed.'
        }
    }
    if ($colorPicker.SelectedItem -ne 'Blue' -or $brickPicker.Items.Count -ne 10 -or
        $brickPicker.SelectedItem.Id -ne $chosen.Id) {
        throw 'The blue filter or selected shape changed during save readback.'
    }
    $colorPicker.SelectedIndex = 0
    if ($brickPicker.Items.Count -ne 100) { throw 'All colors did not restore the 100-variant list.' }

    $inventoryBackup = Get-ChildItem -LiteralPath $inventoryDirectory -Filter '*.bak' -File |
        Where-Object { (Read-BytesHash $_.FullName) -eq $oldInventory } | Select-Object -First 1
    if ($null -eq $inventoryBackup) { throw 'Original inventory backup was missing.' }

    if ($allBrickInput.Minimum -ne 0 -or $allBrickInput.Maximum -ne 999) {
        throw 'Expected the all-bricks input to accept 0 to 999.'
    }
    foreach ($testCount in @(217, 0, 999)) {
        $beforeFill = Read-BytesHash $inventory
        if ($testCount -eq 999) {
            [void]$type.GetMethod('OnMaxBricks', $flags).Invoke($form, $arguments)
        } else {
            $allBrickInput.Value = $testCount
            [void]$type.GetMethod('OnFillBricks', $flags).Invoke($form, $arguments)
        }
        $filled = $editor.ReadColoredBricks($wallet)
        if ($filled.Count -ne 100 -or @($filled | Where-Object Count -ne $testCount).Count -ne 0) {
            throw "The all-bricks action did not set all 100 counts to $testCount. $($status.Text)"
        }
        $fillBackup = Get-ChildItem -LiteralPath $inventoryDirectory -Filter '*.bak' -File |
            Where-Object { (Read-BytesHash $_.FullName) -eq $beforeFill } | Select-Object -First 1
        if ($null -eq $fillBackup) { throw "A matching inventory backup for the edit to $testCount was missing." }
    }
    if ((Read-BytesHash $wallet) -ne $maxWallet) { throw 'The colored brick edits changed the wallet.' }

    function Read-WalletMetadata($Editor, [string]$Path) {
        $members = [Reflection.BindingFlags]'Instance,NonPublic'
        $copy = $Editor.Read($Path)
        $content = $copy.GetType().GetField('Content',$members).GetValue($copy)
        $fields = $content.GetType()
        $original = $fields.GetField('Original',$members).GetValue($content)
        $payload = $fields.GetField('Payload',$members).GetValue($content)
        $prefix = [int]$fields.GetField('IvOffset',$members).GetValue($content) + 4
        $tail = [int]$copy.GetType().GetField('CountEnd',$members).GetValue($copy)
        return [Convert]::ToBase64String($original, 0, $prefix) + '.' +
            [Convert]::ToBase64String($payload, $tail, $payload.Length - $tail)
    }

    $recovery = $wallet + '.bak'
    $liveRecovery = $originalWallet + '.bak'
    $nativeRecovery = if ([IO.File]::Exists($liveRecovery)) { $liveRecovery } else { $originalWallet }
    $nativeRecoveryBytes = Read-BytesHash $nativeRecovery
    [IO.File]::Copy($nativeRecovery, $recovery)
    $mainBefore = Read-BytesHash $wallet
    $recoveryBefore = Read-BytesHash $recovery
    $mainMetadata = Read-WalletMetadata $editor $wallet
    $recoveryMetadata = Read-WalletMetadata $editor $recovery
    $backupCount = @(Get-ChildItem -LiteralPath $walletDirectory -File -Filter 'backup-*.bak').Count

    $locked = [IO.File]::Open($wallet, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        try {
            [void]$editor.Set($editor.Read($wallet), 1234)
            throw 'The locked primary wallet accepted a replacement.'
        } catch {
            if ($_.Exception.InnerException -isnot [IO.IOException]) { throw }
        }
    } finally { $locked.Dispose() }
    if ((Read-BytesHash $wallet) -ne $mainBefore -or (Read-BytesHash $recovery) -ne $recoveryBefore -or
        @(Get-ChildItem -LiteralPath $walletDirectory -File -Filter 'backup-*.bak').Count -ne $backupCount) {
        throw 'Both wallet copies must be restored after a partial write failure.'
    }

    [void]$editor.Set($editor.Read($wallet), 1000)
    $savedCopies = @(Get-ChildItem -LiteralPath $walletDirectory -File -Filter 'backup-*.bak')
    if ($editor.Read($wallet).Count -ne 1000 -or $editor.Read($recovery).Count -ne 1000 -or
        (Read-WalletMetadata $editor $wallet) -ne $mainMetadata -or
        (Read-WalletMetadata $editor $recovery) -ne $recoveryMetadata -or
        $savedCopies.Count -ne ($backupCount + 2) -or
        @($savedCopies | Where-Object { (Read-BytesHash $_.FullName) -eq $mainBefore }).Count -eq 0 -or
        @($savedCopies | Where-Object { (Read-BytesHash $_.FullName) -eq $recoveryBefore }).Count -eq 0) {
        throw 'Grey Blocks did not update both copies while preserving their distinct metadata and backups.'
    }

    $mainWithRequestedCount = Read-BytesHash $wallet
    [IO.File]::Copy($nativeRecovery, $recovery, $true)
    [void]$editor.Set($editor.Read($wallet), 1000)
    if ($editor.Read($recovery).Count -ne 1000 -or
        (Read-BytesHash $wallet) -ne $mainWithRequestedCount -or
        @(Get-ChildItem -LiteralPath $walletDirectory -File -Filter 'backup-*.bak').Count -ne ($backupCount + 3) -or
        $null -ne $editor.Set($editor.Read($wallet), 1000)) {
        throw 'A matching main wallet did not repair the older recovery balance or a no-op rewrote data.'
    }

    $crc32 = $editorType.GetMethod('Crc32', [Reflection.BindingFlags]'NonPublic, Static')
    Copy-WithSessionId $nativeRecovery $recovery 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' $crc32
    $mismatched = Read-BytesHash $recovery
    [void]$editor.Set($editor.Read($wallet), 1250)
    if ($editor.Read($wallet).Count -ne 1250 -or (Read-BytesHash $recovery) -ne $mismatched -or
        (Read-BytesHash $nativeRecovery) -ne $nativeRecoveryBytes) {
        throw 'An unrelated recovery copy changed.'
    }
    'Grey Blocks recovery synchronization, rollback, exact headers and backups, and slot isolation verified.'

    Test-AdditionalSlots $editor
    if ((Read-BytesHash $originalWallet) -ne $oldWallet -or
        (Read-BytesHash $originalInventory) -ne $oldInventory -or
        (Read-BytesHash $originalManifest) -ne $oldManifest) {
        throw 'The live wallet, inventory, or manifest changed during verification.'
    }

    "Typed Grey Blocks count of 1,000, rejected inputs, and 9,999,999 cap verified. Blue filter and individual edit verified. All 100 variants at 217, 0, and 999 verified. Reload discovers new saves. Backups match byte-for-byte."
    $image = [System.Drawing.Bitmap]::new($form.Width, $form.Height)
    try {
        $form.DrawToBitmap($image, [System.Drawing.Rectangle]::new(0, 0, $image.Width, $image.Height))
        function Draw-Children($parent, [int]$x, [int]$y) {
            foreach ($child in $parent.Controls) {
                if ($child.AccessibleName -eq 'Save slot' -and $child.Items.Count -le 1) { continue }
                $left = $x + $child.Left
                $top = $y + $child.Top
                $bounds = [System.Drawing.Rectangle]::new($left, $top, $child.Width, $child.Height)
                $child.DrawToBitmap($image, $bounds)
                if ($child -is [System.Windows.Forms.Panel]) { Draw-Children $child $left $top }
            }
        }
        Draw-Children $form 8 31
        $image.Save($RenderPath, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $image.Dispose()
        $form.Close()
    }
} finally {
    $form.Dispose()
    $env:APPDATA = $liveAppData
    $scratch = [IO.Path]::GetFullPath($base)
    $testParent = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    if ($scratch.StartsWith($testParent, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Directory]::Exists($scratch)) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
