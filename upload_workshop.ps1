# Workshop upload script for CenterSpeed (item 3711263039)
# Usage: .\upload_workshop.ps1 -Username yoursteamname
param(
    [Parameter(Mandatory=$true)]
    [string]$Username
)

$steamcmd = "C:\steamcmd\steamcmd.exe"
$vdf      = "i:\Repos\Centerspeeds2\CenterSpeed\workshop_upload.vdf"

& $steamcmd `
    +login $Username `
    +workshop_build_item $vdf `
    +quit
