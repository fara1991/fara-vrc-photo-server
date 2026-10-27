; FaraVRCPhotoServer のセットアップ（Inno Setup 6）。build/publish.ps1 が、先に実行ファイルを dist\publish に作ってから、これをコンパイルする。
; 手で動かすとき: ISCC.exe /DAppVersion=1.0.0 installer\FaraVRCPhotoServer.iss
#define AppName "FaraVRCPhotoServer"
#define AppExe "FaraVrcPhotoServer.exe"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; 同じ AppId なら、上書きインストール（更新）になる。変えない
AppId={{B8A4F5E2-3C1D-4E7A-9A56-7D2F1C0E8B34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Fara
AppPublisherURL=https://github.com/fara1991/fara-vrc-photo-server
AppSupportURL=https://github.com/fara1991/fara-vrc-photo-server/issues
; 管理者の権限は要らない（現在のユーザーの AppData\Local\Programs に入る）
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\FaraVrcPhotoServer.App\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 動いているときは、閉じるように案内する（更新・削除の前）。アプリ側の同時起動の防止（Program.cs）と同じ名前
AppMutex=Local\FaraVrcPhotoServer
CloseApplications=yes
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "autostart"; Description: "Windows の起動時に自動で起動する"; GroupDescription: "追加の設定:"
Name: "desktopicon"; Description: "デスクトップにアイコンを作る"; GroupDescription: "追加の設定:"; Flags: unchecked

[Files]
Source: "..\dist\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; 自動起動（アプリの設定の「Windows の起動時に自動で起動する」と同じ Run キー）。アンインストールで消す
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FaraVrcPhotoServer"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{#AppName} を起動する"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; アンインストールでは、アプリが作ったデータ（設定・履歴・記録。%LOCALAPPDATA%\FaraVrcPhotoServer）も、まとめて消す。
; 利用者が選んだフォルダ（元の写真の控えのフォルダなど）は、写真が入っているので、消さない
Type: filesandordirs; Name: "{localappdata}\FaraVrcPhotoServer"
