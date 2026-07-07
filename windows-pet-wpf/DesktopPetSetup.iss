[Setup]
AppId={{8D5C4C3A-9F3E-4BA3-A8F1-35D3C86A7C11}
AppName=桌宠
AppVersion=1.0.0
AppPublisher=Junjiee
DefaultDirName={autopf}\JunjieeDesktopPet
DefaultGroupName=桌宠
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=installer-output
OutputBaseFilename=桌宠-安装包
Compression=lzma
SolidCompression=yes
WizardStyle=modern
SetupIconFile=Assets\app-icon.ico
UninstallDisplayIcon={app}\DesktopPet.Wpf.exe
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked
Name: "autorun"; Description: "开机自动启动"; GroupDescription: "附加任务:"; Flags: checkedonce

[Files]
Source: "bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\桌宠"; Filename: "{app}\DesktopPet.Wpf.exe"
Name: "{autodesktop}\桌宠"; Filename: "{app}\DesktopPet.Wpf.exe"; Tasks: desktopicon
Name: "{userstartup}\桌宠"; Filename: "{app}\DesktopPet.Wpf.exe"; Tasks: autorun

[Run]
Filename: "{app}\DesktopPet.Wpf.exe"; Description: "立即启动桌宠"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\uninstall.ps1"" -InstallDir ""{app}"""; Flags: runhidden waituntilterminated skipifdoesntexist
