; YuWangCloudSync installer implemented in NSIS with Modern UI 2.
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "Win\COM.nsh"
!include "nsDialogs.nsh"
!include "TextFunc.nsh"
!include "StrFunc.nsh"
${Using:StrFunc} UnStrRep
${Using:StrFunc} StrRep
${Using:StrFunc} StrStr
${Using:StrFunc} UnStrStr
!include "WordFunc.nsh"
!insertmacro VersionCompare

!define PRODUCT_NAME_ZH "@@PRODUCT_NAME_ZH@@"
!define PRODUCT_NAME_EN "@@PRODUCT_NAME_EN@@"
!define PRODUCT_IDENTIFIER "@@PRODUCT_IDENTIFIER@@"
!define PRODUCT_VERSION "@@PRODUCT_VERSION@@"
!define INSTALL_VARIANT "@@INSTALL_VARIANT@@"
!define UNINSTALL_KEY "SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_IDENTIFIER}"
!define SETUP_OUTPUT_PATH "@@SETUP_OUTPUT_PATH@@"
!define APP_PAYLOAD_DIRECTORY "@@APP_PAYLOAD_DIRECTORY@@"
!define SHELL_EXTENSION_FILE_NAME "@@SHELL_EXTENSION_FILE_NAME@@"
!define SHELL_EXTENSION_PATH "@@SHELL_EXTENSION_PATH@@"
!define INSTALLED_FILES_PATH "@@INSTALLED_FILES_PATH@@"
!define INSTALLED_DIRECTORIES_PATH "@@INSTALLED_DIRECTORIES_PATH@@"
!define SETUP_LOGIN_SCRIPT_PATH "@@SETUP_LOGIN_SCRIPT_PATH@@"
!define PRODUCT_ICON_PATH "@@PRODUCT_ICON_PATH@@"
!define PRODUCT_UNINSTALL_ICON_PATH "@@PRODUCT_UNINSTALL_ICON_PATH@@"
!define MUI_ICON "${PRODUCT_ICON_PATH}"
!define MUI_UNICON "${PRODUCT_UNINSTALL_ICON_PATH}"
!define MUI_CUSTOMFUNCTION_GUIINIT InstallerGuiInit
!define MUI_CUSTOMFUNCTION_UNGUIINIT un.UninstallerGuiInit
!include "MUI2.nsh"

Name "${PRODUCT_NAME_ZH}"
BrandingText "${PRODUCT_NAME_EN} ${PRODUCT_VERSION}"
OutFile "${SETUP_OUTPUT_PATH}"
InstallDir "$PROGRAMFILES64\${PRODUCT_IDENTIFIER}"
RequestExecutionLevel highest
ManifestDPIAware true
SetFont "Segoe UI" 9
SetCompressor zlib
VIProductVersion "${PRODUCT_VERSION}"
VIAddVersionKey "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "ProductName" "${PRODUCT_NAME_ZH}"
VIAddVersionKey "CompanyName" "${PRODUCT_NAME_EN}"
VIAddVersionKey "FileDescription" "${PRODUCT_NAME_ZH}安装程序"
VIAddVersionKey "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright © ${PRODUCT_NAME_EN}"
VIAddVersionKey "OriginalFilename" "@@SETUP_FILE_NAME@@"

!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "欢迎使用${PRODUCT_NAME_ZH}安装向导"
!define MUI_WELCOMEPAGE_TEXT "将安装${PRODUCT_NAME_ZH}。"
!define MUI_FINISHPAGE_TITLE "${PRODUCT_NAME_ZH}安装完成"
!define MUI_FINISHPAGE_TEXT "${PRODUCT_NAME_ZH}已安装。若资源管理器角标未出现，请重启资源管理器。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${PRODUCT_IDENTIFIER}.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动程序"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchAppOnFinish
!define MUI_FINISHPAGE_SHOWREADME "$SyncRoot"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "打开同步目录"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION OpenSyncFolder
!define MUI_UNWELCOMEPAGE_TITLE "欢迎使用${PRODUCT_NAME_ZH}卸载向导"
!define MUI_UNWELCOMEPAGE_TEXT "此向导将移除${PRODUCT_NAME_ZH}程序和桌面快捷方式。"
!define MUI_UNFINISHPAGE_TITLE "${PRODUCT_NAME_ZH}卸载完成"
!define MUI_UNFINISHPAGE_TEXT "${PRODUCT_NAME_ZH}已从此计算机中移除。"

!define MUI_PAGE_CUSTOMFUNCTION_PRE WelcomePagePre
!define MUI_PAGE_CUSTOMFUNCTION_SHOW WelcomePageShow
!insertmacro MUI_PAGE_WELCOME
PageEx custom
  PageCallbacks InstallDirectoryPage InstallDirectoryPageLeave
PageExEnd
PageEx custom
  PageCallbacks AccountPage AccountPageLeave
PageExEnd
PageEx custom
  PageCallbacks SyncRootPage SyncRootPageLeave
PageExEnd
PageEx custom
  PageCallbacks SummaryPage
PageExEnd
!define MUI_PAGE_CUSTOMFUNCTION_PRE InstallFilesPagePre
!insertmacro MUI_PAGE_INSTFILES
!define MUI_PAGE_CUSTOMFUNCTION_PRE FinishPagePre
!define MUI_PAGE_CUSTOMFUNCTION_SHOW FinishPageShow
!insertmacro MUI_PAGE_FINISH
PageEx custom
  PageCallbacks FailurePageShow FailurePageLeave
PageExEnd

!define MUI_PAGE_CUSTOMFUNCTION_SHOW un.UninstallerPageShow
!insertmacro MUI_UNPAGE_WELCOME
UninstPage custom un.UninstallOptionsPage un.UninstallOptionsLeave
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!define MUI_PAGE_CUSTOMFUNCTION_SHOW un.UninstallerPageShow
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"

Var PageDialog
Var InstallDirectory
Var PreviousInstallDirectory
Var PreviousShellExtensionFileName
Var PreviousVersion
Var IsInstalled
Var QuickUpdateRequested
Var InstallationBlocked
Var FailureMessage
Var UserSid
Var CurrentToken
Var TokenInfoLength
Var InstallerHeaderFont
Var InstallerPageTitleFont
Var Server
Var Username
Var Password
Var SyncRoot
Var CreateDesktopShortcut
Var KeepDownloadedFiles
Var KeepUserConfiguration
Var InstallDirectoryEdit
Var InstallDirectoryBrowse
Var ServerEdit
Var UsernameEdit
Var PasswordEdit
Var CaptchaCode
Var CaptchaEdit
Var CaptchaButton
Var CaptchaBitmap
Var CaptchaImageHandle
Var CaptchaFetched
Var CaptchaServerInput
Var CaptchaServerBase
Var CaptchaTargetHeight
Var ServerInputUpdateSuppressed
Var SessionUsername
Var SyncRootEdit
Var SyncRootBrowse
Var DesktopShortcutCheckbox
Var KeepDownloadedCheckbox
Var KeepConfigurationCheckbox
Var SummaryText
Var RelativePathValid
Var RelativePathFull
Var ManifestSearchFile
Var ManifestSearchPath
Var ManifestPathFound
Var PreviousManifestHandle
Var NewInstalledFilesManifest
Var NewInstalledDirectoriesManifest
Var InstalledFileCount
Var TreeScanResult
Var ProtectedPassword
Var ConfigPath
Var ConfigDirectory
Var JsonValue
Var ServerAuthority
Var ServerPath
Var ServerScheme
Var ServerProbeUrl
Var SensitivePath
Var SensitiveRoot
Var SensitivePathKind

!macro CHECK_SENSITIVE_ROOT TAG ENVIRONMENT ALLOW_CHILDREN
  ExpandEnvStrings $R1 "%${ENVIRONMENT}%"
  StrCmp $R1 "" sensitive_root_next_${TAG}
  GetFullPathName $R1 "$R1"
  StrLen $R2 $R1
  IntCmp $R2 3 sensitive_root_normalized_${TAG}
  StrCpy $R3 $R1 1 -1
  StrCmp $R3 "\" 0 sensitive_root_normalized_${TAG}
  StrCpy $R1 $R1 -1
sensitive_root_normalized_${TAG}:
  StrCmp $R0 $R1 sensitive_root_found
!if ${ALLOW_CHILDREN} == 0
  StrLen $R2 $R1
  StrCpy $R3 $R0 $R2
  StrCmp $R3 $R1 0 sensitive_root_next_${TAG}
  StrCpy $R3 $R0 1 $R2
  StrCmp $R3 "\" sensitive_root_found sensitive_root_next_${TAG}
!endif
sensitive_root_next_${TAG}:
!macroend

!macro CHECK_ALLOWED_INSTALL_BASE TAG ENVIRONMENT
  ExpandEnvStrings $R1 "%${ENVIRONMENT}%"
  StrCmp $R1 "" install_base_next_${TAG}
  GetFullPathName $R1 "$R1"
  StrLen $R2 $R1
  IntCmp $R2 3 install_base_normalized_${TAG}
  StrCpy $R3 $R1 1 -1
  StrCmp $R3 "\" 0 install_base_normalized_${TAG}
  StrCpy $R1 $R1 -1
install_base_normalized_${TAG}:
  StrCmp $R0 $R1 install_base_found
install_base_next_${TAG}:
!macroend

Function FindSensitiveRoot
  StrCpy $R9 0
  StrCpy $SensitiveRoot ""
  StrCpy $R0 "$SensitivePath"
  StrLen $R2 $R0
  IntCmp $R2 3 sensitive_path_normalized
  StrCpy $R3 $R0 1 -1
  StrCmp $R3 "\" 0 sensitive_path_normalized
  StrCpy $R0 $R0 -1
sensitive_path_normalized:
  ${If} $SensitivePathKind == "install"
    !insertmacro CHECK_SENSITIVE_ROOT "programfiles_install" "PROGRAMFILES" 1
    !insertmacro CHECK_SENSITIVE_ROOT "programfiles_x86_install" "PROGRAMFILES(X86)" 1
  ${Else}
    !insertmacro CHECK_SENSITIVE_ROOT "programfiles_sync" "PROGRAMFILES" 0
    !insertmacro CHECK_SENSITIVE_ROOT "programfiles_x86_sync" "PROGRAMFILES(X86)" 0
  ${EndIf}
  !insertmacro CHECK_SENSITIVE_ROOT "appdata" "APPDATA" 0
  !insertmacro CHECK_SENSITIVE_ROOT "localappdata" "LOCALAPPDATA" 0
  !insertmacro CHECK_SENSITIVE_ROOT "commonfiles" "CommonProgramFiles" 0
  !insertmacro CHECK_SENSITIVE_ROOT "commonfiles_x86" "CommonProgramFiles(x86)" 0
  !insertmacro CHECK_SENSITIVE_ROOT "programdata" "PROGRAMDATA" 0
  !insertmacro CHECK_SENSITIVE_ROOT "public" "PUBLIC" 0
  !insertmacro CHECK_SENSITIVE_ROOT "systemroot" "SYSTEMROOT" 0
  !insertmacro CHECK_SENSITIVE_ROOT "windir" "WINDIR" 0
  !insertmacro CHECK_SENSITIVE_ROOT "temp" "TEMP" 0
  !insertmacro CHECK_SENSITIVE_ROOT "tmp" "TMP" 0
  Goto sensitive_root_done
sensitive_root_found:
  StrCpy $SensitiveRoot $R1
  StrCpy $SensitivePath $R0
  StrCpy $R9 1
sensitive_root_done:
FunctionEnd

Function IsAllowedInstallBase
  StrCpy $R8 0
  !insertmacro CHECK_ALLOWED_INSTALL_BASE "programfiles" "PROGRAMFILES"
  !insertmacro CHECK_ALLOWED_INSTALL_BASE "programfiles_x86" "PROGRAMFILES(X86)"
  Goto install_base_done
install_base_found:
  StrCpy $R8 1
install_base_done:
FunctionEnd

Function .onInit
  StrCpy $QuickUpdateRequested "0"
  ${GetParameters} $R0
  ${GetOptions} $R0 "-quick-update" $R1
  IfErrors quick_update_argument_missing
  ${If} $R1 == ""
  StrCpy $QuickUpdateRequested "1"
  ${EndIf}
quick_update_argument_missing:
  ClearErrors

  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=setup-login.ps1 "${SETUP_LOGIN_SCRIPT_PATH}"
  StrCpy $CaptchaImageHandle "0"
  StrCpy $CaptchaFetched "0"
  StrCpy $CaptchaServerInput ""
  StrCpy $CaptchaServerBase ""
  SetRegView 64
  SetShellVarContext current
  StrCpy $InstallDirectory "$PROGRAMFILES64\${PRODUCT_IDENTIFIER}"
  StrCpy $SyncRoot "$PROFILE\YuWangCloud"
  StrCpy $CreateDesktopShortcut "1"
  StrCpy $IsInstalled "0"
  StrCpy $InstallationBlocked "0"
  StrCpy $PreviousShellExtensionFileName "${PRODUCT_IDENTIFIER}ShellExt.dll"

  ReadRegStr $PreviousInstallDirectory HKLM "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $PreviousInstallDirectory != ""
  ${AndIf} ${FileExists} "$PreviousInstallDirectory\${PRODUCT_IDENTIFIER}.exe"
  ${AndIf} ${FileExists} "$PreviousInstallDirectory\uninstall.exe"
  ${AndIf} ${FileExists} "$PreviousInstallDirectory\installed-files.txt"
  ${AndIf} ${FileExists} "$PreviousInstallDirectory\installed-directories.txt"
    StrCpy $IsInstalled "1"
    StrCpy $InstallDirectory $PreviousInstallDirectory
    ReadRegStr $R1 HKLM "${UNINSTALL_KEY}" "ShellExtensionDll"
    ${If} $R1 != ""
      Call ValidateShellExtensionFileName
      ${If} $R0 == "1"
        StrCpy $PreviousShellExtensionFileName $R1
      ${EndIf}
    ${EndIf}
    ReadRegStr $PreviousVersion HKLM "${UNINSTALL_KEY}" "DisplayVersion"
    ${If} $PreviousVersion != ""
      ${VersionCompare} "${PRODUCT_VERSION}" "$PreviousVersion" $R0
      ${If} $R0 == "2"
        StrCpy $InstallationBlocked "1"
        StrCpy $FailureMessage "不能降级安装。请使用不低于当前已安装版本的安装程序。"
        MessageBox MB_OK|MB_ICONSTOP "不能降级安装。"
      ${EndIf}
    ${EndIf}
  ${EndIf}
  StrCpy $INSTDIR $InstallDirectory

  ${If} $QuickUpdateRequested == "1"
  ${AndIf} $IsInstalled == "1"
  ${AndIf} $InstallationBlocked != "1"
    SetAutoClose true
  ${EndIf}

  ${If} $InstallationBlocked != "1"
    UserInfo::GetAccountType
    Pop $R0
    ${If} $R0 != "Admin"
      MessageBox MB_OK|MB_ICONSTOP "请使用当前 Windows 用户的管理员权限运行安装程序。"
      StrCpy $InstallationBlocked "1"
      StrCpy $FailureMessage "请使用当前 Windows 用户的管理员权限运行安装程序。"
    ${Else}
      Call GetCurrentSid
      ${If} $UserSid == ""
        MessageBox MB_OK|MB_ICONSTOP "无法确认当前 Windows 用户身份。"
        StrCpy $InstallationBlocked "1"
        StrCpy $FailureMessage "无法确认当前 Windows 用户身份。"
      ${Else}
        ${If} $IsInstalled == "1"
          ReadRegStr $R1 HKLM "${UNINSTALL_KEY}" "UserSid"
          ${If} $R1 != ""
          ${AndIf} $R1 != $UserSid
            MessageBox MB_OK|MB_ICONSTOP "请使用原安装 Windows 用户的管理员权限运行安装程序。"
            StrCpy $InstallationBlocked "1"
            StrCpy $FailureMessage "请使用原安装 Windows 用户的管理员权限运行安装程序。"
          ${EndIf}
        ${EndIf}
      ${EndIf}
    ${EndIf}
  ${EndIf}
FunctionEnd

Function ValidateShellExtensionFileName
  StrCpy $R0 "0"
  StrLen $R2 "${PRODUCT_IDENTIFIER}ShellExt"
  StrCpy $R3 $R1 $R2
  StrCmp $R3 "${PRODUCT_IDENTIFIER}ShellExt" 0 validate_shell_name_done
  StrLen $R2 $R1
  IntOp $R2 $R2 - 4
  IntCmp $R2 0 validate_shell_name_done validate_shell_name_done
  StrCpy $R3 $R1 4 $R2
  StrCmp $R3 ".dll" 0 validate_shell_name_done
  StrCpy $R2 "0"
validate_shell_name_chars:
  StrCpy $R3 $R1 1 $R2
  StrCmp $R3 "" validate_shell_name_valid
  ${StrStr} $R4 "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-." "$R3"
  StrCmp $R4 "" validate_shell_name_done
  IntOp $R2 $R2 + 1
  Goto validate_shell_name_chars
validate_shell_name_valid:
  StrCpy $R0 "1"
validate_shell_name_done:
FunctionEnd

Function GetCurrentSid
  StrCpy $UserSid ""
  System::Call 'kernel32::GetCurrentProcess() p.R0'
  System::Alloc 4
  Pop $R1
  System::Call 'advapi32::OpenProcessToken(p $R0, i 0x0008, p $R1)i.R2'
  StrCmp $R2 0 get_sid_free_token_slot
  System::Call '*$R1(p.R2)'
  StrCpy $CurrentToken $R2
  System::Alloc 4
  Pop $R2
  System::Call 'advapi32::GetTokenInformation(p $CurrentToken, i 1, p 0, i 0, p $R2)i.R3'
  System::Call '*$R2(i.R3)'
  IntCmp $R3 0 get_sid_free_length get_sid_allocate get_sid_allocate
get_sid_allocate:
  StrCpy $TokenInfoLength $R3
  System::Alloc $TokenInfoLength
  Pop $R3
  System::Call 'advapi32::GetTokenInformation(p $CurrentToken, i 1, p $R3, i $TokenInfoLength, p $R2)i.R4'
  StrCmp $R4 0 get_sid_free_buffer
  System::Call '*$R3(p.R4, i.R5)'
  System::Alloc 4
  Pop $R6
  System::Call 'advapi32::ConvertSidToStringSidW(p $R4, p $R6)i.R7'
  StrCmp $R7 0 get_sid_free_output_slot
  System::Call '*$R6(p.R8)'
  System::Call '*$R8(&t1024 .R9)'
  StrCpy $UserSid $R9
  System::Call 'kernel32::LocalFree(p $R8) p.R0'
get_sid_free_output_slot:
  System::Free $R6
get_sid_free_buffer:
  System::Free $R3
get_sid_free_length:
  System::Free $R2
  System::Call 'kernel32::CloseHandle(p $CurrentToken) i.R0'
get_sid_free_token_slot:
  System::Free $R1
FunctionEnd

Function WelcomePagePre
  ${If} $InstallationBlocked == "1"
    Abort
  ${EndIf}
  ${If} $QuickUpdateRequested == "1"
  ${AndIf} $IsInstalled == "1"
    Abort
  ${EndIf}
FunctionEnd

Function InstallerGuiInit
  CreateFont $InstallerHeaderFont "Microsoft YaHei UI" 9 700
  SendMessage $mui.Header.Text ${WM_SETFONT} $InstallerHeaderFont 1
  CreateFont $InstallerPageTitleFont "Microsoft YaHei UI" 12 700
FunctionEnd

Function un.UninstallerGuiInit
  CreateFont $InstallerHeaderFont "Microsoft YaHei UI" 9 700
  SendMessage $mui.Header.Text ${WM_SETFONT} $InstallerHeaderFont 1
  CreateFont $InstallerPageTitleFont "Microsoft YaHei UI" 12 700
FunctionEnd

Function WelcomePageShow
  SendMessage $mui.WelcomePage.Title ${WM_SETFONT} $InstallerPageTitleFont 1
  ${If} $IsInstalled == "1"
    SendMessage $mui.WelcomePage.Text ${WM_SETTEXT} 0 "STR:将安装${PRODUCT_NAME_ZH}。继续以升级程序，升级会保留已有同步内容。"
  ${EndIf}
FunctionEnd

Function un.UninstallerPageShow
  System::Call 'user32::IsWindow(p $mui.WelcomePage.Title)i.R0'
  ${If} $R0 != 0
    SendMessage $mui.WelcomePage.Title ${WM_SETFONT} $InstallerPageTitleFont 1
  ${EndIf}
  System::Call 'user32::IsWindow(p $mui.FinishPage.Title)i.R0'
  ${If} $R0 != 0
    SendMessage $mui.FinishPage.Title ${WM_SETFONT} $InstallerPageTitleFont 1
  ${EndIf}
FunctionEnd

!macro SKIP_CONFIGURATION_PAGE
  ${If} $IsInstalled == "1"
  ${OrIf} $InstallationBlocked == "1"
    Abort
  ${EndIf}
!macroend

Function InstallFilesPagePre
  ${If} $InstallationBlocked == "1"
    Abort
  ${EndIf}
FunctionEnd

Function FinishPagePre
  ${If} $InstallationBlocked == "1"
    Abort
  ${EndIf}
  ${If} $QuickUpdateRequested == "1"
  ${AndIf} $IsInstalled == "1"
    Abort
  ${EndIf}
FunctionEnd

Function .onInstSuccess
  ${If} $QuickUpdateRequested == "1"
  ${AndIf} $IsInstalled == "1"
    ExecShell "open" "$INSTDIR\${PRODUCT_IDENTIFIER}.exe" "--cleanup-update-temp -about"
  ${EndIf}
FunctionEnd

Function FailurePageShow
  ${If} $InstallationBlocked != "1"
    Abort
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "安装失败" "安装程序无法继续。"
  nsDialogs::Create 1018
  Pop $PageDialog
  ${If} $PageDialog == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 32u "$FailureMessage"
  Pop $0
  SendMessage $mui.Button.Next ${WM_SETTEXT} 0 "STR:完成"
  nsDialogs::Show
FunctionEnd

Function FailurePageLeave
FunctionEnd

Function InstallDirectoryPage
  !insertmacro SKIP_CONFIGURATION_PAGE
  !insertmacro MUI_HEADER_TEXT "选择程序安装位置" "选择程序文件所在的本地目录。"
  nsDialogs::Create 1018
  Pop $PageDialog
  ${If} $PageDialog == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 14u "程序文件将安装到以下目录："
  Pop $0
  ${NSD_CreateText} 0 20u 78% 13u "$InstallDirectory"
  Pop $InstallDirectoryEdit
  ${NSD_CreateButton} 81% 19u 19% 15u "浏览"
  Pop $InstallDirectoryBrowse
  ${NSD_OnClick} $InstallDirectoryBrowse BrowseInstallDirectory
  nsDialogs::Show
FunctionEnd

Function BrowseInstallDirectory
  nsDialogs::SelectFolderDialog "选择程序安装目录" "$InstallDirectory"
  Pop $0
  ${If} $0 != ""
    ${NSD_SetText} $InstallDirectoryEdit $0
  ${EndIf}
FunctionEnd

Function InstallDirectoryPageLeave
  ${NSD_GetText} $InstallDirectoryEdit $InstallDirectory
  Call ValidateInstallDirectory
  ${If} $R9 != 0
    Abort
  ${EndIf}
  StrCpy $INSTDIR $InstallDirectory
FunctionEnd

Function ValidateInstallDirectory
  StrCpy $R9 1
  ${If} $InstallDirectory == ""
    MessageBox MB_OK|MB_ICONEXCLAMATION "请选择程序安装目录。"
    Return
  ${EndIf}
  System::Call 'kernel32::GetFullPathNameW(w "$InstallDirectory", i 1024, w .R0, p 0)i.R1'
  ${If} $R1 == 0
    Goto install_directory_path_failed
  ${EndIf}
  ${If} $R1 >= 1024
    Goto install_directory_path_failed
  ${EndIf}
  ${If} $R0 == ""
    Goto install_directory_path_failed
  ${EndIf}
  StrCpy $R1 $R0 2
  ${If} $R1 == "\\"
    MessageBox MB_OK|MB_ICONEXCLAMATION "程序安装目录必须位于本地磁盘。"
    Return
  ${EndIf}
  Call IsAllowedInstallBase
  StrCpy $R1 $R0 3
  ${If} $R0 == $R1
  ${AndIf} $R8 != 1
    MessageBox MB_OK|MB_ICONEXCLAMATION "不能使用磁盘根目录。"
    Return
  ${EndIf}
  System::Call 'kernel32::GetFileAttributesW(w "$R0") i.R1'
  ${If} $R1 != -1
    IntOp $R2 $R1 & 0x10
    ${If} $R2 == 0
      MessageBox MB_OK|MB_ICONEXCLAMATION "请选择一个文件夹。"
      Return
    ${EndIf}
  ${EndIf}
  StrCpy $InstallDirectory $R0
  ${If} $R8 != 1
    StrCpy $SensitivePath $R0
    StrCpy $SensitivePathKind "install"
    Call FindSensitiveRoot
    ${If} $R9 == 1
      MessageBox MB_OK|MB_ICONSTOP "位置位于系统敏感目录：$\r$\n$SensitivePath$\r$\n（位于 $SensitiveRoot 下）。"
      Return
    ${EndIf}
  ${EndIf}
  ClearErrors
  CreateDirectory "$R0"
  IfErrors install_directory_create_failed
  StrCpy $R9 0
  Return
install_directory_path_failed:
  MessageBox MB_OK|MB_ICONEXCLAMATION "无法解析程序安装目录路径：$InstallDirectory"
  Return
install_directory_create_failed:
  MessageBox MB_OK|MB_ICONEXCLAMATION "无法创建程序安装目录：$R0。请检查目录路径和权限后重试。"
FunctionEnd

Function AccountPage
  !insertmacro SKIP_CONFIGURATION_PAGE
  ${NSD_FreeBitmap} $CaptchaImageHandle
  StrCpy $CaptchaFetched "0"
  StrCpy $CaptchaCode ""
  StrCpy $CaptchaServerInput ""
  StrCpy $CaptchaServerBase ""
  StrCpy $CaptchaImageHandle "0"
  Delete "$PLUGINSDIR\setup-login-input.txt"
  Delete "$PLUGINSDIR\setup-login-session.txt"
  Delete "$PLUGINSDIR\setup-captcha.bmp"
  !insertmacro MUI_HEADER_TEXT "输入服务器账号" "密码将加密保存。"
  nsDialogs::Create 1018
  Pop $PageDialog
  ${If} $PageDialog == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 12u "服务器地址（IP 地址或域名）"
  Pop $0
  ${NSD_CreateText} 0 14u 100% 13u "$Server"
  Pop $ServerEdit
  ${NSD_CreateLabel} 0 30u 100% 12u "账号"
  Pop $0
  ${NSD_CreateText} 0 43u 100% 13u "$Username"
  Pop $UsernameEdit
  ${NSD_CreateLabel} 0 60u 100% 12u "密码"
  Pop $0
  ${NSD_CreatePassword} 0 73u 100% 13u "$Password"
  Pop $PasswordEdit
  SendMessage $PasswordEdit ${EM_LIMITTEXT} 200 0
  ${NSD_CreateLabel} 0 90u 100% 12u "验证码"
  Pop $0
  ${NSD_CreateText} 0 103u 35% 13u "$CaptchaCode"
  Pop $CaptchaEdit
  SendMessage $CaptchaEdit ${EM_LIMITTEXT} 64 0
  ${NSD_CreateButton} 38% 103u 27% 13u "获取验证码"
  Pop $CaptchaButton
  ${NSD_OnClick} $CaptchaButton GetInstallerCaptcha
  ${NSD_CreateBitmap} 68% 103u 32% 13u ""
  Pop $CaptchaBitmap
  ${NSD_OnChange} $ServerEdit InvalidateInstallerCaptcha
  nsDialogs::Show
FunctionEnd

Function AccountPageLeave
  ${NSD_GetText} $ServerEdit $Server
  ${NSD_GetText} $UsernameEdit $Username
  ${NSD_GetText} $PasswordEdit $Password
  ${NSD_GetText} $CaptchaEdit $CaptchaCode
  ${If} $Server == ""
  ${OrIf} $Username == ""
  ${OrIf} $Password == ""
    MessageBox MB_OK|MB_ICONEXCLAMATION "服务器地址、账号和密码不能为空。"
    Abort
  ${EndIf}
  ${If} $CaptchaCode == ""
    MessageBox MB_OK|MB_ICONEXCLAMATION "请先获取并输入验证码。"
    Abort
  ${EndIf}
  ${If} $CaptchaFetched != "1"
    MessageBox MB_OK|MB_ICONEXCLAMATION "请先点击“获取验证码”。"
    Abort
  ${EndIf}
  ${If} $Server != $CaptchaServerInput
    Call InvalidateInstallerCaptcha
    MessageBox MB_OK|MB_ICONEXCLAMATION "服务器地址已更改，请重新获取验证码。"
    Abort
  ${EndIf}
  StrCpy $Server $CaptchaServerBase
  Call WriteInstallerLoginInput
  nsExec::ExecToStack /TIMEOUT=45000 '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\setup-login.ps1" -Operation Login -InputPath "$PLUGINSDIR\setup-login-input.txt" -SessionPath "$PLUGINSDIR\setup-login-session.txt" -ImagePath "$PLUGINSDIR\setup-captcha.bmp"'
  Pop $R9
  Pop $R8
  Delete "$PLUGINSDIR\setup-login-input.txt"
  ${If} $R9 != "0"
    Call InvalidateInstallerCaptcha
    ${Switch} $R9
      ${Case} "1"
        MessageBox MB_OK|MB_ICONEXCLAMATION "账密或验证码不正确"
        ${Break}
      ${Case} "30"
      ${Case} "timeout"
        MessageBox MB_OK|MB_ICONEXCLAMATION "登录请求超时。请检查服务器地址、端口及网络连接。"
        ${Break}
      ${Case} "31"
        MessageBox MB_OK|MB_ICONEXCLAMATION "无法连接登录服务器。请检查服务器地址、端口及网络连接。"
        ${Break}
      ${Case} "32"
        MessageBox MB_OK|MB_ICONEXCLAMATION "无法建立 HTTPS 安全连接。请检查服务器证书和系统时间。"
        ${Break}
      ${Case} "33"
        MessageBox MB_OK|MB_ICONEXCLAMATION "登录服务器返回 HTTP 错误。请确认服务器地址、端口及服务状态。"
        ${Break}
      ${Case} "34"
        MessageBox MB_OK|MB_ICONEXCLAMATION "登录已通过，但无法保存登录会话。请重新获取验证码后重试。"
        ${Break}
      ${Case} "35"
        MessageBox MB_OK|MB_ICONEXCLAMATION "无法处理登录服务器响应，请确认服务器地址和登录服务状态。"
        ${Break}
      ${Default}
        MessageBox MB_OK|MB_ICONEXCLAMATION "登录请求失败（错误代码：$R9）。请检查服务器地址、端口和服务状态。"
    ${EndSwitch}
    Abort
  ${EndIf}
  StrCpy $CaptchaFetched "0"
FunctionEnd

Function WriteInstallerLoginInput
  FileOpen $R0 "$PLUGINSDIR\setup-login-input.txt" w
  ${If} $R0 == ""
    Return
  ${EndIf}
  FileWriteUTF16LE /BOM $R0 "$Server$\r$\n$Username$\r$\n$Password$\r$\n$CaptchaCode"
  FileClose $R0
FunctionEnd

Function GetInstallerCaptcha
  ${NSD_GetText} $ServerEdit $Server
  Call InvalidateInstallerCaptcha
  Call ValidateServer
  ${If} $R9 != 0
    Return
  ${EndIf}
  StrCpy $CaptchaTargetHeight "0"
  System::Call 'USER32::GetWindowRect(p$CaptchaBitmap,@r0)i.r1'
  ${If} $1 != 0
    System::Call '*$0(i,i.r2,i,i.r3)'
    IntOp $CaptchaTargetHeight $3 - $2
  ${EndIf}
  ${If} $CaptchaTargetHeight <= 0
    ; Map the control's 13 dialog units through this page's current font and DPI.
    System::Call '*(i 0,i 0,i 0,i 13)i.R0'
    System::Call 'USER32::MapDialogRect(p$PageDialog,p$R0)i.R1'
    ${If} $R1 != 0
      System::Call '*$R0(i,i,i.R2,i.R3)'
      StrCpy $CaptchaTargetHeight $R3
    ${EndIf}
    System::Free $R0
  ${EndIf}
  ${If} $CaptchaTargetHeight <= 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "验证码图片控件尺寸无效，请重新打开安装向导。"
    Return
  ${EndIf}
  ${NSD_GetText} $ServerEdit $CaptchaServerInput
  FileOpen $R0 "$PLUGINSDIR\setup-login-input.txt" w
  ${If} $R0 == ""
    MessageBox MB_OK|MB_ICONEXCLAMATION "无法创建验证码临时文件，请检查临时目录写入权限。"
    Return
  ${EndIf}
  FileWriteUTF16LE /BOM $R0 "$Server"
  FileClose $R0
  nsExec::ExecToStack /TIMEOUT=45000 '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\setup-login.ps1" -Operation GetCaptcha -InputPath "$PLUGINSDIR\setup-login-input.txt" -SessionPath "$PLUGINSDIR\setup-login-session.txt" -ImagePath "$PLUGINSDIR\setup-captcha.bmp" -TargetHeight $CaptchaTargetHeight'
  Pop $R9
  Pop $R8
  Delete "$PLUGINSDIR\setup-login-input.txt"
  ${If} $R9 != "0"
  ${OrIfNot} ${FileExists} "$PLUGINSDIR\setup-captcha.bmp"
    Delete "$PLUGINSDIR\setup-login-session.txt"
    Delete "$PLUGINSDIR\setup-captcha.bmp"
    ${If} $R9 == "0"
      StrCpy $R9 "25"
    ${EndIf}
    Call ShowInstallerCaptchaError
    Return
  ${EndIf}
  ; The helper may discover a non-default port; use that address for login and configuration.
  StrCpy $R1 ""
  FileOpen $R0 "$PLUGINSDIR\setup-login-session.txt" r
  ${If} $R0 != ""
    FileSeek $R0 2 SET ; Skip the UTF-16LE BOM.
    FileReadUTF16LE $R0 $R1
    FileClose $R0
  ${EndIf}
  ${TrimNewLines} $R1 $Server
  ${If} $Server == ""
    Call InvalidateInstallerCaptcha
    StrCpy $R9 "26"
    Call ShowInstallerCaptchaError
    Return
  ${EndIf}
  StrCpy $CaptchaServerBase $Server
  StrCpy $CaptchaServerInput $Server
  StrCpy $ServerInputUpdateSuppressed "1"
  ${NSD_SetText} $ServerEdit "$Server"
  StrCpy $ServerInputUpdateSuppressed "0"
  ${NSD_SetStretchedBitmap} $CaptchaBitmap "$PLUGINSDIR\setup-captcha.bmp" $CaptchaImageHandle
  ${If} $CaptchaImageHandle == "0"
    Call InvalidateInstallerCaptcha
    MessageBox MB_OK|MB_ICONEXCLAMATION "验证码图片加载失败，请重新获取。"
    Return
  ${EndIf}
  StrCpy $CaptchaFetched "1"
FunctionEnd

Function ShowInstallerCaptchaError
  ${Switch} $R9
    ${Case} "20"
    ${Case} "timeout"
      MessageBox MB_OK|MB_ICONEXCLAMATION "连接服务器或等待响应超时。请检查服务器地址、端口及网络连接。"
      ${Break}
    ${Case} "21"
      MessageBox MB_OK|MB_ICONEXCLAMATION "无法连接服务器。请检查服务器地址、端口及网络连接。"
      ${Break}
    ${Case} "22"
      MessageBox MB_OK|MB_ICONEXCLAMATION "无法建立 HTTPS 安全连接。请检查服务器证书和系统时间。"
      ${Break}
    ${Case} "23"
      MessageBox MB_OK|MB_ICONEXCLAMATION "服务器返回 HTTP 错误。请确认完整地址、端口及服务状态。"
      ${Break}
    ${Case} "24"
      MessageBox MB_OK|MB_ICONEXCLAMATION "无法处理验证码临时文件。请检查临时目录权限和系统组件。"
      ${Break}
    ${Case} "25"
      MessageBox MB_OK|MB_ICONEXCLAMATION "服务器返回的内容不是有效验证码图片。请确认完整地址和端口。"
      ${Break}
    ${Case} "26"
      MessageBox MB_OK|MB_ICONEXCLAMATION "服务器未返回有效登录会话，请确认服务器地址并重新获取。"
      ${Break}
    ${Case} "27"
      MessageBox MB_OK|MB_ICONEXCLAMATION "验证码请求参数无效，请重新输入服务器地址。"
      ${Break}
    ${Case} "28"
      MessageBox MB_OK|MB_ICONEXCLAMATION "无法打开服务器登录页。请检查服务器地址、端口及网络连接。"
      ${Break}
    ${Case} "29"
      MessageBox MB_OK|MB_ICONEXCLAMATION "验证码下载失败。请检查服务器地址、端口及服务状态。"
      ${Break}
    ${Case} "error"
      MessageBox MB_OK|MB_ICONEXCLAMATION "无法启动验证码处理程序，请检查 Windows PowerShell 是否可用。"
      ${Break}
    ${Default}
      MessageBox MB_OK|MB_ICONEXCLAMATION "获取验证码失败（错误代码：$R9）。"
  ${EndSwitch}
FunctionEnd

Function InvalidateInstallerCaptcha
  ${If} $ServerInputUpdateSuppressed == "1"
    Return
  ${EndIf}
  StrCpy $CaptchaFetched "0"
  StrCpy $CaptchaCode ""
  StrCpy $CaptchaServerInput ""
  StrCpy $CaptchaServerBase ""
  Delete "$PLUGINSDIR\setup-login-session.txt"
  Delete "$PLUGINSDIR\setup-captcha.bmp"
  ${If} $CaptchaEdit != ""
    ${NSD_SetText} $CaptchaEdit ""
  ${EndIf}
  ${If} $CaptchaBitmap != ""
    ${NSD_ClearBitmap} $CaptchaBitmap
  ${EndIf}
  ${NSD_FreeBitmap} $CaptchaImageHandle
  StrCpy $CaptchaImageHandle "0"
FunctionEnd

Function ValidateServer
  StrCpy $R9 1
  StrCpy $ServerAuthority $Server
  StrCpy $R0 $Server 7
  ${If} $R0 == "http://"
    StrCpy $ServerAuthority $Server "" 7
  ${Else}
    StrCpy $R0 $Server 8
    ${If} $R0 == "https://"
      StrCpy $ServerAuthority $Server "" 8
    ${Else}
      ${StrStr} $R1 $Server "://"
      ${If} $R1 != ""
        MessageBox MB_OK|MB_ICONEXCLAMATION "请输入有效的 IP 地址或域名（或完整 http(s) 地址）。"
        Return
      ${EndIf}
    ${EndIf}
  ${EndIf}
  ${If} $ServerAuthority == ""
    MessageBox MB_OK|MB_ICONEXCLAMATION "请输入有效的 IP 地址或域名（或完整 http(s) 地址）。"
    Return
  ${EndIf}
  ${StrStr} $R1 $ServerAuthority "@"
  ${If} $R1 != ""
    MessageBox MB_OK|MB_ICONEXCLAMATION "服务器地址中不能包含账密。"
    Return
  ${EndIf}

  ; Extract the host so IPv4 literals can bypass the HTTPS probe.
  StrLen $R0 $ServerAuthority
  StrCpy $R1 0
server_host_scan:
  IntCmp $R1 $R0 server_host_scan_done server_host_scan_char server_host_scan_done
server_host_scan_char:
  StrCpy $R2 $ServerAuthority 1 $R1
  StrCmp $R2 "/" server_host_scan_done
  StrCmp $R2 "?" server_host_scan_done
  StrCmp $R2 "#" server_host_scan_done
  IntOp $R1 $R1 + 1
  Goto server_host_scan
server_host_scan_done:
  StrCpy $ServerPath $ServerAuthority $R1
  ${If} $ServerPath == ""
    MessageBox MB_OK|MB_ICONEXCLAMATION "请输入有效的 IP 地址或域名（或完整 http(s) 地址）。"
    Return
  ${EndIf}

  ; Recognize IPv6 literals too. Bare IPv6 input is bracketed when building the URL.
  StrCpy $R5 "0"
  StrCpy $R6 "0"
  StrCpy $R7 "1"
  StrCpy $R8 $ServerPath
  StrCpy $R0 $ServerPath 1
  ${If} $R0 == "["
    ${StrStr} $R2 $ServerPath "]"
    ${If} $R2 == ""
      StrCpy $R7 "0"
    ${Else}
      StrLen $R3 $ServerPath
      StrLen $R4 $R2
      IntOp $R3 $R3 - $R4
      IntOp $R4 $R3 - 1
      StrCpy $R8 $ServerPath $R4 1
      StrCpy $R5 "1"
    ${EndIf}
  ${EndIf}
  StrLen $R0 $R8
  StrCpy $R4 0
server_ipv6_scan:
  IntCmp $R4 $R0 server_ipv6_scan_done server_ipv6_scan_char server_ipv6_scan_done
server_ipv6_scan_char:
  StrCpy $R3 $R8 1 $R4
  ${StrStr} $R2 "0123456789abcdefABCDEF:." "$R3"
  StrCmp $R2 "" server_ipv6_not_candidate
  StrCmp $R3 ":" server_ipv6_count_colon server_ipv6_next
server_ipv6_count_colon:
  IntOp $R6 $R6 + 1
server_ipv6_next:
  IntOp $R4 $R4 + 1
  Goto server_ipv6_scan
server_ipv6_not_candidate:
  StrCpy $R7 "0"
  Goto server_ipv6_scan_done
server_ipv6_scan_done:
  ${If} $R7 == "1"
    IntCmp $R6 2 server_ipv6_valid server_ipv6_not_ip server_ipv6_not_ip
server_ipv6_valid:
    ${If} $R5 != "1"
      StrLen $R3 $ServerPath
      StrCpy $R4 $ServerAuthority "" $R3
      StrCpy $ServerAuthority "[$ServerPath]$R4"
    ${EndIf}
    StrCpy $ServerScheme "http"
    Goto server_protocol_selected
server_ipv6_not_ip:
  ${EndIf}

  ; A single colon after an IPv4 literal introduces its optional port.
  ${StrStr} $R2 $ServerPath ":"
  ${If} $R2 != ""
    StrLen $R3 $ServerPath
    StrLen $R4 $R2
    IntOp $R3 $R3 - $R4
    StrCpy $ServerPath $ServerPath $R3
  ${EndIf}
  StrCpy $R5 "1"
  StrCpy $R6 "0"
  StrLen $R7 $ServerPath
  StrCpy $R4 0
server_ipv4_scan:
  IntCmp $R4 $R7 server_ipv4_scan_done server_ipv4_scan_char server_ipv4_scan_done
server_ipv4_scan_char:
  StrCpy $R3 $ServerPath 1 $R4
  StrCmp $R3 "." server_ipv4_dot
  ${StrStr} $R2 "0123456789" "$R3"
  StrCmp $R2 "" 0 server_ipv4_next
  StrCpy $R5 "0"
  Goto server_ipv4_next
server_ipv4_dot:
  IntOp $R6 $R6 + 1
server_ipv4_next:
  IntOp $R4 $R4 + 1
  Goto server_ipv4_scan
server_ipv4_scan_done:
  ${If} $R5 == "1"
  ${AndIf} $R6 == 3
    StrCpy $ServerScheme "http"
  ${Else}
    StrCpy $ServerScheme "https"
    StrCpy $ServerProbeUrl "https://$ServerAuthority"
    Call ProbeServerHttps
    ${If} $R8 != 1
      StrCpy $ServerScheme "http"
    ${EndIf}
  ${EndIf}
server_protocol_selected:
  StrCpy $Server "$ServerScheme://$ServerAuthority"
  StrCpy $R9 0
FunctionEnd

Function ProbeServerHttps
  StrCpy $R8 "0"
  System::Call 'wininet::InternetOpenW(w "${PRODUCT_NAME_EN} installer", i 0, p 0, p 0, i 0) p.R0'
  ${If} $R0 == 0
    Return
  ${EndIf}
  System::Alloc 4
  Pop $R2
  ${If} $R2 != 0
    System::Call '*$R2(i 5000)'
    System::Call 'wininet::InternetSetOptionW(p $R0, i 2, p $R2, i 4) i.R3'
    System::Call 'wininet::InternetSetOptionW(p $R0, i 5, p $R2, i 4) i.R3'
    System::Call 'wininet::InternetSetOptionW(p $R0, i 6, p $R2, i 4) i.R3'
    System::Free $R2
  ${EndIf}
  System::Call 'wininet::InternetOpenUrlW(p $R0, w "$ServerProbeUrl", p 0, i 0, i 0x84000200, p 0) p.R1'
  ${If} $R1 != 0
    StrCpy $R8 "1"
    System::Call 'wininet::InternetCloseHandle(p $R1)'
  ${EndIf}
  System::Call 'wininet::InternetCloseHandle(p $R0)'
FunctionEnd

Function SyncRootPage
  !insertmacro SKIP_CONFIGURATION_PAGE
  !insertmacro MUI_HEADER_TEXT "选择同步目录与快捷方式" "选择同步根目录，需选择空目录。"
  nsDialogs::Create 1018
  Pop $PageDialog
  ${If} $PageDialog == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 12u "本地同步根目录"
  Pop $0
  ${NSD_CreateText} 0 14u 78% 13u "$SyncRoot"
  Pop $SyncRootEdit
  ${NSD_CreateButton} 81% 13u 19% 15u "浏览"
  Pop $SyncRootBrowse
  ${NSD_OnClick} $SyncRootBrowse BrowseSyncRoot
  ${NSD_CreateCheckbox} 0 48u 100% 14u "创建桌面快捷方式（可打开同步目录）"
  Pop $DesktopShortcutCheckbox
  ${If} $CreateDesktopShortcut == "1"
    ${NSD_SetState} $DesktopShortcutCheckbox ${BST_CHECKED}
  ${EndIf}
  nsDialogs::Show
FunctionEnd

Function BrowseSyncRoot
  nsDialogs::SelectFolderDialog "选择同步根目录" "$SyncRoot"
  Pop $0
  ${If} $0 != ""
    ${NSD_SetText} $SyncRootEdit $0
  ${EndIf}
FunctionEnd

Function SyncRootPageLeave
  ${NSD_GetText} $SyncRootEdit $SyncRoot
  ${NSD_GetState} $DesktopShortcutCheckbox $0
  StrCpy $CreateDesktopShortcut "0"
  ${If} $0 == ${BST_CHECKED}
    StrCpy $CreateDesktopShortcut "1"
  ${EndIf}
  ${If} $SyncRoot == ""
    MessageBox MB_OK|MB_ICONEXCLAMATION "请选择本地同步目录。"
    Abort
  ${EndIf}
  Call ValidateSyncRoot
  ${If} $R9 != 0
    Abort
  ${EndIf}
FunctionEnd

Function ValidateSyncRoot
  StrCpy $R9 1
  ${If} $SyncRoot == ""
    MessageBox MB_OK|MB_ICONEXCLAMATION "请选择本地同步目录。"
    Return
  ${EndIf}
  System::Call 'kernel32::GetFullPathNameW(w "$SyncRoot", i 1024, w .R0, p 0)i.R1'
  ${If} $R1 == 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "无法解析同步目录路径：$SyncRoot"
    Return
  ${EndIf}
  ${If} $R1 >= 1024
    MessageBox MB_OK|MB_ICONEXCLAMATION "同步目录路径过长，请选择较短的路径。"
    Return
  ${EndIf}
  StrCpy $R1 $R0 2
  ${If} $R1 == "\\"
    MessageBox MB_OK|MB_ICONEXCLAMATION "同步目录必须位于本地磁盘。"
    Return
  ${EndIf}
  StrCpy $R3 $R0 3
  ${If} $R3 == $R0
    MessageBox MB_OK|MB_ICONEXCLAMATION "不能使用磁盘根目录。"
    Return
  ${EndIf}
  StrCpy $SensitivePath $R0
  StrCpy $SensitivePathKind "sync"
  Call FindSensitiveRoot
  ${If} $R9 == 1
    MessageBox MB_OK|MB_ICONSTOP "位置位于系统敏感目录：$\r$\n$SensitivePath$\r$\n（位于 $SensitiveRoot 下）"
    Return
  ${EndIf}
  ClearErrors
  CreateDirectory "$R0"
  IfErrors sync_root_directory_create_failed
  System::Call 'kernel32::GetVolumePathNameW(w "$R0", w .R4, i 260)i.R5'
  StrCmp $R5 0 sync_root_volume_failed
  System::Call 'kernel32::GetDriveTypeW(w "$R4")i.R5'
  StrCmp $R5 3 sync_root_drive_ok
  Goto sync_root_volume_failed
sync_root_drive_ok:
  System::Call 'kernel32::GetVolumeInformationW(w "$R4", p 0, i 0, p 0, p 0, p 0, w .R6, i 32)i.R5'
  StrCmp $R5 0 sync_root_volume_failed
  StrCmp $R6 "NTFS" sync_root_volume_ok
  Goto sync_root_volume_failed
sync_root_volume_ok:
  GetFullPathName $R1 "$InstallDirectory"
  StrLen $R2 $R0
  StrCpy $R3 $R1 $R2
  ${If} $R3 == $R0
    StrCpy $R3 $R1 1 $R2
    ${If} $R3 == "\"
      MessageBox MB_OK|MB_ICONEXCLAMATION "程序安装目录不能位于同步目录中。"
      Return
    ${EndIf}
    ${If} $R1 == $R0
      MessageBox MB_OK|MB_ICONEXCLAMATION "程序安装目录不能与同步目录相同。"
      Return
    ${EndIf}
  ${EndIf}
  ; A previous installer version could leave its empty probe file if FindNext's
  ; end-of-enumeration error flag made the later FileOpen check fail spuriously.
  StrCpy $R4 "$R0\.yw-setup-probe"
  IfFileExists "$R4" sync_root_legacy_probe_present sync_root_legacy_probe_done
sync_root_legacy_probe_present:
  System::Call 'kernel32::GetFileAttributesW(w "$R4") i.R6'
  IntCmp $R6 -1 sync_root_legacy_probe_done sync_root_legacy_probe_attributes sync_root_legacy_probe_attributes
sync_root_legacy_probe_attributes:
  IntOp $R7 $R6 & 0x410
  IntCmp $R7 0 sync_root_legacy_probe_open sync_root_legacy_probe_done sync_root_legacy_probe_done
sync_root_legacy_probe_open:
  ClearErrors
  FileOpen $R5 "$R4" r
  IfErrors sync_root_legacy_probe_done
  ClearErrors
  FileSeek $R5 0 END $R8
  IfErrors sync_root_legacy_probe_seek_failed
  FileClose $R5
  StrCmp $R8 "0" sync_root_legacy_probe_delete sync_root_legacy_probe_done
sync_root_legacy_probe_seek_failed:
  FileClose $R5
  Goto sync_root_legacy_probe_done
sync_root_legacy_probe_delete:
  ClearErrors
  Delete "$R4"
  IfErrors sync_root_probe_delete_failed
  IfFileExists "$R4" sync_root_probe_delete_failed
sync_root_legacy_probe_done:
  FindFirst $R5 $R6 "$R0\*.*"
sync_root_empty_loop:
  StrCmp $R6 "" sync_root_empty_done
  StrCmp $R6 "." sync_root_empty_next
  StrCmp $R6 ".." sync_root_empty_next
  FindClose $R5
  MessageBox MB_OK|MB_ICONEXCLAMATION "请选择空目录。"
  Return
sync_root_empty_next:
  FindNext $R5 $R6
  Goto sync_root_empty_loop
sync_root_empty_done:
  FindClose $R5
  StrCpy $R4 "$R0\.yw-setup-probe"
  ClearErrors
  FileOpen $R5 "$R4" w
  IfErrors sync_root_probe_failed
  FileClose $R5
  ClearErrors
  Delete "$R4"
  IfErrors sync_root_probe_delete_failed
  IfFileExists "$R4" sync_root_probe_delete_failed
  StrCpy $SyncRoot $R0
  StrCpy $R9 0
  Return
sync_root_probe_failed:
  MessageBox MB_OK|MB_ICONEXCLAMATION "无法在所选同步目录中创建临时测试文件：$R4。请检查目录权限和文件占用后重试。"
  Return
sync_root_probe_delete_failed:
  MessageBox MB_OK|MB_ICONEXCLAMATION "无法删除同步目录中的临时测试文件：$R4。请检查文件占用和目录权限后重试。"
  Return
sync_root_directory_create_failed:
  MessageBox MB_OK|MB_ICONEXCLAMATION "无法创建所选同步目录：$R0。请检查父目录权限后重试。"
  Return
sync_root_volume_failed:
  MessageBox MB_OK|MB_ICONEXCLAMATION "同步目录必须位于本地 NTFS 磁盘。"
FunctionEnd

Function SummaryPage
  !insertmacro SKIP_CONFIGURATION_PAGE
  !insertmacro MUI_HEADER_TEXT "确认安装选项" "请确认安装设置。"
  nsDialogs::Create 1018
  Pop $PageDialog
  ${If} $PageDialog == error
    Abort
  ${EndIf}
  StrCpy $0 "程序目录：$InstallDirectory$\r$\n服务器：$Server$\r$\n服务器账号：$Username$\r$\n同步根目录：$SyncRoot$\r$\n桌面快捷方式："
  ${If} $CreateDesktopShortcut == "1"
    StrCpy $0 "$0创建"
  ${Else}
    StrCpy $0 "$0不创建"
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 100u "$0"
  Pop $SummaryText
  nsDialogs::Show
FunctionEnd

Function FinishPageShow
  ${NSD_OnClick} $mui.FinishPage.Run FinishPageLaunchClicked
  SendMessage $mui.FinishPage.Title ${WM_SETFONT} $InstallerPageTitleFont 1
  EnableWindow $mui.FinishPage.ShowReadme 1
FunctionEnd

Function LaunchAppOnFinish
  SendMessage $mui.FinishPage.ShowReadme ${BM_GETCHECK} 0 0 $R0
  ${If} $R0 == ${BST_CHECKED}
    ExecShell "open" "$INSTDIR\${PRODUCT_IDENTIFIER}.exe" "-open-folder"
  ${Else}
    ExecShell "open" "$INSTDIR\${PRODUCT_IDENTIFIER}.exe"
  ${EndIf}
FunctionEnd

Function FinishPageLaunchClicked
  SendMessage $mui.FinishPage.Run ${BM_GETCHECK} 0 0 $R0
  ${If} $R0 != ${BST_CHECKED}
    SendMessage $mui.FinishPage.ShowReadme ${BM_SETCHECK} ${BST_UNCHECKED} 0
    EnableWindow $mui.FinishPage.ShowReadme 0
  ${ElseIf} $SyncRoot != ""
    EnableWindow $mui.FinishPage.ShowReadme 1
  ${EndIf}
FunctionEnd

Function OpenSyncFolder
  ; LaunchAppOnFinish passes -open-folder so the app opens its configured path.
FunctionEnd

Function EscapeJson
  StrCpy $R0 $JsonValue
  StrCpy $JsonValue ""
  StrLen $R1 $R0
  StrCpy $R2 0
escape_json_loop:
  IntCmp $R2 $R1 escape_json_done escape_json_char escape_json_done
escape_json_char:
  StrCpy $R3 $R0 1 $R2
  StrCmp $R3 "\" escape_json_backslash
  StrCmp $R3 '$\"' escape_json_quote
  StrCpy $JsonValue "$JsonValue$R3"
  IntOp $R2 $R2 + 1
  Goto escape_json_loop
escape_json_backslash:
  StrCpy $JsonValue "$JsonValue\\"
  IntOp $R2 $R2 + 1
  Goto escape_json_loop
escape_json_quote:
  StrCpy $JsonValue "$JsonValue\$\""
  IntOp $R2 $R2 + 1
  Goto escape_json_loop
escape_json_done:
FunctionEnd

Function ProtectPassword
  StrCpy $R9 1
  System::Call 'kernel32::WideCharToMultiByte(i 65001, i 0, w "$Password", i -1, p 0, i 0, p 0, p 0) i.R0'
  IntCmp $R0 2 protect_password_allocate protect_password_failed protect_password_allocate
protect_password_allocate:
  System::Alloc $R0
  Pop $R1
  System::Call 'kernel32::WideCharToMultiByte(i 65001, i 0, w "$Password", i -1, p $R1, i $R0, p 0, p 0) i.R2'
  IntCmp $R2 2 protect_password_make_blob protect_password_bad_input protect_password_make_blob
protect_password_make_blob:
  IntOp $R2 $R2 - 1
  System::Call '*(i R2, p R1)p.R3'
  System::Call '*(i 0, p 0)p.R4'
  System::Call 'crypt32::CryptProtectData(p $R3, p 0, p 0, p 0, p 0, i 1, p $R4)i.R5'
  StrCmp $R5 0 protect_password_bad_crypt
  System::Call '*$R4(i.R5, p.R6)'
  System::Call '*(i 0)p.R7'
  System::Call 'crypt32::CryptBinaryToStringW(p $R6, i $R5, i 0x40000001, p 0, p $R7)i.R8'
  StrCmp $R8 0 protect_password_bad_blob
  System::Call '*$R7(i.R8)'
  System::StrAlloc 1024
  Pop $R9
  System::Call 'crypt32::CryptBinaryToStringW(p $R6, i $R5, i 0x40000001, p $R9, p $R7)i.R8'
  StrCmp $R8 0 protect_password_bad_string
  System::Call '*$R9(&t1024 .R0)'
  StrCpy $ProtectedPassword $R0
  System::Free $R9
  System::Call 'kernel32::LocalFree(p $R6) p.R0'
  System::Call 'kernel32::RtlSecureZeroMemory(p $R1, i $R2) p.R0'
  System::Free $R1
  System::Free $R3
  System::Free $R4
  System::Free $R7
  StrCpy $R9 0
  Return
protect_password_bad_string:
  System::Free $R9
protect_password_bad_blob:
  System::Call 'kernel32::LocalFree(p $R6) p.R0'
  System::Free $R3
  System::Free $R4
  System::Free $R7
  Goto protect_password_bad_input
protect_password_bad_crypt:
  System::Free $R3
  System::Free $R4
protect_password_bad_input:
  System::Call 'kernel32::RtlSecureZeroMemory(p $R1, i $R0) p.R0'
  System::Free $R1
protect_password_failed:
FunctionEnd

Function SaveConfiguration
  StrCpy $SessionUsername $Username
  Call ProtectPassword
  StrCmp $R9 0 save_config_password_ok
  MessageBox MB_OK|MB_ICONSTOP "无法加密密码。"
  StrCpy $R9 1
  Return
save_config_password_ok:
  StrCpy $Password ""
  StrCpy $JsonValue $Server
  Call EscapeJson
  StrCpy $Server $JsonValue
  StrCpy $JsonValue $Username
  Call EscapeJson
  StrCpy $Username $JsonValue
  StrCpy $JsonValue $SyncRoot
  Call EscapeJson
  StrCpy $SyncRoot $JsonValue

  StrCpy $ConfigDirectory "$LOCALAPPDATA\${PRODUCT_IDENTIFIER}"
  CreateDirectory "$ConfigDirectory"
  IfErrors save_config_failed
  StrCpy $ConfigPath "$ConfigDirectory\appsettings.json"
  FileOpen $R0 "$ConfigPath" w
  IfErrors save_config_failed
  FileWriteUTF16LE /BOM $R0 "{$\"SyncRootPath$\":$\"$SyncRoot$\",$\"Server$\":$\"$Server$\",$\"Username$\":$\"$Username$\",$\"UserKey$\":$\"$Username$\",$\"ProtectedPassword$\":$\"$ProtectedPassword$\"}"
  FileClose $R0
  StrCpy $ProtectedPassword ""

  ClearErrors
  FileOpen $R0 "$PLUGINSDIR\setup-login-input.txt" w
  IfErrors save_config_session_input_open_failed
  FileWriteUTF16LE /BOM $R0 "$SessionUsername$\r$\n$ConfigDirectory"
  FileClose $R0
  nsExec::ExecToStack /TIMEOUT=15000 '$\"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe$\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $\"$PLUGINSDIR\setup-login.ps1$\" -Operation SaveSession -InputPath $\"$PLUGINSDIR\setup-login-input.txt$\" -SessionPath $\"$PLUGINSDIR\setup-login-session.txt$\" -ImagePath $\"$PLUGINSDIR\setup-captcha.bmp$\"'
  Pop $R9
  Pop $R8
  Delete "$PLUGINSDIR\setup-login-input.txt"
  ${If} $R9 != "0"
    Goto save_config_session_failed
  ${EndIf}
  Delete "$PLUGINSDIR\setup-login-session.txt"
  StrCpy $SessionUsername ""
  StrCpy $R9 0
  Return
save_config_session_input_open_failed:
  StrCpy $R8 "无法创建安装器临时输入文件。"
  StrCpy $R9 "41"
save_config_session_failed:
  Delete "$PLUGINSDIR\setup-login-input.txt"
  StrCpy $SessionUsername ""
  DetailPrint "保存登录会话凭据失败，返回值：$R9"
  ${If} $R8 != ""
    DetailPrint "SaveSession 输出：$R8"
    MessageBox MB_OK|MB_ICONSTOP "无法保存登录会话凭据（错误代码：$R9）。$\r$\n系统信息：$R8"
  ${Else}
    MessageBox MB_OK|MB_ICONSTOP "无法保存登录会话凭据（错误代码：$R9）。请重新运行安装程序。"
  ${EndIf}
  StrCpy $R9 1
  Return
save_config_failed:
  MessageBox MB_OK|MB_ICONSTOP "无法写入${PRODUCT_NAME_ZH}账号配置。"
  StrCpy $R9 1
FunctionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  ReadRegStr $R0 HKLM "${UNINSTALL_KEY}" "InstallLocation"
  StrCmp $R0 "" un_init_try_exedir
  GetFullPathName $R1 "$R0"
  IfFileExists "$R1\installed-files.txt" 0 un_init_try_exedir
  IfFileExists "$R1\installed-directories.txt" 0 un_init_try_exedir
  StrCpy $INSTDIR $R1
  Goto un_init_location_ready
un_init_try_exedir:
  ; NSIS can relaunch the uninstaller from %TEMP%\~nsu*.tmp. Only use $EXEDIR
  ; as a fallback when it actually contains this install's manifests.
  GetFullPathName $R1 "$EXEDIR"
  IfFileExists "$R1\installed-files.txt" 0 un_init_location_missing
  IfFileExists "$R1\installed-directories.txt" 0 un_init_location_missing
  StrCpy $INSTDIR $R1
  Goto un_init_location_ready
un_init_location_missing:
  MessageBox MB_OK|MB_ICONSTOP "无法确定${PRODUCT_NAME_ZH}的安装目录。为保护其他文件，卸载已停止。"
  SetErrorLevel 1
  Abort
un_init_location_ready:
  StrCpy $PreviousShellExtensionFileName "${PRODUCT_IDENTIFIER}ShellExt.dll"
  ReadRegStr $R1 HKLM "${UNINSTALL_KEY}" "ShellExtensionDll"
  ${If} $R1 != ""
    Call un.ValidateShellExtensionFileName
    ${If} $R0 == "1"
      StrCpy $PreviousShellExtensionFileName $R1
    ${EndIf}
  ${EndIf}
  StrCpy $KeepDownloadedFiles "1"
  StrCpy $KeepUserConfiguration "0"
FunctionEnd

Function un.ValidateShellExtensionFileName
  StrCpy $R0 "0"
  StrLen $R2 "${PRODUCT_IDENTIFIER}ShellExt"
  StrCpy $R3 $R1 $R2
  StrCmp $R3 "${PRODUCT_IDENTIFIER}ShellExt" 0 un_validate_shell_name_done
  StrLen $R2 $R1
  IntOp $R2 $R2 - 4
  IntCmp $R2 0 un_validate_shell_name_done un_validate_shell_name_done
  StrCpy $R3 $R1 4 $R2
  StrCmp $R3 ".dll" 0 un_validate_shell_name_done
  StrCpy $R2 "0"
un_validate_shell_name_chars:
  StrCpy $R3 $R1 1 $R2
  StrCmp $R3 "" un_validate_shell_name_valid
  ${UnStrStr} $R4 "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-." "$R3"
  StrCmp $R4 "" un_validate_shell_name_done
  IntOp $R2 $R2 + 1
  Goto un_validate_shell_name_chars
un_validate_shell_name_valid:
  StrCpy $R0 "1"
un_validate_shell_name_done:
FunctionEnd

Function un.UninstallOptionsPage
  !insertmacro MUI_HEADER_TEXT "确认卸载选项" "请选择文件保留选项。"
  nsDialogs::Create 1018
  Pop $PageDialog
  ${If} $PageDialog == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 32u "卸载会移除程序、桌面快捷方式、侧边栏入口和 SyncRoot 注册。保留的已下载内容会转换为普通文件；清理后为空的同步目录会删除。"
  Pop $0
  ${NSD_CreateCheckbox} 0 46u 100% 28u "保留已下载文件和离线内容"
  Pop $KeepDownloadedCheckbox
  ${NSD_SetState} $KeepDownloadedCheckbox ${BST_CHECKED}
  ${NSD_CreateCheckbox} 0 80u 100% 32u "保留用户配置和日志"
  Pop $KeepConfigurationCheckbox
  nsDialogs::Show
FunctionEnd

Function un.UninstallOptionsLeave
  ${NSD_GetState} $KeepDownloadedCheckbox $0
  StrCpy $KeepDownloadedFiles "0"
  ${If} $0 == ${BST_CHECKED}
    StrCpy $KeepDownloadedFiles "1"
  ${EndIf}
  ${NSD_GetState} $KeepConfigurationCheckbox $0
  StrCpy $KeepUserConfiguration "0"
  ${If} $0 == ${BST_CHECKED}
    StrCpy $KeepUserConfiguration "1"
  ${EndIf}
FunctionEnd

Function ManifestContainsPath
  Push $R0
  Push $R1
  StrCpy $ManifestPathFound "0"
  ClearErrors
  FileOpen $R0 "$ManifestSearchFile" r
  IfErrors manifest_contains_open_failed
manifest_contains_loop:
  ClearErrors
  FileRead $R0 $R1
  IfErrors manifest_contains_close
  ${StrRep} $R1 $R1 "$\r" ""
  ${StrRep} $R1 $R1 "$\n" ""
  StrCmp $R1 "" manifest_contains_loop
  StrCmp $R1 $ManifestSearchPath manifest_contains_found
  Goto manifest_contains_loop
manifest_contains_found:
  StrCpy $ManifestPathFound "1"
manifest_contains_close:
  FileClose $R0
  Goto manifest_contains_done
manifest_contains_open_failed:
  StrCpy $ManifestPathFound "-1"
manifest_contains_done:
  Pop $R1
  Pop $R0
FunctionEnd

Function ValidatePreviousInstallRelativePath
  StrCpy $RelativePathValid "0"
  StrCmp $R1 "" upgrade_validate_path_invalid
  StrLen $R3 $R1
  StrCpy $R2 "0"
  StrCpy $R7 "0"
upgrade_validate_path_chars:
  IntCmp $R2 $R3 upgrade_validate_path_root upgrade_validate_path_char upgrade_validate_path_char
upgrade_validate_path_char:
  StrCpy $R4 $R1 1 $R2
  StrCmp $R4 "\" upgrade_validate_path_component_end
  StrCmp $R4 ":" upgrade_validate_path_invalid
  StrCmp $R4 "*" upgrade_validate_path_invalid
  StrCmp $R4 "?" upgrade_validate_path_invalid
  StrCmp $R4 "|" upgrade_validate_path_invalid
  StrCmp $R4 "/" upgrade_validate_path_invalid
  StrCmp $R4 "<" upgrade_validate_path_invalid
  StrCmp $R4 ">" upgrade_validate_path_invalid
  StrCmp $R4 "$\"" upgrade_validate_path_invalid
  IntOp $R2 $R2 + 1
  Goto upgrade_validate_path_chars
upgrade_validate_path_component_end:
  StrCpy $R9 $R2
  IntOp $R9 $R9 - $R7
  StrCpy $R8 $R1 $R9 $R7
  StrCmp $R8 "" upgrade_validate_path_invalid
  StrCmp $R8 "." upgrade_validate_path_invalid
  StrCmp $R8 ".." upgrade_validate_path_invalid
  IntOp $R2 $R2 + 1
  StrCpy $R7 $R2
  Goto upgrade_validate_path_chars
upgrade_validate_path_root:
  StrCpy $R9 $R3
  IntOp $R9 $R9 - $R7
  StrCpy $R8 $R1 $R9 $R7
  StrCmp $R8 "" upgrade_validate_path_invalid
  StrCmp $R8 "." upgrade_validate_path_invalid
  StrCmp $R8 ".." upgrade_validate_path_invalid

  GetFullPathName $R6 "$PreviousInstallDirectory"
  StrCmp $R6 "" upgrade_validate_path_invalid
  StrCpy $RelativePathFull "$R6\$R1"

  ; Reject reparse points from the volume root through the previous install directory.
  StrCpy $R8 $R6 3
  System::Call 'kernel32::GetFileAttributesW(w "$R8") i.R9'
  IntCmp $R9 -1 upgrade_validate_path_invalid upgrade_validate_volume_attributes upgrade_validate_volume_attributes
upgrade_validate_volume_attributes:
  IntOp $R4 $R9 & 0x10
  IntCmp $R4 0 upgrade_validate_path_invalid upgrade_validate_volume_reparse upgrade_validate_volume_reparse
upgrade_validate_volume_reparse:
  IntOp $R4 $R9 & 0x400
  IntCmp $R4 0 upgrade_validate_ancestor_scan_init upgrade_validate_path_invalid upgrade_validate_path_invalid
upgrade_validate_ancestor_scan_init:
  StrCpy $R2 3
  StrLen $R3 $R6
upgrade_validate_ancestor_scan:
  IntCmp $R2 $R3 upgrade_validate_root_attributes upgrade_validate_ancestor_char upgrade_validate_ancestor_char
upgrade_validate_ancestor_char:
  StrCpy $R4 $R6 1 $R2
  StrCmp $R4 "\" upgrade_validate_ancestor_check
  IntOp $R2 $R2 + 1
  Goto upgrade_validate_ancestor_scan
upgrade_validate_ancestor_check:
  StrCpy $R8 $R6 $R2
  System::Call 'kernel32::GetFileAttributesW(w "$R8") i.R9'
  IntCmp $R9 -1 upgrade_validate_path_invalid upgrade_validate_ancestor_attributes upgrade_validate_ancestor_attributes
upgrade_validate_ancestor_attributes:
  IntOp $R4 $R9 & 0x10
  IntCmp $R4 0 upgrade_validate_path_invalid upgrade_validate_ancestor_reparse upgrade_validate_ancestor_reparse
upgrade_validate_ancestor_reparse:
  IntOp $R4 $R9 & 0x400
  IntCmp $R4 0 upgrade_validate_ancestor_next upgrade_validate_path_invalid upgrade_validate_path_invalid
upgrade_validate_ancestor_next:
  IntOp $R2 $R2 + 1
  Goto upgrade_validate_ancestor_scan
upgrade_validate_root_attributes:
  System::Call 'kernel32::GetFileAttributesW(w "$R6") i.R9'
  IntCmp $R9 -1 upgrade_validate_path_invalid upgrade_validate_root_attributes_read upgrade_validate_root_attributes_read
upgrade_validate_root_attributes_read:
  IntOp $R4 $R9 & 0x10
  IntCmp $R4 0 upgrade_validate_path_invalid upgrade_validate_root_reparse upgrade_validate_root_reparse
upgrade_validate_root_reparse:
  IntOp $R4 $R9 & 0x400
  IntCmp $R4 0 upgrade_validate_root_prefix upgrade_validate_path_invalid upgrade_validate_path_invalid
upgrade_validate_root_prefix:
  StrLen $R7 $R6
  StrCpy $R8 $RelativePathFull $R7
  StrCmp $R8 $R6 0 upgrade_validate_path_invalid
  StrCpy $R8 $RelativePathFull 1 $R7
  StrCmp $R8 "\" 0 upgrade_validate_path_invalid

  ; Existing intermediate directories and leaves must not be junctions or symlinks.
  StrCpy $R2 "0"
  StrLen $R3 $R1
upgrade_validate_parent_scan:
  IntCmp $R2 $R3 upgrade_validate_leaf_attributes upgrade_validate_parent_scan_char upgrade_validate_parent_scan_char
upgrade_validate_parent_scan_char:
  StrCpy $R4 $R1 1 $R2
  StrCmp $R4 "\" upgrade_validate_parent_check
  IntOp $R2 $R2 + 1
  Goto upgrade_validate_parent_scan
upgrade_validate_parent_check:
  StrCpy $R8 $R1 $R2
  StrCpy $R9 "$R6\$R8"
  System::Call 'kernel32::GetFileAttributesW(w "$R9") i.R5'
  IntCmp $R5 -1 upgrade_validate_parent_skip upgrade_validate_parent_attributes_read upgrade_validate_parent_attributes_read
upgrade_validate_parent_attributes_read:
  IntOp $R4 $R5 & 0x10
  IntCmp $R4 0 upgrade_validate_path_invalid upgrade_validate_parent_reparse upgrade_validate_parent_reparse
upgrade_validate_parent_reparse:
  IntOp $R4 $R5 & 0x400
  IntCmp $R4 0 upgrade_validate_parent_skip upgrade_validate_path_invalid upgrade_validate_path_invalid
upgrade_validate_parent_skip:
  IntOp $R2 $R2 + 1
  Goto upgrade_validate_parent_scan
upgrade_validate_leaf_attributes:
  System::Call 'kernel32::GetFileAttributesW(w "$RelativePathFull") i.R9'
  IntCmp $R9 -1 upgrade_validate_path_valid upgrade_validate_leaf_attributes_read upgrade_validate_leaf_attributes_read
upgrade_validate_leaf_attributes_read:
  IntOp $R4 $R9 & 0x400
  IntCmp $R4 0 upgrade_validate_path_valid upgrade_validate_path_invalid upgrade_validate_path_invalid
upgrade_validate_path_valid:
  StrCpy $RelativePathValid "1"
  Return
upgrade_validate_path_invalid:
  StrCpy $RelativePathValid "0"
FunctionEnd

Function RemoveObsoleteInstalledFiles
  ${If} $IsInstalled != "1"
    Return
  ${EndIf}
  Push $R0
  Push $R1
  Push $R2
  Push $R3
  Push $R4
  Push $R5
  Push $R6
  Push $R7
  Push $R8
  Push $R9

  ClearErrors
  FileOpen $PreviousManifestHandle "$PreviousInstallDirectory\installed-files.txt" r
  IfErrors upgrade_obsolete_files_open_failed
upgrade_obsolete_files_loop:
  ClearErrors
  FileRead $PreviousManifestHandle $ManifestSearchPath
  IfErrors upgrade_obsolete_files_done
  ${StrRep} $ManifestSearchPath $ManifestSearchPath "$\r" ""
  ${StrRep} $ManifestSearchPath $ManifestSearchPath "$\n" ""
  StrCmp $ManifestSearchPath "" upgrade_obsolete_files_loop

  StrCpy $R1 $ManifestSearchPath
  Call ValidatePreviousInstallRelativePath
  StrCmp $RelativePathValid "1" 0 upgrade_obsolete_files_invalid_path
  StrCpy $ManifestSearchFile $NewInstalledFilesManifest
  Call ManifestContainsPath
  StrCmp $ManifestPathFound "-1" upgrade_obsolete_files_new_manifest_failed
  StrCmp $ManifestPathFound "1" upgrade_obsolete_files_loop

  System::Call 'kernel32::GetFileAttributesW(w "$RelativePathFull") i.R9'
  IntCmp $R9 -1 upgrade_obsolete_files_loop upgrade_obsolete_file_attributes upgrade_obsolete_file_attributes
upgrade_obsolete_file_attributes:
  IntOp $R8 $R9 & 0x10
  IntCmp $R8 0 upgrade_obsolete_file_delete upgrade_obsolete_files_loop upgrade_obsolete_files_loop
upgrade_obsolete_file_delete:
  SetFileAttributes "$RelativePathFull" NORMAL
  ClearErrors
  Delete /REBOOTOK "$RelativePathFull"
  IfErrors upgrade_obsolete_files_delete_failed
  Goto upgrade_obsolete_files_loop

upgrade_obsolete_files_done:
  FileClose $PreviousManifestHandle
  Call RemoveObsoleteInstalledDirectories
  Goto upgrade_obsolete_files_restore
upgrade_obsolete_files_open_failed:
  MessageBox MB_OK|MB_ICONSTOP "无法读取旧版安装文件清单；为保护其他文件，升级已停止。"
  SetErrorLevel 1
  Abort
upgrade_obsolete_files_invalid_path:
  FileClose $PreviousManifestHandle
  MessageBox MB_OK|MB_ICONSTOP "旧版安装文件清单包含越界路径或重解析路径；为保护其他文件，升级已停止。"
  SetErrorLevel 1
  Abort
upgrade_obsolete_files_new_manifest_failed:
  FileClose $PreviousManifestHandle
  MessageBox MB_OK|MB_ICONSTOP "无法读取新版安装文件清单；为保护其他文件，升级已停止。"
  SetErrorLevel 1
  Abort
upgrade_obsolete_files_delete_failed:
  FileClose $PreviousManifestHandle
  MessageBox MB_OK|MB_ICONSTOP "无法清理旧版文件：$RelativePathFull。请检查权限或文件占用后重试。"
  SetErrorLevel 1
  Abort
upgrade_obsolete_files_restore:
  Pop $R9
  Pop $R8
  Pop $R7
  Pop $R6
  Pop $R5
  Pop $R4
  Pop $R3
  Pop $R2
  Pop $R1
  Pop $R0
FunctionEnd

Function RemoveObsoleteInstalledDirectories
  Push $R0
  Push $R1
  Push $R2
  Push $R3
  Push $R4
  Push $R5
  Push $R6
  Push $R7
  Push $R8
  Push $R9
  ClearErrors
  FileOpen $PreviousManifestHandle "$PreviousInstallDirectory\installed-directories.txt" r
  IfErrors upgrade_obsolete_directories_open_failed
upgrade_obsolete_directories_loop:
  ClearErrors
  FileRead $PreviousManifestHandle $ManifestSearchPath
  IfErrors upgrade_obsolete_directories_done
  ${StrRep} $ManifestSearchPath $ManifestSearchPath "$\r" ""
  ${StrRep} $ManifestSearchPath $ManifestSearchPath "$\n" ""
  StrCmp $ManifestSearchPath "" upgrade_obsolete_directories_loop

  StrCpy $R1 $ManifestSearchPath
  Call ValidatePreviousInstallRelativePath
  StrCmp $RelativePathValid "1" 0 upgrade_obsolete_directories_invalid_path
  StrCpy $ManifestSearchFile $NewInstalledDirectoriesManifest
  Call ManifestContainsPath
  StrCmp $ManifestPathFound "-1" upgrade_obsolete_directories_new_manifest_failed
  StrCmp $ManifestPathFound "1" upgrade_obsolete_directories_loop

  System::Call 'kernel32::GetFileAttributesW(w "$RelativePathFull") i.R9'
  IntCmp $R9 -1 upgrade_obsolete_directories_loop upgrade_obsolete_directory_attributes upgrade_obsolete_directory_attributes
upgrade_obsolete_directory_attributes:
  IntOp $R8 $R9 & 0x10
  IntCmp $R8 0 upgrade_obsolete_directories_loop upgrade_obsolete_directory_remove upgrade_obsolete_directory_remove
upgrade_obsolete_directory_remove:
  ClearErrors
  RMDir /REBOOTOK "$RelativePathFull"
  ClearErrors
  Goto upgrade_obsolete_directories_loop

upgrade_obsolete_directories_done:
  FileClose $PreviousManifestHandle
  Goto upgrade_obsolete_directories_restore
upgrade_obsolete_directories_open_failed:
  MessageBox MB_OK|MB_ICONSTOP "无法读取旧版安装目录清单；为保护其他文件，升级已停止。"
  SetErrorLevel 1
  Abort
upgrade_obsolete_directories_invalid_path:
  FileClose $PreviousManifestHandle
  MessageBox MB_OK|MB_ICONSTOP "旧版安装目录清单包含越界路径或重解析路径；为保护其他文件，升级已停止。"
  SetErrorLevel 1
  Abort
upgrade_obsolete_directories_new_manifest_failed:
  FileClose $PreviousManifestHandle
  MessageBox MB_OK|MB_ICONSTOP "无法读取新版安装目录清单；为保护其他文件，升级已停止。"
  SetErrorLevel 1
  Abort
upgrade_obsolete_directories_restore:
  Pop $R9
  Pop $R8
  Pop $R7
  Pop $R6
  Pop $R5
  Pop $R4
  Pop $R3
  Pop $R2
  Pop $R1
  Pop $R0
FunctionEnd

Function RemoveLegacyUninstallerArtifacts
  ; Remove only the exact product-owned paths from the previous installer generation.
  IfFileExists "$R5\uninstall-core.exe" 0 remove_legacy_file_list
  SetFileAttributes "$R5\uninstall-core.exe" NORMAL
  Delete /REBOOTOK "$R5\uninstall-core.exe"
remove_legacy_file_list:
  IfFileExists "$R5\installed-files.json" 0 remove_legacy_artifacts_done
  SetFileAttributes "$R5\installed-files.json" NORMAL
  Delete /REBOOTOK "$R5\installed-files.json"
remove_legacy_artifacts_done:
FunctionEnd

Function AddExistingShortcutIconsToManifest
  Push $0
  Push $1
  Push $2
  StrCpy $R9 "0"

  ClearErrors
  FindFirst $0 $1 "$INSTDIR\Assets\${PRODUCT_IDENTIFIER}-shortcut-*.ico"
  IfErrors record_shortcut_icons_done
  ClearErrors
  FileOpen $2 "$PLUGINSDIR\installed-files.new.txt" a
  IfErrors record_shortcut_icons_open_failed
  ${Do}
    StrCmp $1 "${PRODUCT_IDENTIFIER}-shortcut-${PRODUCT_VERSION}.ico" record_shortcut_icon_next
    ClearErrors
    FileWrite $2 "Assets\$1$\r$\n"
    IfErrors record_shortcut_icons_write_failed
record_shortcut_icon_next:
    ClearErrors
    FindNext $0 $1
  ${LoopUntil} ${Errors}
  FileClose $2
  FindClose $0
  Goto record_shortcut_icons_done

record_shortcut_icons_write_failed:
  FileClose $2
record_shortcut_icons_open_failed:
  FindClose $0
  StrCpy $R9 "1"
record_shortcut_icons_done:
  Pop $2
  Pop $1
  Pop $0
FunctionEnd

Function RefreshDesktopShortcutIcons
  Push $0
  Push $1
  Push $2
  Push $3
  Push $4
  Push $5
  Push $6
  Push $7
  Push $8
  Push $9

  StrCpy $2 0
  StrCpy $3 0
  !insertmacro ComHlpr_CreateInProcInstance ${CLSID_ShellLink} ${IID_IShellLink} r2 .r4
  ${If} $4 == 0
  ${AndIf} $2 P<> 0
    ${IUnknown::QueryInterface} $2 '("${IID_IPersistFile}",.r3).r4'
    ${If} $4 == 0
    ${AndIf} $3 P<> 0
      GetFullPathName $7 "$INSTDIR\${PRODUCT_IDENTIFIER}.exe"
      GetFullPathName $9 "$INSTDIR\Assets\${PRODUCT_IDENTIFIER}-shortcut-${PRODUCT_VERSION}.ico"
      IfFileExists "$9" 0 refresh_shortcuts_done
      System::Call 'shell32::SHChangeNotify(i 0x00002000, i 0x0005, w "$7", p 0)'
      System::Call 'shell32::SHChangeNotify(i 0x00002000, i 0x0005, w "$9", p 0)'
      ClearErrors
      FindFirst $0 $1 "$DESKTOP\*.lnk"
      IfErrors refresh_shortcuts_done
      ${Do}
        ${IPersistFile::Load} $3 '("$DESKTOP\$1",${STGM_READ}).r4'
        ${If} $4 == 0
          StrCpy $5 ""
          ${IShellLink::GetPath} $2 '(.r5,${NSIS_MAX_STRLEN},0,0).r4'
          ${If} $4 == 0
            ExpandEnvStrings $5 $5
            ClearErrors
            GetFullPathName $5 "$5"
            ${IfNot} ${Errors}
            ${AndIf} $5 == $7
              StrCpy $6 ""
              StrCpy $8 0
              ${IShellLink::GetIconLocation} $2 '(.r6,${NSIS_MAX_STRLEN},.r8).r4'
              ${If} $4 == 0
                StrCmp $6 "" refresh_shortcut_use_versioned_icon
                StrCpy $5 $6
                ExpandEnvStrings $5 $5
                ClearErrors
                GetFullPathName $5 "$5"
                ${IfNot} ${Errors}
                ${AndIf} $5 == $7
                  Goto refresh_shortcut_use_versioned_icon
                ${EndIf}
                ${IfNot} ${Errors}
                ${AndIf} $5 == $9
                  Goto refresh_shortcut_use_versioned_icon
                ${EndIf}
                StrCpy $4 "$INSTDIR\Assets\${PRODUCT_IDENTIFIER}-shortcut-"
                StrLen $4 $4
                StrCpy $5 $5 $4
                StrCmp $5 "$INSTDIR\Assets\${PRODUCT_IDENTIFIER}-shortcut-" refresh_shortcut_use_versioned_icon
                StrCpy $5 $6
                ExpandEnvStrings $5 $5
                ClearErrors
                GetFullPathName $5 "$5"
                IfFileExists "$5" refresh_shortcut_set_icon refresh_shortcut_use_versioned_icon
              ${Else}
                Goto refresh_shortcut_use_versioned_icon
              ${EndIf}

refresh_shortcut_use_versioned_icon:
              StrCpy $6 "$9"
              StrCpy $8 0
refresh_shortcut_set_icon:
              ${IShellLink::SetIconLocation} $2 '("$6",$8).r4'
              ${If} $4 == 0
                ${IPersistFile::Save} $3 '("$DESKTOP\$1",1).r4'
                ${If} $4 == 0
                  System::Call 'shell32::SHChangeNotify(i 0x00002000, i 0x0005, w "$DESKTOP\$1", p 0)'
                ${Else}
                  DetailPrint "刷新桌面快捷方式失败：$DESKTOP\$1"
                ${EndIf}
              ${Else}
                DetailPrint "设置桌面快捷方式图标失败：$DESKTOP\$1"
              ${EndIf}
            ${EndIf}
          ${EndIf}
        ${EndIf}
        ClearErrors
        FindNext $0 $1
      ${LoopUntil} ${Errors}
      FindClose $0
    ${EndIf}
  ${EndIf}
refresh_shortcuts_done:
  !insertmacro ComHlpr_SafeRelease $3
  !insertmacro ComHlpr_SafeRelease $2

  Pop $9
  Pop $8
  Pop $7
  Pop $6
  Pop $5
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
FunctionEnd

Section "Install"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=installed-files.new.txt "${INSTALLED_FILES_PATH}"
  File /oname=installed-directories.new.txt "${INSTALLED_DIRECTORIES_PATH}"
  StrCpy $NewInstalledFilesManifest "$PLUGINSDIR\installed-files.new.txt"
  StrCpy $NewInstalledDirectoriesManifest "$PLUGINSDIR\installed-directories.new.txt"
  Call AddExistingShortcutIconsToManifest
  ${If} $R9 != "0"
    MessageBox MB_OK|MB_ICONSTOP "无法保留旧版快捷方式图标清单，安装已停止。"
    SetErrorLevel 1
    Abort
  ${EndIf}

  ${If} $IsInstalled == "1"
    IfFileExists "$INSTDIR\${PRODUCT_IDENTIFIER}.exe" 0 install_stop_done
    ExecWait '"$INSTDIR\${PRODUCT_IDENTIFIER}.exe" --stop' $R9
    ${If} $R9 != 0
      MessageBox MB_OK|MB_ICONSTOP "进程未能安全退出，请手动退出程序后重试。"
      SetErrorLevel 1
      Abort
    ${EndIf}
    IfFileExists "$PreviousInstallDirectory\$PreviousShellExtensionFileName" 0 install_stop_done
    nsExec::ExecToStack /TIMEOUT=30000 '"$SYSDIR\regsvr32.exe" /s /u "$PreviousInstallDirectory\$PreviousShellExtensionFileName"'
    Pop $R9
    Pop $R8
    ${If} $R9 != "0"
      MessageBox MB_OK|MB_ICONSTOP "无法注销旧版资源管理器集成，安装已停止。"
      SetErrorLevel 1
      Abort
    ${EndIf}
install_stop_done:
  ${EndIf}

  ${If} $IsInstalled == "1"
    StrCpy $R5 "$PreviousInstallDirectory"
    Call RemoveLegacyUninstallerArtifacts
  ${EndIf}

  ${If} $IsInstalled == "1"
    IfFileExists "$INSTDIR\Setup.exe" 0 install_legacy_setup_done
    SetFileAttributes "$INSTDIR\Setup.exe" NORMAL
    ClearErrors
    Delete /REBOOTOK "$INSTDIR\Setup.exe"
    IfErrors install_legacy_setup_failed
    Goto install_legacy_setup_done
install_legacy_setup_failed:
    MessageBox MB_OK|MB_ICONSTOP "无法清理旧版安装程序，安装已停止。"
    SetErrorLevel 1
    Abort
install_legacy_setup_done:
  ${EndIf}

  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r "${APP_PAYLOAD_DIRECTORY}\*"
  StrCmp $PreviousShellExtensionFileName "${SHELL_EXTENSION_FILE_NAME}" install_shell_file_same 0
  SetOutPath "$INSTDIR"
  File /oname=${SHELL_EXTENSION_FILE_NAME} "${SHELL_EXTENSION_PATH}"
  Goto install_shell_file_done
install_shell_file_same:
  IfFileExists "$INSTDIR\${SHELL_EXTENSION_FILE_NAME}" install_shell_file_done 0
  SetOutPath "$INSTDIR"
  File /oname=${SHELL_EXTENSION_FILE_NAME} "${SHELL_EXTENSION_PATH}"
install_shell_file_done:
  Call RemoveObsoleteInstalledFiles
  Delete "$INSTDIR\${PRODUCT_IDENTIFIER}.pri"
  WriteUninstaller "$INSTDIR\uninstall.exe"
  CopyFiles /SILENT "$PLUGINSDIR\installed-files.new.txt" "$INSTDIR\installed-files.txt"
  CopyFiles /SILENT "$PLUGINSDIR\installed-directories.new.txt" "$INSTDIR\installed-directories.txt"

  ${If} $IsInstalled != "1"
    Call SaveConfiguration
    ${If} $R9 != 0
      SetErrorLevel 1
      Abort
  ${EndIf}
  ${EndIf}

  ${If} $IsInstalled == "1"
    ReadRegStr $R0 HKLM "${UNINSTALL_KEY}" "UserSid"
    ${If} $R0 != ""
      StrCpy $UserSid $R0
    ${EndIf}
  ${EndIf}

  ${If} $IsInstalled != "1"
    nsExec::ExecToStack /TIMEOUT=30000 '"$SYSDIR\schtasks.exe" /Create /TN "${PRODUCT_IDENTIFIER}-$UserSid" /SC ONLOGON /TR "\"$INSTDIR\${PRODUCT_IDENTIFIER}.exe\"" /RL HIGHEST /F'
    Pop $R9
    Pop $R8
    ${If} $R9 != "0"
      DetailPrint "创建开机任务失败，schtasks.exe 返回值：$R9"
      ${If} $R8 != ""
        DetailPrint "schtasks.exe 输出：$R8"
        MessageBox MB_OK|MB_ICONSTOP "无法创建开机任务，安装已停止。$\r$\nschtasks.exe 返回值：$R9$\r$\n系统信息：$R8"
      ${Else}
        MessageBox MB_OK|MB_ICONSTOP "无法创建开机任务，安装已停止。$\r$\nschtasks.exe 返回值：$R9。请展开“显示详情”查看安装记录。"
      ${EndIf}
      SetErrorLevel 1
      Abort
    ${EndIf}
  ${EndIf}

  nsExec::ExecToStack /TIMEOUT=30000 '"$SYSDIR\regsvr32.exe" /s "$INSTDIR\${SHELL_EXTENSION_FILE_NAME}"'
  Pop $R9
  Pop $R8
  ${If} $R9 != "0"
    MessageBox MB_OK|MB_ICONSTOP "无法注册资源管理器集成，安装已停止。"
    SetErrorLevel 1
    Abort
  ${EndIf}

  WriteRegStr HKLM "${UNINSTALL_KEY}" "ShellExtensionDll" "${SHELL_EXTENSION_FILE_NAME}"
  ${If} $IsInstalled == "1"
  ${AndIf} $PreviousShellExtensionFileName != "${SHELL_EXTENSION_FILE_NAME}"
    SetFileAttributes "$PreviousInstallDirectory\$PreviousShellExtensionFileName" NORMAL
    Delete /REBOOTOK "$PreviousInstallDirectory\$PreviousShellExtensionFileName"
  ${EndIf}

  ; Preserve the user's shortcut name and contents during upgrades.
  ${If} $IsInstalled != "1"
  ${AndIf} $CreateDesktopShortcut == "1"
    CreateShortCut "$DESKTOP\${PRODUCT_NAME_ZH}.lnk" "$INSTDIR\${PRODUCT_IDENTIFIER}.exe" "-open-folder" "$INSTDIR\Assets\${PRODUCT_IDENTIFIER}-shortcut-${PRODUCT_VERSION}.ico" 0 SW_SHOWNORMAL "" "启动${PRODUCT_NAME_ZH}并打开同步目录"
  ${EndIf}
  Call RefreshDesktopShortcutIcons

  SetRegView 64
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME_ZH}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallVariant" "${INSTALL_VARIANT}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "${PRODUCT_NAME_EN}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "UserSid" "$UserSid"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${PRODUCT_IDENTIFIER}.exe"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd

Section "Uninstall"
  Call un.ValidateInstalledLists
  DetailPrint "正在停止${PRODUCT_NAME_ZH}并清理同步目录…"
  IfFileExists "$INSTDIR\${PRODUCT_IDENTIFIER}.exe" uninstall_app_present 0
  MessageBox MB_OK|MB_ICONSTOP "主程序文件缺失，无法清理同步目录并注销 SyncRoot。请重新安装后再重试卸载。"
  SetErrorLevel 1
  Abort
uninstall_app_present:
  nsExec::ExecToStack /TIMEOUT=70000 '"$INSTDIR\${PRODUCT_IDENTIFIER}.exe" --stop'
  Pop $R9
  Pop $R8
  ${If} $R9 != "0"
    MessageBox MB_OK|MB_ICONSTOP "${PRODUCT_NAME_ZH}进程未能安全退出。请手动退出托盘程序后重试卸载。"
    SetErrorLevel 1
    Abort
  ${EndIf}
  StrCpy $R0 '"$INSTDIR\${PRODUCT_IDENTIFIER}.exe" --quiet --unregister'
  ${If} $KeepDownloadedFiles == "1"
    StrCpy $R0 "$R0 --keep-downloaded-files"
  ${EndIf}
  nsExec::ExecToStack /TIMEOUT=180000 '$R0'
  Pop $R9
  Pop $R8
  ${If} $R9 != "0"
    MessageBox MB_OK|MB_ICONSTOP "SyncRoot 清理失败，卸载已停止。请查看程序日志。"
    SetErrorLevel 1
    Abort
  ${EndIf}
  IfFileExists "$INSTDIR\$PreviousShellExtensionFileName" uninstall_shell_present uninstall_shell_done
uninstall_shell_present:
  nsExec::ExecToStack /TIMEOUT=30000 '"$SYSDIR\regsvr32.exe" /s /u "$INSTDIR\$PreviousShellExtensionFileName"'
  Pop $R9
  Pop $R8
  ${If} $R9 != "0"
    MessageBox MB_OK|MB_ICONSTOP "无法注销资源管理器集成，卸载已停止。"
    SetErrorLevel 1
    Abort
  ${EndIf}
uninstall_shell_done:

  SetRegView 64
  ReadRegStr $R0 HKLM "${UNINSTALL_KEY}" "UserSid"
  ${If} $R0 != ""
    nsExec::ExecToStack /TIMEOUT=30000 '"$SYSDIR\schtasks.exe" /Query /TN "${PRODUCT_IDENTIFIER}-$R0"'
    Pop $R9
    Pop $R8
    ${If} $R9 == "0"
      nsExec::ExecToStack /TIMEOUT=30000 '"$SYSDIR\schtasks.exe" /Delete /TN "${PRODUCT_IDENTIFIER}-$R0" /F'
      Pop $R9
      Pop $R8
      ${If} $R9 != "0"
        MessageBox MB_OK|MB_ICONSTOP "无法移除开机任务，卸载已停止。"
        SetErrorLevel 1
        Abort
      ${EndIf}
    ${EndIf}
  ${EndIf}

  Call un.RemoveDesktopShortcuts
  StrCpy $R5 "$LOCALAPPDATA\${PRODUCT_IDENTIFIER}"
  Call un.DeleteSessionFilesInDirectory
  StrCpy $R5 "$INSTDIR"
  Call un.DeleteSessionFilesInDirectory
  ${If} $KeepUserConfiguration != "1"
    StrCpy $R0 "$LOCALAPPDATA\${PRODUCT_IDENTIFIER}"
    System::Call 'kernel32::GetFileAttributesW(w "$R0") i.R1'
    IntCmp $R1 -1 uninstall_appdata_done
    IntOp $R2 $R1 & 0x400
    IntCmp $R2 0 uninstall_appdata_remove uninstall_appdata_reparse uninstall_appdata_reparse
uninstall_appdata_reparse:
    MessageBox MB_OK|MB_ICONSTOP "用户配置目录或其子项包含重解析路径。为保护目标内容，卸载已停止。"
    SetErrorLevel 1
    Abort
uninstall_appdata_remove:
    Push "$R0"
    Call un.HasReparsePointInTree
    Pop $R1
    StrCmp $R1 "1" uninstall_appdata_reparse
    ClearErrors
    RMDir /r /REBOOTOK "$R0"
    IfErrors uninstall_appdata_failed
    Delete /REBOOTOK "$INSTDIR\appsettings.json"
    Goto uninstall_appdata_done
uninstall_appdata_failed:
    MessageBox MB_OK|MB_ICONSTOP "无法删除配置目录，卸载已停止。请检查目录权限后重试。"
    SetErrorLevel 1
    Abort
uninstall_appdata_done:
  ${EndIf}

  DeleteRegKey HKLM "${UNINSTALL_KEY}"
  Call un.DeleteInstalledFiles
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd

Function un.RemoveDesktopShortcuts
  Push $0
  Push $1
  Push $2
  Push $3
  Push $4
  Push $5
  Push $6

  StrCpy $2 0
  StrCpy $3 0
  !insertmacro ComHlpr_CreateInProcInstance ${CLSID_ShellLink} ${IID_IShellLink} r2 .r4
  ${If} $4 == 0
  ${AndIf} $2 P<> 0
    ${IUnknown::QueryInterface} $2 '("${IID_IPersistFile}",.r3).r4'
    ${If} $4 == 0
    ${AndIf} $3 P<> 0
      GetFullPathName $6 "$INSTDIR\${PRODUCT_IDENTIFIER}.exe"
      ClearErrors
      FindFirst $0 $1 "$DESKTOP\*.lnk"
      IfErrors un_shortcuts_release
      ${Do}
        ; Read the stored target without resolving or rewriting the shortcut.
        ${IPersistFile::Load} $3 '("$DESKTOP\$1",${STGM_READ}).r4'
        ${If} $4 == 0
          StrCpy $5 ""
          ${IShellLink::GetPath} $2 '(.r5,${NSIS_MAX_STRLEN},0,0).r4'
          ${If} $4 == 0
          ${AndIf} $5 != ""
            ExpandEnvStrings $5 $5
            ClearErrors
            GetFullPathName $5 "$5"
            ; Compare the full executable path, never the shortcut name.
            ${IfNot} ${Errors}
            ${AndIf} $5 == $6
              Delete /REBOOTOK "$DESKTOP\$1"
            ${EndIf}
          ${EndIf}
        ${EndIf}
        ClearErrors
        FindNext $0 $1
      ${LoopUntil} ${Errors}
      FindClose $0
    ${EndIf}
  ${EndIf}
un_shortcuts_release:
  !insertmacro ComHlpr_SafeRelease $3
  !insertmacro ComHlpr_SafeRelease $2

  Pop $6
  Pop $5
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
FunctionEnd

Function un.DeleteSessionFilesInDirectory
  System::Call 'kernel32::GetFileAttributesW(w "$R5") i.R0'
  IntCmp $R0 -1 un_session_directory_done un_session_directory_exists un_session_directory_exists
un_session_directory_exists:
  IntOp $R1 $R0 & 0x10
  StrCmp $R1 "0" un_session_directory_done
  IntOp $R1 $R0 & 0x400
  StrCmp $R1 "0" un_session_directory_safe un_session_directory_reparse
un_session_directory_reparse:
  MessageBox MB_OK|MB_ICONSTOP "登录会话目录是重解析路径。为保护其目标内容，卸载已停止。"
  SetErrorLevel 1
  Abort
un_session_directory_safe:
  StrCpy $R7 ""
  ClearErrors
  FindFirst $R6 $R7 "$R5\*.*"
  IfErrors un_session_directory_read_failed
un_session_files_loop:
  StrCmp $R7 "" un_session_files_done
  StrCmp $R7 "." un_session_files_next
  StrCmp $R7 ".." un_session_files_next
  StrCmp $R7 "cookies.json" un_session_file_delete
  StrCpy $R8 $R7 8
  StrCmp $R8 "cookies-" 0 un_session_files_next
  StrLen $R9 $R7
  IntCmp $R9 13 un_session_file_name_long_enough un_session_file_name_long_enough un_session_files_next
un_session_file_name_long_enough:
  IntOp $R9 $R9 - 5
  StrCpy $R8 $R7 5 $R9
  StrCmp $R8 ".json" un_session_file_delete un_session_files_next
un_session_file_delete:
  System::Call 'kernel32::GetFileAttributesW(w "$R5\$R7") i.R0'
  IntCmp $R0 -1 un_session_files_next un_session_file_attributes_read un_session_file_attributes_read
un_session_file_attributes_read:
  IntOp $R1 $R0 & 0x10
  StrCmp $R1 "0" un_session_file_remove un_session_files_next
un_session_file_remove:
  ClearErrors
  Delete /REBOOTOK "$R5\$R7"
  IfErrors un_session_file_delete_failed
un_session_files_next:
  FindNext $R6 $R7
  Goto un_session_files_loop
un_session_file_delete_failed:
  FindClose $R6
  MessageBox MB_OK|MB_ICONSTOP "无法删除登录会话文件：$R7。请关闭程序后重试卸载。"
  SetErrorLevel 1
  Abort
un_session_directory_read_failed:
  MessageBox MB_OK|MB_ICONSTOP "无法读取登录会话目录，卸载已停止。"
  SetErrorLevel 1
  Abort
un_session_files_done:
  FindClose $R6
un_session_directory_done:
FunctionEnd

Function un.ValidateInstallRelativePath
  StrCpy $RelativePathValid "0"
  StrCmp $R1 "" un_validate_path_invalid
  StrLen $R3 $R1
  StrCpy $R2 "0"
  StrCpy $R7 "0"
un_validate_path_chars:
  IntCmp $R2 $R3 un_validate_path_root un_validate_path_char un_validate_path_char
un_validate_path_char:
  StrCpy $R4 $R1 1 $R2
  StrCmp $R4 "\" un_validate_path_component_end
  StrCmp $R4 ":" un_validate_path_invalid
  StrCmp $R4 "*" un_validate_path_invalid
  StrCmp $R4 "?" un_validate_path_invalid
  StrCmp $R4 "|" un_validate_path_invalid
  StrCmp $R4 "/" un_validate_path_invalid
  StrCmp $R4 "<" un_validate_path_invalid
  StrCmp $R4 ">" un_validate_path_invalid
  StrCmp $R4 "$\"" un_validate_path_invalid
  IntOp $R2 $R2 + 1
  Goto un_validate_path_chars
un_validate_path_component_end:
  StrCpy $R9 $R2
  IntOp $R9 $R9 - $R7
  StrCpy $R8 $R1 $R9 $R7
  StrCmp $R8 "" un_validate_path_invalid
  StrCmp $R8 "." un_validate_path_invalid
  StrCmp $R8 ".." un_validate_path_invalid
  IntOp $R2 $R2 + 1
  StrCpy $R7 $R2
  Goto un_validate_path_chars
un_validate_path_root:
  StrCpy $R9 $R3
  IntOp $R9 $R9 - $R7
  StrCpy $R8 $R1 $R9 $R7
  StrCmp $R8 "" un_validate_path_invalid
  StrCmp $R8 "." un_validate_path_invalid
  StrCmp $R8 ".." un_validate_path_invalid

  ; Build the candidate lexically. The manifest may list legacy files that no
  ; longer exist, so path validation must not depend on the leaf file existing.
  GetFullPathName $R6 "$INSTDIR"
  StrCmp $R6 "" un_validate_path_invalid
  StrCpy $RelativePathFull "$R6\$R1"

  ; Reject reparse points from the volume root through $INSTDIR.
  StrCpy $R8 $R6 3
  System::Call 'kernel32::GetFileAttributesW(w "$R8") i.R9'
  IntCmp $R9 -1 un_validate_path_invalid un_validate_volume_attributes_read un_validate_volume_attributes_read
un_validate_volume_attributes_read:
  IntOp $R4 $R9 & 0x10
  IntCmp $R4 0 un_validate_path_invalid un_validate_volume_reparse_check un_validate_volume_reparse_check
un_validate_volume_reparse_check:
  IntOp $R4 $R9 & 0x400
  IntCmp $R4 0 un_validate_install_ancestor_scan_init un_validate_path_invalid un_validate_path_invalid
un_validate_install_ancestor_scan_init:
  StrCpy $R2 3
  StrLen $R3 $R6
un_validate_install_ancestor_scan:
  IntCmp $R2 $R3 un_validate_install_root_attributes un_validate_install_ancestor_char un_validate_install_ancestor_char
un_validate_install_ancestor_char:
  StrCpy $R4 $R6 1 $R2
  StrCmp $R4 "\" un_validate_install_ancestor_check
  IntOp $R2 $R2 + 1
  Goto un_validate_install_ancestor_scan
un_validate_install_ancestor_check:
  StrCpy $R8 $R6 $R2
  System::Call 'kernel32::GetFileAttributesW(w "$R8") i.R9'
  IntCmp $R9 -1 un_validate_path_invalid un_validate_install_ancestor_attributes_read un_validate_install_ancestor_attributes_read
un_validate_install_ancestor_attributes_read:
  IntOp $R4 $R9 & 0x10
  IntCmp $R4 0 un_validate_path_invalid un_validate_install_ancestor_reparse_check un_validate_install_ancestor_reparse_check
un_validate_install_ancestor_reparse_check:
  IntOp $R4 $R9 & 0x400
  IntCmp $R4 0 un_validate_install_ancestor_next un_validate_path_invalid un_validate_path_invalid
un_validate_install_ancestor_next:
  IntOp $R2 $R2 + 1
  Goto un_validate_install_ancestor_scan
un_validate_install_root_attributes:
  System::Call 'kernel32::GetFileAttributesW(w "$R6") i.R9'
  IntCmp $R9 -1 un_validate_path_invalid un_validate_root_attributes_read un_validate_root_attributes_read
un_validate_root_attributes_read:
  IntOp $R4 $R9 & 0x10
  IntCmp $R4 0 un_validate_path_invalid un_validate_root_reparse_check un_validate_root_reparse_check
un_validate_root_reparse_check:
  IntOp $R4 $R9 & 0x400
  IntCmp $R4 0 un_validate_install_path_check un_validate_path_invalid un_validate_path_invalid
un_validate_install_path_check:
  StrLen $R7 $R6
  StrCpy $R8 $RelativePathFull $R7
  StrCmp $R8 $R6 0 un_validate_path_invalid
  StrCpy $R8 $RelativePathFull 1 $R7
  StrCmp $R8 "\" 0 un_validate_path_invalid

  ; Validate every existing intermediate directory. Missing components are
  ; allowed because uninstall can be retried after files or folders were removed.
  StrCpy $R2 "0"
  StrLen $R3 $R1
un_validate_parent_scan:
  IntCmp $R2 $R3 un_validate_leaf_attributes un_validate_parent_scan_char un_validate_parent_scan_char
un_validate_parent_scan_char:
  StrCpy $R4 $R1 1 $R2
  StrCmp $R4 "\" un_validate_parent_check
  IntOp $R2 $R2 + 1
  Goto un_validate_parent_scan
un_validate_parent_check:
  StrCpy $R8 $R1 $R2
  StrCpy $R9 "$R6\$R8"
  System::Call 'kernel32::GetFileAttributesW(w "$R9") i.R5'
  IntCmp $R5 -1 un_validate_parent_skip un_validate_parent_attributes_read un_validate_parent_attributes_read
un_validate_parent_attributes_read:
  IntOp $R4 $R5 & 0x10
  IntCmp $R4 0 un_validate_path_invalid un_validate_parent_reparse_check un_validate_parent_reparse_check
un_validate_parent_reparse_check:
  IntOp $R4 $R5 & 0x400
  IntCmp $R4 0 un_validate_parent_skip un_validate_path_invalid un_validate_path_invalid
un_validate_parent_skip:
  IntOp $R2 $R2 + 1
  Goto un_validate_parent_scan
un_validate_leaf_attributes:
  System::Call 'kernel32::GetFileAttributesW(w "$RelativePathFull") i.R9'
  IntCmp $R9 -1 un_validate_path_valid un_validate_leaf_attributes_read un_validate_leaf_attributes_read
un_validate_leaf_attributes_read:
  ; File attributes are irrelevant; if the leaf is a directory, reject a reparse point.
  IntOp $R4 $R9 & 0x10
  IntCmp $R4 0 un_validate_path_valid un_validate_leaf_reparse_check un_validate_leaf_reparse_check
un_validate_leaf_reparse_check:
  IntOp $R4 $R9 & 0x400
  IntCmp $R4 0 un_validate_path_valid un_validate_path_invalid un_validate_path_invalid
un_validate_path_valid:
  StrCpy $RelativePathValid "1"
  Return
un_validate_path_invalid:
  StrCpy $RelativePathValid "0"
FunctionEnd

Function un.HasReparsePointInTree
  ; Preflight RMDir /r and fail closed on reparse points or incomplete enumeration.
  Exch $R0
  Push $R1
  Push $R2
  Push $R3
  Push $R4
  Push $R5
  Push $R6
  Push $R7
  Push $R8
  Push $R9
  StrCpy $TreeScanResult "0"
  GetFullPathName $R0 "$R0"
  ; Check path ancestors too, so a redirected profile or parent junction fails closed.
  StrCpy $R8 $R0 3
  System::Call 'kernel32::GetFileAttributesW(w "$R8") i.R3'
  IntCmp $R3 -1 un_tree_has_reparse un_tree_volume_attributes_read un_tree_volume_attributes_read
un_tree_volume_attributes_read:
  IntOp $R4 $R3 & 0x400
  IntCmp $R4 0 un_tree_ancestor_scan_init un_tree_has_reparse un_tree_has_reparse
un_tree_ancestor_scan_init:
  StrCpy $R2 3
  StrLen $R7 $R0
un_tree_ancestor_scan:
  IntCmp $R2 $R7 un_tree_root_attributes un_tree_ancestor_char un_tree_ancestor_char
un_tree_ancestor_char:
  StrCpy $R4 $R0 1 $R2
  StrCmp $R4 "\" un_tree_ancestor_check
  IntOp $R2 $R2 + 1
  Goto un_tree_ancestor_scan
un_tree_ancestor_check:
  StrCpy $R8 $R0 $R2
  System::Call 'kernel32::GetFileAttributesW(w "$R8") i.R3'
  IntCmp $R3 -1 un_tree_has_reparse un_tree_ancestor_attributes_read un_tree_ancestor_attributes_read
un_tree_ancestor_attributes_read:
  IntOp $R4 $R3 & 0x400
  IntCmp $R4 0 un_tree_ancestor_next un_tree_has_reparse un_tree_has_reparse
un_tree_ancestor_next:
  IntOp $R2 $R2 + 1
  Goto un_tree_ancestor_scan
un_tree_root_attributes:
  System::Call 'kernel32::GetFileAttributesW(w "$R0") i.R3'
  IntCmp $R3 -1 un_tree_has_reparse un_tree_root_attributes_read un_tree_root_attributes_read
un_tree_root_attributes_read:
  IntOp $R4 $R3 & 0x400
  IntCmp $R4 0 un_tree_root_is_directory un_tree_has_reparse un_tree_has_reparse
un_tree_root_is_directory:
  IntOp $R4 $R3 & 0x10
  IntCmp $R4 0 un_tree_has_reparse un_tree_enumerate un_tree_enumerate
un_tree_enumerate:
  StrCpy $R2 ""
  ClearErrors
  FindFirst $R1 $R2 "$R0\*"
  IfErrors un_tree_has_reparse
un_tree_loop:
  StrCmp $R2 "" un_tree_safe
  StrCmp $R2 "." un_tree_next
  StrCmp $R2 ".." un_tree_next
  System::Call 'kernel32::GetFileAttributesW(w "$R0\$R2") i.R3'
  IntCmp $R3 -1 un_tree_unsafe_close un_tree_child_attributes_read un_tree_child_attributes_read
un_tree_child_attributes_read:
  IntOp $R4 $R3 & 0x400
  IntCmp $R4 0 un_tree_child_is_directory un_tree_unsafe_close un_tree_unsafe_close
un_tree_child_is_directory:
  IntOp $R4 $R3 & 0x10
  IntCmp $R4 0 un_tree_next un_tree_recurse_child un_tree_recurse_child
un_tree_recurse_child:
  Push "$R0\$R2"
  Call un.HasReparsePointInTree
  Pop $R5
  StrCmp $R5 "1" un_tree_unsafe_close
un_tree_next:
  FindNext $R1 $R2
  Goto un_tree_loop
un_tree_unsafe_close:
  FindClose $R1
  Goto un_tree_has_reparse
un_tree_safe:
  FindClose $R1
  Goto un_tree_return
un_tree_has_reparse:
  StrCpy $TreeScanResult "1"
un_tree_return:
  Pop $R9
  Pop $R8
  Pop $R7
  Pop $R6
  Pop $R5
  Pop $R4
  Pop $R3
  Pop $R2
  Pop $R1
  Pop $R0
  Push $TreeScanResult
FunctionEnd

Function un.ValidateInstalledLists
  IfFileExists "$INSTDIR\installed-files.txt" 0 un_validate_list_missing
  ClearErrors
  FileOpen $R0 "$INSTDIR\installed-files.txt" r
  IfErrors un_validate_list_missing
  StrCpy $InstalledFileCount "0"
un_validate_files_loop:
  ClearErrors
  FileRead $R0 $R1
  IfErrors un_validate_files_done
  ${UnStrRep} $R1 $R1 "$\r" ""
  ${UnStrRep} $R1 $R1 "$\n" ""
  StrCmp $R1 "" un_validate_files_loop
  Call un.ValidateInstallRelativePath
  StrCmp $RelativePathValid "1" 0 un_validate_files_invalid
  IntOp $InstalledFileCount $InstalledFileCount + 1
  Goto un_validate_files_loop
un_validate_files_invalid:
  FileClose $R0
  Goto un_validate_file_list_invalid
un_validate_files_done:
  FileClose $R0
  StrCmp $InstalledFileCount "0" un_validate_file_list_invalid

  IfFileExists "$INSTDIR\installed-directories.txt" 0 un_validate_list_missing
  ClearErrors
  FileOpen $R0 "$INSTDIR\installed-directories.txt" r
  IfErrors un_validate_list_missing
un_validate_directories_loop:
  ClearErrors
  FileRead $R0 $R1
  IfErrors un_validate_directories_done
  ${UnStrRep} $R1 $R1 "$\r" ""
  ${UnStrRep} $R1 $R1 "$\n" ""
  StrCmp $R1 "" un_validate_directories_loop
  Call un.ValidateInstallRelativePath
  StrCmp $RelativePathValid "1" 0 un_validate_directories_invalid
  Goto un_validate_directories_loop
un_validate_directories_invalid:
  FileClose $R0
  Goto un_validate_directory_list_invalid
un_validate_directories_done:
  FileClose $R0
  Return
un_validate_list_missing:
  MessageBox MB_OK|MB_ICONSTOP "安装文件清单缺失或无法读取。请重新安装后再卸载。"
  SetErrorLevel 1
  Abort
un_validate_file_list_invalid:
  MessageBox MB_OK|MB_ICONSTOP "安装文件清单包含越界路径或经过重解析目录的路径。为保护其他文件，卸载已停止。"
  SetErrorLevel 1
  Abort
un_validate_directory_list_invalid:
  MessageBox MB_OK|MB_ICONSTOP "安装目录清单包含越界路径或经过重解析目录的路径。为保护其他文件，卸载已停止。"
  SetErrorLevel 1
  Abort
FunctionEnd

Function un.DeleteInstalledFiles
  ClearErrors
  FileOpen $R0 "$INSTDIR\installed-files.txt" r
  IfErrors un_delete_list_failed
un_delete_files_loop:
  ClearErrors
  FileRead $R0 $R1
  IfErrors un_delete_files_done
  ${UnStrRep} $R1 $R1 "$\r" ""
  ${UnStrRep} $R1 $R1 "$\n" ""
  StrCmp $R1 "" un_delete_files_loop
  Call un.ValidateInstallRelativePath
  StrCmp $RelativePathValid "1" 0 un_delete_list_invalid
  StrCmp $R1 "uninstall.exe" un_delete_files_loop
  StrCmp $R1 "installed-files.txt" un_delete_files_loop
  StrCmp $R1 "installed-directories.txt" un_delete_files_loop
  IfFileExists "$INSTDIR\$R1" 0 un_delete_files_loop
  SetFileAttributes "$INSTDIR\$R1" NORMAL
  ClearErrors
  Delete "$INSTDIR\$R1"
  IfErrors un_delete_locked_file
  Goto un_delete_files_loop
un_delete_locked_file:
  GetTempFileName $R2 "$INSTDIR"
  Delete "$R2"
  Rename "$INSTDIR\$R1" "$R2"
  IfErrors un_delete_file_failed
  ClearErrors
  Delete /REBOOTOK "$R2"
  IfErrors un_delete_file_failed
  Goto un_delete_files_loop
un_delete_file_failed:
  FileClose $R0
  MessageBox MB_OK|MB_ICONSTOP "无法删除安装文件：$R1。请检查权限后重试。"
  SetErrorLevel 1
  Abort
un_delete_list_invalid:
  FileClose $R0
  MessageBox MB_OK|MB_ICONSTOP "安装文件清单包含越界路径或经过重解析目录的路径。为保护其他文件，卸载已停止。"
  SetErrorLevel 1
  Abort
un_delete_files_done:
  FileClose $R0

  ClearErrors
  FileOpen $R0 "$INSTDIR\installed-directories.txt" r
  IfErrors un_delete_list_failed
un_delete_directories_loop:
  ClearErrors
  FileRead $R0 $R1
  IfErrors un_delete_directories_done
  ${UnStrRep} $R1 $R1 "$\r" ""
  ${UnStrRep} $R1 $R1 "$\n" ""
  StrCmp $R1 "" un_delete_directories_loop
  Call un.ValidateInstallRelativePath
  StrCmp $RelativePathValid "1" 0 un_delete_directories_invalid
  IfFileExists "$INSTDIR\$R1" 0 un_delete_directories_loop
  RMDir /REBOOTOK "$INSTDIR\$R1"
  Goto un_delete_directories_loop
un_delete_directories_invalid:
  FileClose $R0
  MessageBox MB_OK|MB_ICONSTOP "安装目录清单包含越界路径或经过重解析目录的路径。为保护其他文件，卸载已停止。"
  SetErrorLevel 1
  Abort
un_delete_directories_done:
  FileClose $R0
  Delete /REBOOTOK "$INSTDIR\installed-files.txt"
  Delete /REBOOTOK "$INSTDIR\installed-directories.txt"
  GetTempFileName $R2 "$INSTDIR"
  Delete "$R2"
  ClearErrors
  Rename "$INSTDIR\uninstall.exe" "$R2"
  IfErrors un_delete_self_directly
  Delete /REBOOTOK "$R2"
  Goto un_delete_root
un_delete_self_directly:
  Delete /REBOOTOK "$INSTDIR\uninstall.exe"
un_delete_root:
  RMDir /REBOOTOK "$INSTDIR"
  Return
un_delete_list_failed:
  MessageBox MB_OK|MB_ICONSTOP "无法读取或删除安装清单。请重试卸载。"
  SetErrorLevel 1
  Abort
FunctionEnd
