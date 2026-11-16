#define UNICODE
#define _UNICODE
#define WINVER 0x0601
#define _WIN32_WINNT 0x0601
#include <windows.h>
#include <stdio.h>
#include <setjmp.h>

static jmp_buf returned;
static DWORD majorVersion, minorVersion, jobLimits, exitCode;
static BOOL versionSuccess, membershipSuccess, querySuccess, member;
static int queries, memberships, failures;

static BOOL WINAPI TestGetVersionExW(LPOSVERSIONINFOW value) {
    value->dwMajorVersion = majorVersion;
    value->dwMinorVersion = minorVersion;
    return versionSuccess;
}
static BOOL WINAPI TestIsProcessInJob(HANDLE process, HANDLE jobHandle, PBOOL value) {
    (void)process; (void)jobHandle;
    memberships++;
    *value = member;
    return membershipSuccess;
}
static BOOL WINAPI TestQueryInformationJobObject(HANDLE handle, JOBOBJECTINFOCLASS kind, LPVOID value, DWORD size, LPDWORD length) {
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION *limits = (JOBOBJECT_EXTENDED_LIMIT_INFORMATION *)value;
    (void)handle; (void)kind; (void)size; (void)length;
    queries++;
    limits->BasicLimitInformation.LimitFlags = jobLimits;
    return querySuccess;
}
__declspec(noreturn) static void WINAPI TestExitProcess(UINT code) {
    exitCode = code;
    longjmp(returned, 1);
}

/* Execute the production policy function against deterministic Win7/Win8 API
   results; no GUI, real child, or current-process job is created by this test. */
#define GetVersionExW TestGetVersionExW
#define IsProcessInJob TestIsProcessInJob
#define QueryInformationJobObject TestQueryInformationJobObject
#define ExitProcess TestExitProcess
#include "../launcher/Launcher.c"
#undef GetVersionExW
#undef IsProcessInJob
#undef QueryInformationJobObject
#undef ExitProcess

static void Check(const char *name, DWORD expectedFlags, DWORD expectedExit, int expectedMemberships, int expectedQueries) {
    DWORD flags = 0;
    exitCode = 0; memberships = 0; queries = 0; quiet = TRUE;
    if (setjmp(returned) == 0) flags = ChildCreationFlags();
    if (exitCode != expectedExit || (!exitCode && flags != expectedFlags) || memberships != expectedMemberships || queries != expectedQueries) {
        printf("FAIL %s flags=%lu exit=%lu membership=%d query=%d\n", name, flags, exitCode, memberships, queries);
        failures++;
    } else printf("PASS %s\n", name);
}

int main(void) {
    majorVersion = 6; minorVersion = 1;
    versionSuccess = membershipSuccess = querySuccess = TRUE;
    member = FALSE; jobLimits = 0;
    Check("Win7 without a parent job", 0, 0, 1, 0);
    member = TRUE; jobLimits = JOB_OBJECT_LIMIT_BREAKAWAY_OK;
    Check("Win7 explicitly permits breakaway", CREATE_BREAKAWAY_FROM_JOB, 0, 1, 1);
    jobLimits = JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
    Check("Win7 silent breakaway needs no flag", 0, 0, 1, 1);
    jobLimits |= JOB_OBJECT_LIMIT_BREAKAWAY_OK;
    Check("Win7 silent breakaway takes precedence", 0, 0, 1, 1);
    jobLimits = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    Check("Win7 nonbreakable parent requires relaunch", 0, 18, 1, 1);
    querySuccess = FALSE;
    Check("Win7 failed query is explicit", 0, 18, 1, 1);
    membershipSuccess = FALSE;
    Check("Win7 failed membership check is explicit", 0, 18, 1, 0);
    minorVersion = 2;
    Check("Win8 keeps its nested-job behavior", 0, 0, 0, 0);
    majorVersion = 10; minorVersion = 0;
    Check("Win10 and newer keep their job context", 0, 0, 0, 0);
    versionSuccess = FALSE;
    Check("OS query failure is explicit", 0, 18, 0, 0);
    printf("Launcher job policy: 10 cases, %d failures.\n", failures);
    return failures ? 1 : 0;
}
