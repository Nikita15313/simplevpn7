#define UNICODE
#define _UNICODE
#define WINVER 0x0601
#define _WIN32_WINNT 0x0601
#include <windows.h>
#include <shellapi.h>

/* No CRT or .NET dependency. Built solely against Windows 7 system APIs. */
#define CAP 1024
static WCHAR folder[CAP], child[CAP], scratch[CAP], command[CAP + 4];
static WCHAR windows[CAP], volume[CAP], filesystem[32];
static BOOL quiet = FALSE;
static DWORD Length(const WCHAR *s) { DWORD n = 0; while (s[n]) ++n; return n; }
static BOOL Equal(const WCHAR *a, const WCHAR *b) { while (*a && *a == *b) { ++a; ++b; } return *a == *b; }
static BOOL Join(WCHAR *out, const WCHAR *base, const WCHAR *suffix) {
    DWORD a = Length(base), b = Length(suffix), i;
    if (a + b >= CAP) return FALSE;
    for (i = 0; i < a; ++i) out[i] = base[i];
    for (i = 0; i <= b; ++i) out[a + i] = suffix[i];
    return TRUE;
}
static BOOL FileExists(const WCHAR *p) { DWORD a = GetFileAttributesW(p); return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY); }
static BOOL Installed(const WCHAR *key) {
    HKEY h;
    DWORD type = 0, value = 0, size = sizeof(value);
    LONG result = RegOpenKeyExW(HKEY_LOCAL_MACHINE, key, 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &h);
    if (result != ERROR_SUCCESS) return FALSE;
    result = RegQueryValueExW(h, L"Install", NULL, &type, (BYTE *)&value, &size);
    RegCloseKey(h);
    return result == ERROR_SUCCESS && type == REG_DWORD && value == 1;
}
__declspec(noreturn) static void Fail(UINT code, const WCHAR *message) {
    if (!quiet) MessageBoxW(NULL, message, L"Simple VPN", MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND);
    ExitProcess(code);
}
static DWORD ChildCreationFlags(void) {
    OSVERSIONINFOW version;
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits;
    BOOL inJob = FALSE;
    DWORD i;
    for (i = 0; i < sizeof(version); ++i) ((volatile BYTE *)&version)[i] = 0;
    version.dwOSVersionInfoSize = sizeof(version);
    /* The embedded supportedOS manifest makes this Win7-era API accurate here. */
#pragma warning(suppress: 4996)
    if (!GetVersionExW(&version))
        Fail(18, L"Не удалось определить среду запуска. Закройте приложение и откройте его с рабочего стола. Код: JOB_CHECK.");
    /* Windows 8+ supports nested jobs. Do not reject or escape its parent jobs. */
    if (version.dwMajorVersion > 6 || (version.dwMajorVersion == 6 && version.dwMinorVersion >= 2)) return 0;
    if (!IsProcessInJob(GetCurrentProcess(), NULL, &inJob))
        Fail(18, L"Не удалось проверить среду запуска. Закройте приложение и откройте его с рабочего стола. Код: JOB_CHECK.");
    if (!inJob) return 0;
    for (i = 0; i < sizeof(limits); ++i) ((volatile BYTE *)&limits)[i] = 0;
    if (!QueryInformationJobObject(NULL, JobObjectExtendedLimitInformation, &limits, sizeof(limits), NULL))
        Fail(18, L"Не удалось проверить среду запуска. Закройте окно установки и откройте Simple VPN с рабочего стола. Код: JOB_CHECK.");
    if (limits.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK) return 0;
    if (limits.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_BREAKAWAY_OK) return CREATE_BREAKAWAY_FROM_JOB;
    Fail(18, L"Закройте окно установки и откройте Simple VPN с рабочего стола. Windows 7 ограничила запуск из текущего окна. Код: JOB_CONTEXT.");
}
void WINAPI Start(void) {
    SYSTEM_INFO system;
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    DWORD n, i, code, length, creationFlags;
    int argc;
    LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    HANDLE probe;
    BOOL runtime = FALSE;
    if (argv) { quiet = argc == 2 && Equal(argv[1], L"--check"); LocalFree(argv); }
    GetNativeSystemInfo(&system);
    if (system.wProcessorArchitecture != PROCESSOR_ARCHITECTURE_AMD64)
        Fail(10, L"Этот архив предназначен для 64-разрядной Windows. Для этого компьютера нужна другая сборка. Код: ARCH.");
    n = GetModuleFileNameW(NULL, folder, CAP);
    if (!n || n >= CAP - 1) Fail(11, L"Переместите папку Simple VPN ближе к началу диска и откройте программу снова. Код: PATH.");
    while (n && folder[n - 1] != L'\\') --n;
    if (!n) Fail(11, L"Не удалось определить папку программы. Распакуйте архив заново. Код: PATH.");
    folder[n - 1] = 0;
    /* CLR2 and Win7 file APIs do not consistently support long paths. */
    if (Length(folder) > 180) Fail(11, L"Папка программы находится слишком глубоко. Переместите её, например, в C:\\SimpleVPN. Код: PATH.");
    if (!Join(child, folder, L"\\app\\SimpleVPN.exe") || !FileExists(child) ||
        !Join(scratch, folder, L"\\app\\SimpleVPN.exe.config") || !FileExists(scratch) ||
        !Join(scratch, folder, L"\\app\\core\\xray.exe") || !FileExists(scratch))
        Fail(12, L"Не хватает файлов программы. Распакуйте весь архив целиком и откройте SimpleVPN.exe из полученной папки. Код: FILES.");
    if (!GetVolumePathNameW(folder, volume, CAP) || GetDriveTypeW(volume) != DRIVE_FIXED ||
        !GetVolumeInformationW(volume, NULL, 0, NULL, NULL, NULL, filesystem, 32) || !Equal(filesystem, L"NTFS"))
        Fail(13, L"Скопируйте всю папку Simple VPN с флешки или сетевого диска на локальный диск компьютера и откройте снова. Код: DISK.");
    n = GetWindowsDirectoryW(windows, CAP);
    if (n && n < CAP) {
        if (Installed(L"SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v3.5") &&
            Join(scratch, windows, L"\\Microsoft.NET\\Framework64\\v2.0.50727\\mscorwks.dll") && FileExists(scratch)) runtime = TRUE;
        if (!runtime && Installed(L"SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full") &&
            Join(scratch, windows, L"\\Microsoft.NET\\Framework64\\v4.0.30319\\clr.dll") && FileExists(scratch)) runtime = TRUE;
    }
    if (!runtime)
        Fail(14, L"В Windows отключён компонент для запуска этой версии Simple VPN: Microsoft .NET Framework 3.5.1. Включите его в компонентах Windows или передайте это сообщение человеку, который настраивает компьютер. Устанавливать .NET 4.8 для этой сборки не нужно. Код: NET35.");
    Join(scratch, folder, L"\\app\\");
    length = Length(scratch);
    wsprintfW(scratch + length, L"launch-check-%08lx-%08lx.tmp", GetCurrentProcessId(), GetTickCount());
    probe = CreateFileW(scratch, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, NULL);
    if (probe == INVALID_HANDLE_VALUE)
        Fail(15, L"В эту папку нельзя сохранить настройки. Переместите всю папку Simple VPN в свои «Документы» и откройте программу снова. Код: WRITE.");
    CloseHandle(probe);
    if (quiet) ExitProcess(0);
    command[0] = L'"';
    for (i = 0; child[i]; ++i) command[i + 1] = child[i];
    command[i + 1] = L'"'; command[i + 2] = 0;
    for (i = 0; i < sizeof(startup); ++i) ((volatile BYTE *)&startup)[i] = 0;
    for (i = 0; i < sizeof(process); ++i) ((volatile BYTE *)&process)[i] = 0;
    startup.cb = sizeof(startup);
    Join(scratch, folder, L"\\app");
    creationFlags = ChildCreationFlags();
    if (!CreateProcessW(child, command, NULL, NULL, FALSE, creationFlags, NULL, scratch, &startup, &process))
        Fail(16, L"Windows не смогла открыть Simple VPN. Распакуйте весь архив заново. Если ошибка повторится, передайте сообщение человеку, который настраивает компьютер. Код: START.");
    CloseHandle(process.hThread);
    WaitForSingleObject(process.hProcess, INFINITE);
    code = 0;
    GetExitCodeProcess(process.hProcess, &code);
    CloseHandle(process.hProcess);
    if (code != 0)
        Fail(17, L"Simple VPN завершился с ошибкой. Откройте программу снова. Если это повторится, передайте сообщение человеку, который настраивает компьютер. Код: EXIT.");
    ExitProcess(0);
}
