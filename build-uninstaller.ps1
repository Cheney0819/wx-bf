param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

dotnet publish "$PSScriptRoot\tools\DesktopPet.Uninstaller\DesktopPet.Uninstaller.csproj" -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$PSScriptRoot\publish-uninstaller"
