; Version, payload and output paths are supplied by publish.ps1.
[Setup]
AppId=CodexSwapAccount
AppName=Codex Swap Account
; Setup always starts in English; other languages are opt-in through the language dialog or /LANG.
; This matches the behavior before extra languages were added, including silent updates.
LanguageDetectionMethod=none
AppVersion={#ProductVersion}
VersionInfoVersion={#ProductVersion}
VersionInfoProductVersion={#ProductVersion}
AppPublisher=ZOONGG
DefaultDirName={localappdata}\Programs\CodexSwapAccount
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#ArtifactsDir}
OutputBaseFilename=CodexSwapAccount-win-x64-setup
SetupIconFile=..\src\CodexProfileOverlay\Assets\AppIcon.ico
UninstallDisplayIcon={app}\CodexProfileOverlay.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\CodexProfileOverlay
SetupMutex=Local\CodexSwapAccount.Setup
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
AlwaysRestart=no
Uninstallable=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[CustomMessages]
english.UpdateStillRunning=Codex Swap Account is still running. Close it and run this installer again. No files have been changed.
russian.UpdateStillRunning=Codex Swap Account ещё работает. Закройте его и снова запустите установщик. Файлы не изменены.
chinesesimplified.UpdateStillRunning=Codex Swap Account 仍在运行。请关闭它后重新运行安装程序，未更改任何文件。
english.ProtectedDirectory=Choose a program installation directory outside Codex profiles and application data.
russian.ProtectedDirectory=Выберите каталог установки вне профилей Codex и данных приложения.
chinesesimplified.ProtectedDirectory=请选择位于 Codex 配置目录和应用数据之外的安装目录。
english.UpdateRecovery=The update was not completed. Accounts and settings were preserved. Reopen the existing switcher? You can run the installer again later.
russian.UpdateRecovery=Обновление не завершено. Аккаунты и настройки сохранены. Открыть прежний переключатель? Установщик можно запустить повторно позже.
chinesesimplified.UpdateRecovery=更新未完成。账户和设置已保留。是否重新打开现有的切换器？你可以稍后再次运行安装程序。
english.RecoveryFailed=Could not reopen the switcher. Run Codex Swap Account from the Start Menu or rerun the installer.
russian.RecoveryFailed=Не удалось открыть переключатель. Запустите Codex Swap Account через меню Пуск или повторите установку.
chinesesimplified.RecoveryFailed=无法重新打开切换器。请从开始菜单运行 Codex Swap Account，或重新运行安装程序。

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installation.id"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Codex Swap Account"; Filename: "{app}\CodexProfileOverlay.exe"
Name: "{userdesktop}\Codex Swap Account"; Filename: "{app}\CodexProfileOverlay.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CodexProfileOverlay.exe"; Parameters: "--installed"; Description: "{cm:LaunchProgram,Codex Swap Account}"; Flags: postinstall nowait skipifsilent; Check: not IsUpdate
Filename: "{app}\CodexProfileOverlay.exe"; Parameters: "--updated"; Flags: nowait; Check: IsUpdate

[Code]
var
  UpdateStarted, InstallCompleted: Boolean;

function IsUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function IsWithin(Directory, ProtectedRoot: String): Boolean;
begin
  Directory := AddBackslash(ExpandFileName(Directory));
  ProtectedRoot := AddBackslash(ExpandFileName(ProtectedRoot));
  Result := CompareText(Copy(Directory, 1, Length(ProtectedRoot)), ProtectedRoot) = 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if IsWithin(ExpandConstant('{app}'), ExpandConstant('{%USERPROFILE}\.codex')) or
     IsWithin(ExpandConstant('{app}'), ExpandConstant('{%USERPROFILE}\.codex-profiles')) or
     IsWithin(ExpandConstant('{app}'), ExpandConstant('{localappdata}\CodexProfileOverlay')) then
    Result := CustomMessage('ProtectedDirectory');
end;

function InitializeSetup(): Boolean;
var
  Attempts: Integer;
begin
  Result := True;
  if ExpandConstant('{param:UPDATE|0}') = '1' then begin
    Attempts := 0;
    while CheckForMutexes('Local\CodexProfileOverlay') and (Attempts < 300) do begin
      Sleep(100);
      Attempts := Attempts + 1;
    end;
    if CheckForMutexes('Local\CodexProfileOverlay') then begin
      MsgBox(CustomMessage('UpdateStillRunning'), mbError, MB_OK);
      Result := False;
    end;
    UpdateStarted := Result;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Existing: String;
begin
  if CurStep = ssDone then InstallCompleted := True;
  if CurStep = ssPostInstall then begin
    { Preserve startup preference while migrating a portable registration. }
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CodexProfileOverlay', Existing) and (Existing <> '') then
      RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CodexProfileOverlay', '"' + ExpandConstant('{app}\CodexProfileOverlay.exe') + '"');
  end;
end;

procedure DeinitializeSetup();
var
  ExitCode: Integer;
  Executable: String;
begin
  if UpdateStarted and not InstallCompleted then begin
    Executable := ExpandConstant('{app}\CodexProfileOverlay.exe');
    if FileExists(Executable) and not CheckForMutexes('Local\CodexProfileOverlay') then
      if MsgBox(CustomMessage('UpdateRecovery'), mbConfirmation, MB_YESNO) = IDYES then
        if not Exec(Executable, '--installed', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ExitCode) then
          MsgBox(CustomMessage('RecoveryFailed'), mbError, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Existing: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CodexProfileOverlay', Existing) and
      (CompareText(Existing, '"' + ExpandConstant('{app}\CodexProfileOverlay.exe') + '"') = 0) then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CodexProfileOverlay');
end;
