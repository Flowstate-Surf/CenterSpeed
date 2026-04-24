$game   = "I:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive"
$rc     = "$game\game\bin\win64\resourcecompiler.exe"
$vtex   = "$game\content\csgo_addons\flowassets\materials\digits_x\digits_x.vtex"
$vpcf   = "$game\content\csgo_addons\flowassets\particles\digits_x\digits_x.vpcf"

# Remove stale compiled outputs from both content and game folders
$toDelete = @(
    "$game\content\csgo_addons\flowassets\materials\digits_x\digits_x.vtex_c",
    "$game\game\csgo_addons\flowassets\materials\digits_x\digits_x.vtex_c",
    "$game\game\csgo_addons\flowassets\particles\digits_x\digits_x.vpcf_c"
)
foreach ($f in $toDelete) {
    if (Test-Path $f) { Remove-Item $f -Force; Write-Host "Deleted $f" }
}

& $rc -i $vtex -i $vpcf

$assets = "i:\Repos\Centerspeeds2\CenterSpeed\assets"
Copy-Item "$game\game\csgo_addons\flowassets\materials\digits_x\digits_x.vtex_c" "$assets\materials\digits_x\" -Force
Copy-Item "$game\game\csgo_addons\flowassets\particles\digits_x\digits_x.vpcf_c" "$assets\particles\digits_x\" -Force
Write-Host "Copied compiled files to assets/"
