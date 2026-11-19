param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$Fixture
)

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($Executable))
$editorType = $assembly.GetType('WalletEditor', $true)
$dll = $editorType.GetMethod('FindGameDll').Invoke($null, @())
$editor = [Activator]::CreateInstance($editorType, [object[]]@($dll))
$live = @($editor.FindSaveSlots() | Where-Object { $_.Path })[0].Path
if (!$live) { throw 'Expected an existing save to copy for the regression.' }
$sourceFiles = @(Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($live)) -File)
$sourceHashes = @{}
foreach ($file in $sourceFiles) { $sourceHashes[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }
$scratch = Join-Path $PSScriptRoot ('q-grey-load-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[void][IO.Directory]::CreateDirectory($scratch)
$wallet = Join-Path $scratch 'wallet.data'
$recovery = $wallet + '.bak'

function Read-BytesHash([string]$Path) {
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path))
}

function Read-DirectoryState {
    $result = @{}
    foreach ($file in Get-ChildItem -LiteralPath $scratch -File) {
        $result[$file.Name] = Read-BytesHash $file.FullName
    }
    return $result
}

function Assert-DirectoryState($Expected, [string]$Message) {
    $actual = Read-DirectoryState
    if ($actual.Count -ne $Expected.Count) { throw $Message }
    foreach ($name in $Expected.Keys) {
        if (!$actual.ContainsKey($name) -or $actual[$name] -ne $Expected[$name]) { throw $Message }
    }
}

function Read-NativeChoice {
    $names = @(& $Fixture $dll $scratch)
    if ($LASTEXITCODE -ne 0 -or $names.Count -eq 0) { throw 'The detached native wallet ordering fixture failed.' }
    # All candidate saves in this fixture are valid. The native loader accepts
    # the first successful candidate in the order returned by CompareTo.
    [pscustomobject]@{ Name = $names[0]; Count = $editor.Read((Join-Path $scratch $names[0])).Count }
}

function Write-ForeignSlot([string]$Path, [byte[]]$Bytes) {
    $copy = [byte[]]$Bytes.Clone()
    $header = 36 + [int][BitConverter]::ToUInt32($copy, 12) + [int][BitConverter]::ToUInt32($copy, 16)
    $id = [Text.Encoding]::UTF8.GetBytes('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
    if ([BitConverter]::ToUInt64($copy, $header) -ne $id.Length) { throw 'Unexpected session ID width.' }
    [Buffer]::BlockCopy($id, 0, $copy, $header + 48, $id.Length)
    $crc32 = $editorType.GetMethod('Crc32', [Reflection.BindingFlags]'NonPublic, Static')
    $crc = [uint32]$crc32.Invoke($null, [object[]]@($copy, ($copy.Length - 4)))
    [Buffer]::BlockCopy([BitConverter]::GetBytes($crc), 0, $copy, $copy.Length - 4, 4)
    [IO.File]::WriteAllBytes($Path, $copy)
}

try {
    [IO.File]::Copy($live, $wallet)
    [void]$editor.Set($editor.Read($wallet), 0)
    $zero = [IO.File]::ReadAllBytes($wallet)
    [void]$editor.Set($editor.Read($wallet), 4187)
    [IO.File]::WriteAllBytes($recovery, $zero)
    $legacy1 = $wallet + '.grey-blocks.20261114-222425-3288b802.bak'
    $legacy2 = $wallet + '.grey-blocks.20261115-192425-3288b803.bak'
    foreach ($path in @($legacy1, $legacy2)) { [IO.File]::WriteAllBytes($path, $zero) }
    $ordinary = Join-Path $scratch 'backup-123456789abc.bak'
    [IO.File]::WriteAllBytes($ordinary, $zero)
    $ordinaryBefore = Read-BytesHash $ordinary

    $before = Read-NativeChoice
    if ($editor.Read($wallet).Count -ne 4187 -or $before.Name -ne [IO.Path]::GetFileName($legacy2) -or $before.Count -ne 0) {
        throw 'The native fixture did not reproduce the legacy backup overriding the primary balance.'
    }
    [void]$editor.Set($editor.Read($wallet), 10)
    $after = Read-NativeChoice
    if ($after.Name -ne 'wallet.data' -or $after.Count -ne 10 -or $editor.Read($recovery).Count -ne 10) {
        throw 'The game would still choose an older gray block balance after Set.'
    }
    foreach ($path in @($legacy1, $legacy2)) {
        if ([IO.File]::Exists($path) -or (Read-BytesHash ($path + '.mcneff-backup')) -ne [Convert]::ToBase64String($zero)) {
            throw 'A legacy backup was not archived byte-for-byte outside the native candidate extensions.'
        }
    }
    if ((Read-BytesHash $ordinary) -ne $ordinaryBefore) { throw 'A normal trainer backup changed.' }
    $stable = Read-DirectoryState
    if ($null -ne $editor.Set($editor.Read($wallet), 10)) { throw 'A repeated Set unexpectedly performed another edit.' }
    Assert-DirectoryState $stable 'A repeated Set rewrote the wallet or its backups.'

    $matchingLegacy = $wallet + '.grey-blocks.20261116-192425-3288b804.bak'
    [IO.File]::WriteAllBytes($matchingLegacy, $zero)
    $matchingPrimary = Read-BytesHash $wallet
    $matchingRecovery = Read-BytesHash $recovery
    [void]$editor.Set($editor.Read($wallet), 10)
    if ((Read-NativeChoice).Count -ne 10 -or [IO.File]::Exists($matchingLegacy) -or
        (Read-BytesHash ($matchingLegacy + '.mcneff-backup')) -ne [Convert]::ToBase64String($zero) -or
        (Read-BytesHash $wallet) -ne $matchingPrimary -or (Read-BytesHash $recovery) -ne $matchingRecovery) {
        throw 'A matching primary balance did not repair a shadowing legacy backup without rewriting the wallets.'
    }

    $rollbackFirst = $wallet + '.grey-blocks.20261117-192425-3288b805.bak'
    $rollbackLocked = $wallet + '.grey-blocks.20261118-192425-3288b806.bak'
    foreach ($path in @($rollbackFirst, $rollbackLocked)) { [IO.File]::WriteAllBytes($path, $zero) }
    $beforeFailure = Read-DirectoryState
    $locked = [IO.File]::Open($rollbackLocked, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        try {
            [void]$editor.Set($editor.Read($wallet), 11)
            throw 'A locked legacy backup unexpectedly accepted an archive move.'
        } catch {
            if ($_.Exception.InnerException -isnot [IO.IOException]) { throw }
        }
    } finally { $locked.Dispose() }
    Assert-DirectoryState $beforeFailure 'An archive failure did not roll back both wallets and the earlier archive move.'

    $foreign = $wallet + '.grey-blocks.20261119-192425-foreign.bak'
    Write-ForeignSlot $foreign $zero
    $foreignBefore = Read-BytesHash $foreign
    [void]$editor.Set($editor.Read($wallet), 10)
    if (![IO.File]::Exists($foreign) -or (Read-BytesHash $foreign) -ne $foreignBefore -or
        [IO.File]::Exists($foreign + '.mcneff-backup')) {
        throw 'A legacy backup belonging to another session changed.'
    }
    'Native wallet candidate ordering, legacy shadowing repair, matching-balance repair, idempotence, archive rollback, and session isolation verified.'
} finally {
    foreach ($path in $sourceHashes.Keys) {
        if ((Get-FileHash -LiteralPath $path).Hash -ne $sourceHashes[$path]) { throw 'A live wallet file changed during the regression.' }
    }
    $resolved = [IO.Path]::GetFullPath($scratch)
    $parent = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    if ($resolved.StartsWith($parent + 'q-grey-load-', [StringComparison]::OrdinalIgnoreCase) -and [IO.Directory]::Exists($resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
