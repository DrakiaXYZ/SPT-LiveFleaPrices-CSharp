param (
    [string]$modName,
    [string]$modVersion
)

# Configuration
$packageDir = '.\Package'
$artifactDir = '.\bin\Package'

# Make sure our CWD is where the script lives
Set-Location $PSScriptRoot

Write-Host ('Packaging {0} v{1}' -f $modName, $modVersion)

# Create the package structure
$sptDir = '{0}\SPT' -f $packageDir 
$modsDir = '{0}\user\mods' -f $sptDir
Remove-Item -Path $sptDir -Recurse -Force
$null = mkdir $modsDir -ea 0

# # Copy required files to the package structure
$artifactPath = ('{0}\{1}' -f $artifactDir, $modName)
Copy-Item -Path $artifactPath -Destination $modsDir -Recurse

# Create the archive
$archivePath = '{0}\{1}-{2}.7z' -f $packageDir, $modName, $modVersion
if (Test-Path $archivePath)
{
    Remove-Item $archivePath
}
7z a $archivePath $sptDir

Write-Host ('Mod packaging complete')