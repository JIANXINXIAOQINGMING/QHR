#include <windows.h>

#define PATH_CAPACITY 32768

static BOOL AppendText(WCHAR *destination, SIZE_T capacity, const WCHAR *text)
{
    SIZE_T currentLength = (SIZE_T)lstrlenW(destination);
    SIZE_T textLength = (SIZE_T)lstrlenW(text);
    if (currentLength + textLength + 1 > capacity) return FALSE;
    CopyMemory(destination + currentLength, text, (textLength + 1) * sizeof(WCHAR));
    return TRUE;
}

static const WCHAR *SkipExecutableArgument(const WCHAR *commandLine)
{
    const WCHAR *cursor = commandLine;
    if (*cursor == L'"')
    {
        cursor++;
        while (*cursor != L'\0' && *cursor != L'"') cursor++;
        if (*cursor == L'"') cursor++;
    }
    else
    {
        while (*cursor != L'\0' && *cursor != L' ' && *cursor != L'\t') cursor++;
    }

    while (*cursor == L' ' || *cursor == L'\t') cursor++;
    return cursor;
}

static void ShowLaunchError(const WCHAR *message)
{
    MessageBoxW(NULL, message, L"QHR 加班助手", MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previousInstance, PWSTR ignoredCommandLine, int showCommand)
{
    (void)instance;
    (void)previousInstance;
    (void)ignoredCommandLine;
    (void)showCommand;

    WCHAR launcherDirectory[PATH_CAPACITY] = {0};
    DWORD moduleLength = GetModuleFileNameW(NULL, launcherDirectory, PATH_CAPACITY);
    if (moduleLength == 0 || moduleLength >= PATH_CAPACITY)
    {
        ShowLaunchError(L"无法读取 QHR 启动器所在目录。");
        return 1;
    }

    WCHAR *lastSeparator = launcherDirectory + moduleLength;
    while (lastSeparator > launcherDirectory && *lastSeparator != L'\\' && *lastSeparator != L'/')
        lastSeparator--;
    if (lastSeparator == launcherDirectory)
    {
        ShowLaunchError(L"QHR 启动器路径无效。");
        return 1;
    }
    *lastSeparator = L'\0';

    WCHAR applicationDirectory[PATH_CAPACITY] = {0};
    if (!AppendText(applicationDirectory, PATH_CAPACITY, launcherDirectory) ||
        !AppendText(applicationDirectory, PATH_CAPACITY, L"\\app"))
    {
        ShowLaunchError(L"QHR 程序目录路径过长。");
        return 1;
    }

    WCHAR applicationPath[PATH_CAPACITY] = {0};
    if (!AppendText(applicationPath, PATH_CAPACITY, applicationDirectory) ||
        !AppendText(applicationPath, PATH_CAPACITY, L"\\QHR.Overtime.exe"))
    {
        ShowLaunchError(L"QHR 主程序路径过长。");
        return 1;
    }

    if (GetFileAttributesW(applicationPath) == INVALID_FILE_ATTRIBUTES)
    {
        ShowLaunchError(L"找不到 app\\QHR.Overtime.exe。请完整解压发布包后再运行 QHR.exe。");
        return 2;
    }

    const WCHAR *forwardedArguments = SkipExecutableArgument(GetCommandLineW());
    SIZE_T applicationLength = (SIZE_T)lstrlenW(applicationPath);
    SIZE_T argumentLength = (SIZE_T)lstrlenW(forwardedArguments);
    SIZE_T commandCapacity = applicationLength + argumentLength + 6;
    WCHAR *childCommandLine = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY,
                                        commandCapacity * sizeof(WCHAR));
    if (childCommandLine == NULL)
    {
        ShowLaunchError(L"内存不足，无法启动 QHR。");
        return 3;
    }

    AppendText(childCommandLine, commandCapacity, L"\"");
    AppendText(childCommandLine, commandCapacity, applicationPath);
    AppendText(childCommandLine, commandCapacity, L"\"");
    if (argumentLength > 0)
    {
        AppendText(childCommandLine, commandCapacity, L" ");
        AppendText(childCommandLine, commandCapacity, forwardedArguments);
    }

    STARTUPINFOW startupInfo = {0};
    PROCESS_INFORMATION processInformation = {0};
    startupInfo.cb = sizeof(startupInfo);
    startupInfo.dwFlags = STARTF_USESHOWWINDOW;
    startupInfo.wShowWindow = (WORD)showCommand;
    BOOL launched = CreateProcessW(
        applicationPath,
        childCommandLine,
        NULL,
        NULL,
        FALSE,
        0,
        NULL,
        applicationDirectory,
        &startupInfo,
        &processInformation);
    DWORD launchError = launched ? ERROR_SUCCESS : GetLastError();
    HeapFree(GetProcessHeap(), 0, childCommandLine);

    if (!launched)
    {
        WCHAR errorMessage[512] = {0};
        DWORD prefixLength = (DWORD)lstrlenW(L"无法启动 QHR 主程序。Windows 错误：");
        CopyMemory(errorMessage, L"无法启动 QHR 主程序。Windows 错误：", prefixLength * sizeof(WCHAR));
        WCHAR errorNumber[32] = {0};
        DWORD value = launchError;
        int index = 0;
        do
        {
            errorNumber[index++] = (WCHAR)(L'0' + value % 10);
            value /= 10;
        } while (value > 0 && index < 31);
        for (int left = 0, right = index - 1; left < right; left++, right--)
        {
            WCHAR temporary = errorNumber[left];
            errorNumber[left] = errorNumber[right];
            errorNumber[right] = temporary;
        }
        AppendText(errorMessage, 512, errorNumber);
        ShowLaunchError(errorMessage);
        return 4;
    }

    CloseHandle(processInformation.hThread);
    CloseHandle(processInformation.hProcess);
    return 0;
}
