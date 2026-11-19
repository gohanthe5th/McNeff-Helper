[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'bin\McNeff Helper.exe')
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(
    'TrainerForm.cs', 'TrainerLayout.cs', 'GameControls.cs', 'EnemyTypes.cs', 'ProjectManager.cs',
    'BuildSubStats.cs', 'WalletEditor.cs', 'ColoredBricks.cs', 'BuildOwnership.cs',
    'SaveSlots.cs', 'GameTrainer.cs', 'GameTrainerSpawn.cs', 'GameTrainerFans.cs',
    'GameTrainerOldPeopleGrowthCaps.cs', 'GameTrainerOldPeopleWorldCaps.cs',
    'GameTrainerEnemies.cs', 'GameArt.cs', 'RecipeUnlocks.cs', 'RecipeArtwork.cs'
)
$catalog = @(
    'BuildRecipes.tsv', 'BuildEffects.tsv', 'BuildPlacements.tsv', 'BuildMetadata.tsv',
    'BuildRarities.tsv', 'BuildStatCatalog.tsv', 'BuildStatRules.tsv', 'RecipeMeshes.bin'
)
$art = @(
    'GameHero.jpg', 'GameLogo.png', 'GameIcon.ico', 'GreyBlock.png', 'ColorBlock.png',
    'Health.png', 'Stamina.png', 'OldPerson.png', 'Fan.png', 'EnemyAlert.png',
    'YouTuber.png', 'Lawyer.png', 'Investigator.png', 'Elite.png', 'RecipeBookIcon.png',
    'BlueprintGrid.png', 'BlueprintFrame.png', 'BlueprintFold.png', 'RecipePhotoCorner.png',
    'RecipeCardFont.ttf', 'RecipeRarity0.png', 'RecipeRarity1.png', 'RecipeRarity2.png',
    'RecipeRarity3.png', 'RecipeRarity4.png', 'RecipeModifier.png', 'RecipeEarnsTag.png', 'Cash.png',
    'ButtonBlue.png', 'ButtonBluePressed.png', 'ButtonOrange.png', 'ButtonOrangePressed.png',
    'ButtonGreen.png', 'ButtonGreenPressed.png'
)
$art += 1..10 | ForEach-Object { 'BuildStat{0:00}.png' -f $_ }

$resources = @($catalog | ForEach-Object { "/resource:$_,$_" })
$resources += $art | ForEach-Object { "/resource:GameArt\$_,GameArt.$_" }
$references = 'System.dll', 'System.Core.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll'
$references = $references | ForEach-Object { "/r:$_" }
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$options = @(
    '/nologo', '/langversion:5', '/target:winexe', '/platform:x64', '/optimize+',
    '/warnaserror+', '/codepage:65001', "/out:$output", '/win32icon:GameArt\GameIcon.ico'
)

Push-Location -LiteralPath $PSScriptRoot
try {
    & $compiler @options @references @sources @resources
    if ($LASTEXITCODE -ne 0) { throw "McNeff Helper build exited with code $LASTEXITCODE." }
}
finally { Pop-Location }
