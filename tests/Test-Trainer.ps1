param([string]$Executable,[string]$Fixture,[string]$EnemyTypesRenderPath)

$ErrorActionPreference = 'Stop'
$dll = 'C:\Program Files (x86)\Steam\steamapps\common\We Steal From Old People The Game\data_WeStealFromOldPeople_windows_x86_64\WeStealFromOldPeople.dll'
if (Get-Process -Name WeStealFromOldPeople -ErrorAction SilentlyContinue) {
    throw 'A game process is already running. The test fixture did not start.'
}
$process = [System.Diagnostics.Process]::new()
$process.StartInfo.FileName = $Fixture
$process.StartInfo.Arguments = '"' + $dll + '"'
$process.StartInfo.UseShellExecute = $false
$process.StartInfo.CreateNoWindow = $true
$process.StartInfo.RedirectStandardInput = $true
$process.StartInfo.RedirectStandardOutput = $true
$process.StartInfo.RedirectStandardError = $true
$trainer = $null

function Read-Image([string]$Command) {
    $process.StandardInput.WriteLine($Command)
    $process.StandardInput.Flush()
    $read = $process.StandardOutput.ReadLineAsync()
    if (-not $read.Wait(5000) -or [string]::IsNullOrEmpty($read.Result)) {
        throw "Fixture did not return a value for $Command."
    }
    $read.Result
}
try {
    [void]$process.Start()
    $ready = $process.StandardOutput.ReadLineAsync()
    if (-not $ready.Wait(5000) -or $ready.Result -notmatch '^[0-9A-F]+$') {
        throw ('Image fixture failed to load the installed game library. ' + $ready.Result)
    }
    $matches = @(Get-Process -Name WeStealFromOldPeople)
    if ($matches.Length -ne 1 -or $matches[0].Id -ne $process.Id) {
        throw 'The fixture must be the only WeStealFromOldPeople process.'
    }

    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($Executable))
    $type = $assembly.GetType('GameTrainer', $true)
    $trainer = [Activator]::CreateInstance($type)
    $health = '16BE7B8 2'
    $stamina = @('1BD8584 5', '1BDEE55 5', '1BD9E5B 5', '1BD827C 5', '1BD89F4 5', '1BDEFAD 5')
    $spawnSites = @('18DA984 5','18DBF5C 5')
    $capSites = @('18DBFA0 8','18E31C7 6','18D5386 5','18DDE8A 3')
    $fanSites = @('18DC761 5','18DC7F0 5','18DC96E 5','18E08C5 5')
    $enemyFilter = '1AAB7A9 5'
    $patchSites = @($health) + $stamina + $spawnSites + $capSites + $fanSites + @($enemyFilter)
    $original = @{}
    foreach ($site in $patchSites) { $original[$site] = Read-Image $site }
    if ($original[$health] -ne 'EB-03' -or (Read-Image 'damage') -ne '12.5') {
        throw 'Health fixture baseline mismatch.'
    }

    $trainer.SetUnlimitedHealth($true)
    if (-not $trainer.IsAttached -or (Read-Image $health) -ne 'EB-5D' -or (Read-Image 'damage') -ne '0') {
        throw 'Unlimited health did not cancel the test damage.'
    }
    $trainer.SetUnlimitedStamina($true)
    foreach ($site in $stamina) {
        $patched = Read-Image $site
        if ($patched -eq $original[$site] -or $patched.Substring(0,2) -ne $original[$site].Substring(0,2)) {
            throw "Stamina site $site did not detour correctly."
        }
    }

    foreach ($site in $spawnSites) {
        if ($original[$site] -ne 'F3-0F-10-40-20') { throw "Unexpected old-person quota baseline at $site." }
    }
    [void]$trainer.SetMoreOldPeople($true)
    $spawnPatched = @{}
    foreach ($site in $spawnSites) {
        $spawnPatched[$site] = Read-Image $site
        if ($spawnPatched[$site] -notlike 'E9-*') { throw "The old-person quota detour failed at $site." }
    }
    [void]$trainer.SetMoreOldPeople($true)
    foreach ($site in $spawnSites) {
        if ((Read-Image $site) -ne $spawnPatched[$site]) { throw 'Repeated enable changed the spawn detour.' }
    }
    $spawnDataField = $type.GetField('oldPeopleMultiplierRva', [Reflection.BindingFlags]'NonPublic,Instance')
    $spawnData = '{0:X} 4' -f $spawnDataField.GetValue($trainer)
    if ((Read-Image $spawnData) -ne [BitConverter]::ToString([BitConverter]::GetBytes([single]4))) {
        throw 'Default multiplier was not applied.'
    }
    [void]$trainer.SetMoreOldPeople($true, 1000)
    if ((Read-Image $spawnData) -ne [BitConverter]::ToString([BitConverter]::GetBytes([single]1000))) {
        throw 'The active spawn multiplier did not accept 1,000.'
    }
    foreach ($site in $spawnSites) {
        if ((Read-Image $site) -ne $spawnPatched[$site]) { throw 'Changing the multiplier rewrote the spawn detours.' }
    }
    [void]$trainer.SetMoreOldPeople($true, 37)
    if ((Read-Image $spawnData) -ne [BitConverter]::ToString([BitConverter]::GetBytes([single]37))) {
        throw 'The active spawn multiplier did not accept another value.'
    }
    [void]$trainer.SetMoreOldPeople($true, 37, $true)
    foreach ($site in $capSites) {
        if ((Read-Image $site) -eq $original[$site]) { throw "The cap override did not activate at $site." }
    }
    [void]$trainer.SetMoreOldPeople($true, 37, $false)
    foreach ($site in $capSites) {
        if ((Read-Image $site) -ne $original[$site]) { throw "Turning off the cap override did not restore $site." }
    }
    foreach ($site in $spawnSites) {
        if ((Read-Image $site) -ne $spawnPatched[$site]) { throw 'The multiplier detour should stay active when the override is off.' }
    }

    $fanOriginal = @('E8-0A-04-00-00','E8-7B-03-00-00','E8-FD-01-00-00','48-8B-4C-24-48')
    $fanDetours = @('E8-*','E8-*','E8-*','E9-*')
    $patchedFans = @{}
    [void]$trainer.SetNoFans($true)
    for ($index = 0; $index -lt $fanSites.Count; $index++) {
        $site = $fanSites[$index]
        $actual = Read-Image $site
        if ($original[$site] -ne $fanOriginal[$index] -or
            $actual -eq $original[$site] -or $actual -notlike $fanDetours[$index]) {
            throw "Fan spawn detour failed at $site."
        }
        $patchedFans[$site] = $actual
    }
    $fansOnly = Read-Image $enemyFilter
    if ($original[$enemyFilter] -ne 'E8-B2-02-00-00' -or $fansOnly -eq $original[$enemyFilter] -or
        $fansOnly -notlike 'E8-*') {
        throw 'No fans did not enable the shared hostile spawn filter.'
    }
    [void]$trainer.SetNoEnemies($true)
    $masterMode = Read-Image $enemyFilter
    if ($masterMode -eq $original[$enemyFilter] -or $masterMode -eq $fansOnly -or $masterMode -notlike 'E8-*') {
        throw 'No enemies did not select the master mode.'
    }
    [void]$trainer.SetNoEnemies($false)
    if ((Read-Image $enemyFilter) -ne $fansOnly) {
        throw 'Turning off No enemies lost the independent No fans selection.'
    }
    foreach ($site in $fanSites) {
        if ((Read-Image $site) -ne $patchedFans[$site]) { throw 'No enemies changed the separate No fans toggle.' }
    }
    [void]$trainer.SetNoEnemies($true)
    [void]$trainer.SetNoFans($false)
    if ((Read-Image $enemyFilter) -ne $masterMode) { throw 'Turning off No fans changed the active master mode.' }
    [void]$trainer.SetNoEnemies($false)
    if ((Read-Image $enemyFilter) -ne $original[$enemyFilter]) { throw 'The last enemy toggle failed to restore original code.' }
    foreach ($site in $fanSites) {
        if ((Read-Image $site) -ne $original[$site]) { throw 'Fan targets did not restore when disabled.' }
    }
    foreach ($site in $spawnSites) {
        if ((Read-Image $site) -ne $spawnPatched[$site]) { throw 'Fan toggle changed the old-person quota.' }
    }

    $individualEnemyModes = @{ SetNoFans = $fansOnly }
    foreach ($name in @('SetNoYouTubers','SetNoLawyers','SetNoInvestigators','SetNoElites')) {
        $toggle = $type.GetMethod($name)
        [void]$toggle.Invoke($trainer, @($true))
        $mode = Read-Image $enemyFilter
        if ($mode -eq $original[$enemyFilter] -or $mode -notlike 'E8-*' -or $mode -in $individualEnemyModes.Values) {
            throw "$name did not select its own enemy filter mode."
        }
        $individualEnemyModes[$name] = $mode
        [void]$toggle.Invoke($trainer, @($true))
        if ((Read-Image $enemyFilter) -ne $mode) { throw "Repeated enable changed the mode for $name." }
        [void]$toggle.Invoke($trainer, @($false))
        if ((Read-Image $enemyFilter) -ne $original[$enemyFilter]) { throw "$name did not restore original code." }
    }

    [void]$trainer.SetNoYouTubers($true)
    [void]$trainer.SetNoInvestigators($true)
    $twoTypes = Read-Image $enemyFilter
    if ($twoTypes -eq $original[$enemyFilter] -or $twoTypes -in $individualEnemyModes.Values) {
        throw 'Two independent enemy selections did not combine.'
    }
    foreach ($site in $fanSites) {
        if ((Read-Image $site) -ne $original[$site]) { throw 'Other enemy modes enabled the separate fan spawn hooks.' }
    }
    [void]$trainer.SetNoEnemies($true)
    if ((Read-Image $enemyFilter) -ne $masterMode) { throw 'The master mode changed with different individual selections.' }
    [void]$trainer.SetNoEnemies($false)
    if ((Read-Image $enemyFilter) -ne $twoTypes) { throw 'The master switch lost the individually selected enemy types.' }
    [void]$trainer.SetNoEnemies($true)
    [void]$trainer.SetNoYouTubers($false)
    if ((Read-Image $enemyFilter) -ne $masterMode) { throw 'Changing an individual selection disabled the master mode.' }
    [void]$trainer.SetNoEnemies($false)
    if ((Read-Image $enemyFilter) -ne $individualEnemyModes.SetNoInvestigators) {
        throw 'Turning off one enemy type removed another active selection.'
    }
    [void]$trainer.SetNoInvestigators($false)
    if ((Read-Image $enemyFilter) -ne $original[$enemyFilter]) { throw 'The final enemy type did not restore original code.' }

    $trainer.SetUnlimitedHealth($false)
    if ((Read-Image 'damage') -ne '12.5') { throw 'Health could not be shut off independently.' }
    if ((Read-Image $stamina[0]) -eq $original[$stamina[0]]) { throw 'Health toggle changed the stamina toggle.' }
    $trainer.SetUnlimitedStamina($false)
    foreach ($site in $spawnSites) {
        if ((Read-Image $site) -ne $spawnPatched[$site]) { throw 'Health or stamina changed the spawn toggle.' }
    }
    $trainer.SetMoreOldPeople($false)
    foreach ($site in $patchSites) {
        if ((Read-Image $site) -ne $original[$site]) { throw "Original code at $site did not restore." }
    }

    [void]$trainer.SetUnlimitedHealth($true)
    [void]$trainer.SetUnlimitedStamina($true)
    [void]$trainer.SetMoreOldPeople($true, 19, $true)
    [void]$trainer.SetNoFans($true)
    [void]$trainer.SetNoLawyers($true)
    [void]$trainer.SetNoElites($true)
    [void]$trainer.SetNoEnemies($true)
    $trainer.Dispose()
    $trainer = $null
    foreach ($site in $patchSites) {
        if ((Read-Image $site) -ne $original[$site]) { throw "Trainer close did not restore $site." }
    }
    'Health canceled 12.5 test damage. Individual enemy modes and the master switch restored exact bytes independently and on close.'
    $formType = $assembly.GetType('TrainerForm', $true)
    $private = [Reflection.BindingFlags]'NonPublic, Instance, DeclaredOnly'
    $form = [Activator]::CreateInstance($formType)
    try {
        $healthToggle = $formType.GetField('health', $private).GetValue($form)
        $staminaToggle = $formType.GetField('stamina', $private).GetValue($form)
        $spawnToggle = $formType.GetField('oldPeople', $private).GetValue($form)
        $spawnPicker = $formType.GetField('spawnMultiplier', $private).GetValue($form)
        $capPicker = $formType.GetField('overrideSpawnCaps', $private).GetValue($form)
        $applyButton = $formType.GetField('applySpawnSettings', $private).GetValue($form)
        $applySpawn = $formType.GetMethod('OnApplySpawnSettings', $private)
        if ($form.Text -ne 'McNeff Helper' -or $formType.GetField('noFans', $private) -or
            $formType.GetField('noEnemies', $private) -or $spawnPicker.Minimum -ne 1 -or $spawnPicker.Maximum -ne 1000) {
            throw 'The renamed form did not move the enemy switches or provide the multiplier field.'
        }
        $connectionTick = $formType.GetMethod('OnConnectionTick', $private)
        [void]$connectionTick.Invoke($form, @($form, [EventArgs]::Empty))
        if (-not $healthToggle.Enabled -or -not $staminaToggle.Enabled -or -not $spawnToggle.Enabled -or
            -not $spawnPicker.Enabled -or -not $applyButton.Enabled -or $applyButton.Text -ne 'Apply') {
            throw 'Live controls did not enable for a running game.'
        }
        $healthToggle.Checked = $true
        $staminaToggle.Checked = $true
        $spawnPicker.Value = 13
        [void]$applySpawn.Invoke($form,@($applyButton,[EventArgs]::Empty))
        if (-not $healthToggle.Checked -or -not $staminaToggle.Checked -or -not $spawnToggle.Checked -or
            $healthToggle.Text -notlike '*On' -or $staminaToggle.Text -notlike '*On' -or $spawnToggle.Text -notlike '*On' -or
            (Read-Image 'damage') -ne '0' -or (Read-Image $stamina[0]) -eq $original[$stamina[0]]) {
            throw 'The EXE controls failed to enable the toggles.'
        }
        $formTrainer = $formType.GetField('trainer', $private).GetValue($form)
        $formSpawnData = '{0:X} 4' -f $spawnDataField.GetValue($formTrainer)
        if ((Read-Image $formSpawnData) -ne [BitConverter]::ToString([BitConverter]::GetBytes([single]13))) {
            throw 'Apply did not enable More old people with the selected multiplier.'
        }
        $spawnPicker.Value = 1000
        $capPicker.Checked = $true
        if ((Read-Image $formSpawnData) -ne [BitConverter]::ToString([BitConverter]::GetBytes([single]13))) {
            throw 'Entering a new multiplier changed the active value before Apply.'
        }
        foreach ($site in $capSites) {
            if ((Read-Image $site) -ne $original[$site]) { throw 'The override changed the game before Apply.' }
        }
        [void]$applySpawn.Invoke($form,@($applyButton,[EventArgs]::Empty))
        if ((Read-Image $formSpawnData) -ne [BitConverter]::ToString([BitConverter]::GetBytes([single]1000))) {
            throw 'Apply did not update the active multiplier.'
        }
        foreach ($site in $capSites) {
            if ((Read-Image $site) -eq $original[$site]) { throw "Apply did not install the cap override at $site." }
        }
        foreach ($site in $spawnSites) {
            if ((Read-Image $site) -notlike 'E9-*') { throw 'The spawn checkbox did not enable the quota detour.' }
        }
        $dialogType = $formType.GetNestedType('EnemyTypesForm', [Reflection.BindingFlags]'NonPublic')
        $dialog = [Activator]::CreateInstance($dialogType, [object[]]@($form.PSObject.BaseObject))
        try {
            $types = @{}
            foreach ($toggle in $dialogType.GetField('switches', $private).GetValue($dialog).Values) {
                $types[$toggle.AccessibleName] = $toggle
            }
            $expectedTypes = @('No enemies','No Fans','No YouTubers','No Lawyers','No Internet Investigators','No Elites')
            if ($dialog.Text -ne 'Enemy Types | McNeff Helper' -or $types.Count -ne 6 -or
                @($expectedTypes | Where-Object { !$types.ContainsKey($_) -or !$types[$_].Enabled }).Count) {
                throw 'Enemy Types did not show the master and five individual switches.'
            }
            $types['No Fans'].Checked = $true
            for ($index = 0; $index -lt $fanSites.Count; $index++) {
                $site = $fanSites[$index]
                $actual = Read-Image $site
                if ($actual -eq $original[$site] -or $actual -notlike $fanDetours[$index]) {
                    throw 'Fan switch did not update the wave and reinforcement spawn paths.'
                }
            }
            $types['No enemies'].Checked = $true
            $types['No Lawyers'].Checked = $true
            $types['No Elites'].Checked = $true
            $masterBranch = Read-Image $enemyFilter
            $types['No enemies'].Checked = $false
            if ((Read-Image $enemyFilter) -in @($masterBranch,$original[$enemyFilter])) {
                throw 'Turning off the master cleared the switches set in Enemy Types.'
            }
            if ($EnemyTypesRenderPath) {
                [void]$dialogType.GetMethod('RefreshState',$private).Invoke($dialog,@())
                $dialog.PerformLayout()
                $bitmap = [Drawing.Bitmap]::new($dialog.Width, $dialog.Height)
                try {
                    $dialog.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
                    foreach ($control in $dialog.Controls) {
                        $rect = [Drawing.Rectangle]::new($control.Left + 8, $control.Top + 31, $control.Width, $control.Height)
                        $control.DrawToBitmap($bitmap, $rect)
                    }
                    $bitmap.Save($EnemyTypesRenderPath,[Drawing.Imaging.ImageFormat]::Png)
                } finally { $bitmap.Dispose() }
            }
        } finally { $dialog.Dispose() }
        $spawnToggle.Checked = $false
        foreach ($site in ($spawnSites + $capSites)) {
            if ((Read-Image $site) -ne $original[$site]) { throw 'The spawn checkbox did not restore quotas.' }
        }
        if ((Read-Image 'damage') -ne '0' -or (Read-Image $stamina[0]) -eq $original[$stamina[0]]) {
            throw 'The spawn checkbox changed the other cheats.'
        }
        $spawnToggle.Checked = $true
        $healthToggle.Checked = $false
        if ($healthToggle.Text -notlike '*Off' -or (Read-Image 'damage') -ne '12.5' -or
            (Read-Image $stamina[0]) -eq $original[$stamina[0]]) {
            throw 'The health checkbox did not toggle off independently.'
        }
        $close = $formType.GetMethod('OnFormClosed', $private)
        [void]$close.Invoke($form, @($form, [System.Windows.Forms.FormClosedEventArgs]::new('UserClosing')))
        foreach ($site in $patchSites) {
            if ((Read-Image $site) -ne $original[$site]) { throw "Window close did not restore $site." }
        }
        'Verified Apply, the adjustable multiplier and cap override, Enemy Types switches, and restoration at window close.'
        $process.StandardInput.WriteLine('quit')
        $process.StandardInput.Flush()
        if (-not $process.WaitForExit(5000)) { throw 'The isolated game fixture did not close.' }
        $closedForm = [Activator]::CreateInstance($formType)
        try {
            [void]$connectionTick.Invoke($closedForm, @($closedForm, [EventArgs]::Empty))
            if ($formType.GetField('health', $private).GetValue($closedForm).Enabled -or
                $formType.GetField('stamina', $private).GetValue($closedForm).Enabled -or
                $formType.GetField('oldPeople', $private).GetValue($closedForm).Enabled -or
                $formType.GetField('applySpawnSettings', $private).GetValue($closedForm).Enabled) {
                throw 'Live controls were enabled with the game closed.'
            }
        } finally { $closedForm.Dispose() }
        'Verified live control availability with the game running and closed.'
    } finally {
        $form.Dispose()
    }
}
finally {
    try {
        if ($null -ne $trainer) { $trainer.Dispose() }
    }
    finally {
        if (-not $process.HasExited) {
            try {
                $process.StandardInput.WriteLine('quit')
                $process.StandardInput.Flush()
            } finally {
                if (-not $process.WaitForExit(3000)) { $process.Kill() }
            }
        }
        $process.Dispose()
    }
}
