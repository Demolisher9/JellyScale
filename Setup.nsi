; JellyScale installer (NSIS 3, Modern UI 2). Build JellyScale.exe first (tools/build.sh or tools\build.cmd).
Unicode true
ManifestDPIAware true
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
!include "WinMessages.nsh"

!define APP "JellyScale"
!define VERSION "0.4.0"
!define PUBLISHER "Demolisher9"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\JellyScale"
!define LEGACY_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\JellyfinTVBridge"
; JellyScale was called JellyHop until 0.4.0. Same app, different folder, firewall rule and Installed apps entry.
!define PREV_APP "JellyHop"
!define PREV_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\JellyHop"

Name "${APP}"
OutFile "JellyScale-Setup.exe"
InstallDir "$PROGRAMFILES64\${APP}"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show
BrandingText "${PUBLISHER}  |  ${APP} ${VERSION}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "FileDescription" "${APP} Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright (C) 2026 ${PUBLISHER}"

!define MUI_ICON "art\JellyScale.ico"
!define MUI_UNICON "art\JellyScale.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP "art\wizard.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "art\wizard.bmp"
!define MUI_ABORTWARNING

!define MUI_WELCOMEPAGE_TITLE "Welcome to JellyScale"
!define MUI_WELCOMEPAGE_TEXT "Many TVs can install the Jellyfin app but can't install Tailscale, so they can't reach a Jellyfin server that you share over Tailscale.$\r$\n$\r$\nJellyScale fixes that without buying a streaming stick. This Windows PC connects to your server over Tailscale and passes it on to the TV over your home network. The TV only needs its Jellyfin app.$\r$\n$\r$\nBefore you begin:$\r$\n  -  Install Tailscale on this PC and sign in.$\r$\n  -  Make sure this PC can reach your Jellyfin server.$\r$\n  -  Connect this PC to your home network."
!insertmacro MUI_PAGE_WELCOME
Page custom ReviewPage
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "JellyScale is ready"
!define MUI_FINISHPAGE_TEXT "Open JellyScale from the Start menu or the desktop. Connect your server once, then press the big button to turn the bridge ON.$\r$\n$\r$\nJellyScale shows the address to type into your TV's Jellyfin app."
!define MUI_FINISHPAGE_RUN "$INSTDIR\JellyScale.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Open JellyScale now"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "English"

Var Dialog
Var BoldFont
Var Result
Var Legacy
Var LegacyDir
Var Prev
Var PrevDir
Var AutoStart   ; 1 = Start with Windows (the review page checkbox)
Var AutoStartBox

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "JellyScale needs 64-bit Windows 10 or Windows 11." /SD IDOK
    Abort
  ${EndIf}
  ${DisableX64FSRedirection}
  SetRegView 64
  SetShellVarContext all
  InitPluginsDir

  ; JellyScale is a .NET Framework 4.x app (4.8 ships with Windows 10 and 11).
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 == ""
  ${OrIf} $0 < 378389
    MessageBox MB_ICONSTOP "JellyScale needs the .NET Framework 4.8, which comes with Windows 10 and 11. Run Windows Update, then try again." /SD IDOK
    Abort
  ${EndIf}

  ; The earlier version of this app was called Jellyfin TV Bridge.
  StrCpy $Legacy 0
  ReadRegStr $0 HKLM "${LEGACY_KEY}" "DisplayName"
  ReadRegStr $LegacyDir HKLM "${LEGACY_KEY}" "InstallLocation"
  ${If} $0 != ""
  ${OrIf} $LegacyDir != ""
    ${If} $LegacyDir == ""
      StrCpy $LegacyDir "$PROGRAMFILES64\Jellyfin TV Bridge"
    ${EndIf}
    MessageBox MB_YESNO|MB_ICONINFORMATION "Jellyfin TV Bridge, the earlier version of this app, is installed.$\r$\n$\r$\nJellyScale replaces it. Setup will turn it off and remove it first, and keep your server address.$\r$\n$\r$\nContinue?" /SD IDYES IDYES legacy_ok
    Abort
    legacy_ok:
    StrCpy $Legacy 1
  ${EndIf}

  ; JellyHop is this app's previous name. Both use port 8090, so it has to go before JellyScale is installed.
  StrCpy $Prev 0
  ReadRegStr $0 HKLM "${PREV_KEY}" "DisplayName"
  ReadRegStr $PrevDir HKLM "${PREV_KEY}" "InstallLocation"
  ${If} $0 != ""
  ${OrIf} $PrevDir != ""
    ${If} $PrevDir == ""
      StrCpy $PrevDir "$PROGRAMFILES64\${PREV_APP}"
    ${EndIf}
    MessageBox MB_YESNO|MB_ICONINFORMATION "${PREV_APP} is installed. JellyScale is its new name.$\r$\n$\r$\nSetup will turn ${PREV_APP} off and remove it first, and keep your server address.$\r$\n$\r$\nContinue?" /SD IDYES IDYES prev_ok
    Abort
    prev_ok:
    StrCpy $Prev 1
  ${EndIf}

  ; Start with Windows: off for new installs; when reinstalling, keep whatever is set now.
  StrCpy $AutoStart 0
  nsExec::Exec '"$SYSDIR\schtasks.exe" /Query /TN "${APP}"'
  Pop $0
  ${If} $0 == 0
    StrCpy $AutoStart 1
  ${EndIf}
FunctionEnd

!macro Heading TOP TEXT
  ${NSD_CreateLabel} 0 ${TOP} 100% 10u "${TEXT}"
  Pop $0
  SendMessage $0 ${WM_SETFONT} $BoldFont 1
!macroend

Function ReviewPage
  !insertmacro MUI_HEADER_TEXT "What JellyScale does" "Nothing changes until you turn JellyScale ON."
  nsDialogs::Create 1018
  Pop $Dialog
  CreateFont $BoldFont "$(^Font)" "$(^FontSize)" "700"

  !insertmacro Heading 0u "WHEN YOU TURN IT ON"
  ${NSD_CreateLabel} 0 10u 100% 26u "Your TV can reach Jellyfin through this PC (port 8090). To keep the stream from dropping, this PC stays awake: JellyScale switches to the High Performance power plan and sets sleep and hibernate to Never. Keep laptops plugged in."
  Pop $0
  !insertmacro Heading 38u "WHEN YOU TURN IT OFF"
  ${NSD_CreateLabel} 0 48u 100% 10u "Your own sleep settings come back and the Balanced power plan is selected."
  Pop $0
  !insertmacro Heading 61u "WHAT GETS INSTALLED"
  ${If} $Prev == 1
    StrCpy $1 "${PREV_APP} (this app's previous name) is turned off and removed first; your server address is kept."
  ${ElseIf} $Legacy == 1
    StrCpy $1 "Jellyfin TV Bridge is turned off and removed first; your server address is kept."
  ${Else}
    StrCpy $1 "Tailscale itself is never changed."
  ${EndIf}
  ${NSD_CreateLabel} 0 71u 100% 34u "The JellyScale app and shortcuts, a firewall rule for port 8090 (home network only), and Caddy, which does the bridging (downloaded with WinGet if needed). $1"
  Pop $0
  !insertmacro Heading 108u "START WITH WINDOWS"
  ${NSD_CreateCheckbox} 0 118u 100% 11u "Turn JellyScale on automatically when I sign in to Windows"
  Pop $AutoStartBox
  ${NSD_SetState} $AutoStartBox $AutoStart
  ${NSD_OnClick} $AutoStartBox AutoStartClicked
  ${NSD_CreateLabel} 12u 129u 100% 10u "You can change this later in JellyScale's menu (•••)."
  Pop $0
  nsDialogs::Show
FunctionEnd

Function AutoStartClicked
  Pop $0
  ${NSD_GetState} $AutoStartBox $AutoStart
FunctionEnd

Section "Install"
  ; 1. Replace the older Jellyfin TV Bridge (it uses the same port).
  ${If} $Legacy == 1
    DetailPrint "Removing Jellyfin TV Bridge (keeping your server address)..."
    CreateDirectory "$PLUGINSDIR\legacy"
    ${If} ${FileExists} "$LegacyDir\settings.json"
      CopyFiles /SILENT "$LegacyDir\settings.json" "$PLUGINSDIR\legacy"
    ${EndIf}
    ${If} ${FileExists} "$LegacyDir\caddy.exe"
      CopyFiles /SILENT "$LegacyDir\caddy.exe" "$PLUGINSDIR\legacy"
    ${EndIf}
    StrCpy $Result "missing"
    ${If} ${FileExists} "$LegacyDir\Uninstall-JellyfinTV.ps1"
      nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$LegacyDir\Uninstall-JellyfinTV.ps1" -Quiet'
      Pop $Result
    ${EndIf}
    ${If} $Result != 0
      MessageBox MB_ICONSTOP "Jellyfin TV Bridge couldn't be removed automatically.$\r$\n$\r$\nUninstall it from Settings > Apps > Installed apps, then run JellyScale Setup again." /SD IDOK
      Abort "Jellyfin TV Bridge is still installed."
    ${EndIf}
    DeleteRegKey HKLM "${LEGACY_KEY}"
  ${EndIf}

  ; 1b. Replace JellyHop (this app's previous name). Its own uninstaller turns it off, restores
  ;     sleep settings and removes its firewall rule; its server address and Caddy are kept.
  ${If} $Prev == 1
    DetailPrint "Removing ${PREV_APP} (keeping your server address)..."
    CreateDirectory "$PLUGINSDIR\legacy"
    ${If} ${FileExists} "$PrevDir\settings.json"
      CopyFiles /SILENT "$PrevDir\settings.json" "$PLUGINSDIR\legacy"
    ${EndIf}
    ${If} ${FileExists} "$PrevDir\caddy.exe"
      CopyFiles /SILENT "$PrevDir\caddy.exe" "$PLUGINSDIR\legacy"
    ${EndIf}
    ${If} ${FileExists} "$PrevDir\Uninstall.exe"
      ; _?= runs the uninstaller in place, so ExecWait really waits and gets its exit code.
      ClearErrors
      ExecWait '"$PrevDir\Uninstall.exe" /S _?=$PrevDir' $Result
      ${If} ${Errors}
      ${OrIf} $Result != 0
        MessageBox MB_ICONSTOP "${PREV_APP} couldn't be removed automatically.$\r$\n$\r$\nOpen ${PREV_APP}, turn it OFF, uninstall it from Settings > Apps > Installed apps, then run JellyScale Setup again." /SD IDOK
        Abort "${PREV_APP} is still installed."
      ${EndIf}
      Delete "$PrevDir\Uninstall.exe"
      RMDir "$PrevDir"
    ${ElseIf} ${FileExists} "$PrevDir\${PREV_APP}.exe"
      MessageBox MB_ICONSTOP "${PREV_APP} couldn't be removed automatically (its uninstaller is missing).$\r$\n$\r$\nOpen ${PREV_APP}, turn it OFF, delete its folder ($PrevDir), then run JellyScale Setup again." /SD IDOK
      Abort "${PREV_APP} is still installed."
    ${Else}
      ; Only a leftover Installed apps entry: there's nothing to turn off, so just tidy up.
      nsExec::ExecToLog '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="${PREV_APP}"'
      Pop $0
      Delete "$SMPROGRAMS\${PREV_APP}.lnk"
      Delete "$DESKTOP\${PREV_APP}.lnk"
    ${EndIf}
    DeleteRegKey HKLM "${PREV_KEY}"
  ${EndIf}

  ; 2. Upgrading: close the window and turn the bridge off so files can be replaced.
  ${If} ${FileExists} "$INSTDIR\JellyScale.exe"
    DetailPrint "Closing JellyScale and turning the bridge off..."
    nsExec::ExecToLog '"$INSTDIR\JellyScale.exe" --stop'
    Pop $Result
    ${If} $Result != 0
      MessageBox MB_YESNO|MB_ICONEXCLAMATION|MB_DEFBUTTON2 "The installed JellyScale couldn't turn itself off (see the details).$\r$\n$\r$\nContinue anyway? If it was ON, the new version will restore your sleep settings the next time it opens." /SD IDNO IDYES stop_continue
      Abort "Couldn't turn the running JellyScale off. See the details above."
      stop_continue:
    ${EndIf}
  ${EndIf}

  ; 3. App files, uninstaller, Installed apps entry and shortcuts.
  SetOutPath "$INSTDIR"
  File "build\JellyScale.exe"
  File "THIRD-PARTY-NOTICES.txt"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APP}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\JellyScale.exe,0"
  WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
  CreateShortCut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\JellyScale.exe" "" "$INSTDIR\JellyScale.exe" 0 SW_SHOWNORMAL "" "Turn the JellyScale TV bridge on or off"
  CreateShortCut "$DESKTOP\${APP}.lnk" "$INSTDIR\JellyScale.exe" "" "$INSTDIR\JellyScale.exe" 0 SW_SHOWNORMAL "" "Turn the JellyScale TV bridge on or off"

  ; 4. Caddy, the firewall rule and (if upgrading from JellyHop or Jellyfin TV Bridge) the saved server.
  DetailPrint "Preparing Caddy and the firewall rule..."
  ${If} $AutoStart == ${BST_CHECKED}
    StrCpy $1 "on"
  ${Else}
    StrCpy $1 "off"
  ${EndIf}
  nsExec::ExecToLog '"$INSTDIR\JellyScale.exe" --setup --import "$PLUGINSDIR\legacy" --autostart $1'
  Pop $Result
  ${If} $Result != 0
    DetailPrint "Setup did not finish. JellyScale is installed but can't turn on until this is fixed."
    MessageBox MB_ICONEXCLAMATION "Setup couldn't finish preparing the bridge. The details are in the installer window.$\r$\n$\r$\nFix the problem and run JellyScale Setup again, or uninstall JellyScale from Installed apps." /SD IDOK
    Abort "Setup could not finish."
  ${EndIf}

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" "$0"
  DetailPrint "JellyScale is installed."
SectionEnd

Function un.onInit
  ${DisableX64FSRedirection}
  SetRegView 64
  SetShellVarContext all
FunctionEnd

Section "Uninstall"
  ${If} ${FileExists} "$INSTDIR\JellyScale.exe"
    DetailPrint "Turning JellyScale off and removing the firewall rule..."
    nsExec::ExecToLog '"$INSTDIR\JellyScale.exe" --uninstall'
    Pop $Result
    ${If} $Result != 0
      MessageBox MB_YESNO|MB_ICONEXCLAMATION|MB_DEFBUTTON2 "JellyScale couldn't turn itself off and restore everything (see the details).$\r$\n$\r$\nNo: keep JellyScale so you can fix it and try again (recommended).$\r$\nYes: remove it anyway. Sleep settings it changed stay as they are." /SD IDNO IDYES force_remove
      Abort "Uninstall could not finish. JellyScale and your power backup were kept."
      force_remove:
      nsExec::ExecToLog '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="JellyScale"'
      Pop $Result
      nsExec::ExecToLog '"$SYSDIR\schtasks.exe" /Delete /TN "${APP}" /F'
      Pop $Result
    ${EndIf}
  ${EndIf}
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$DESKTOP\${APP}.lnk"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKLM "${UNINST_KEY}"
SectionEnd
