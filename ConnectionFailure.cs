using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security;

namespace SimpleVPN
{
    // Diagnostics contain fixed identifiers and a numeric native error only.
    // Exception messages, filenames, stack traces and core output may contain
    // subscription credentials, so none of them is written to the log or UI.
    public sealed class ConnectionFailure
    {
        public string Code { get; private set; }
        public string ExceptionKind { get; private set; }
        public int? NativeErrorCode { get; private set; }
        public string DetailText { get; private set; }
        public string Notice { get; private set; }

        private static string KnownCode(string message)
        {
            switch (message)
            {
                case "CORE_MISSING": case "JOB_CREATE_FAILED": case "JOB_SETUP_FAILED":
                case "CORE_START_FAILED": case "CORE_JOB_FAILED": case "CORE_EXITED":
                case "CORE_TIMEOUT": case "WATCHDOG_START_FAILED":
                case "CONNECTIVITY_CHECK_FAILED": case "PROXY_QUERY_FAILED":
                case "PROXY_SET_FAILED": case "RECOVERY_PENDING": case "SAVE_FLUSH_FAILED":
                    return message;
                default: return null;
            }
        }

        private static string Kind(Exception error)
        {
            if (error == null) return "NONE";
            if (error is FileNotFoundException) return "FILE_NOT_FOUND";
            if (error is DirectoryNotFoundException) return "DIRECTORY_NOT_FOUND";
            if (error is BadImageFormatException) return "BAD_IMAGE_FORMAT";
            if (error is DllNotFoundException) return "DLL_NOT_FOUND";
            if (error is EntryPointNotFoundException) return "ENTRY_POINT_NOT_FOUND";
            if (error is MissingMethodException) return "MISSING_METHOD";
            if (error is TypeInitializationException) return "TYPE_INITIALIZATION";
            if (error is UnauthorizedAccessException) return "ACCESS_DENIED";
            if (error is SecurityException) return "SECURITY";
            if (error is Win32Exception) return "WIN32";
            if (error is IOException) return "IO";
            if (error is ArgumentException) return "ARGUMENT";
            if (error is ObjectDisposedException) return "OBJECT_DISPOSED";
            if (error is InvalidOperationException) return "INVALID_OPERATION";
            if (error is NotSupportedException) return "NOT_SUPPORTED";
            return "OTHER";
        }

        public static ConnectionFailure FromException(Exception error)
        {
            var result = new ConnectionFailure { ExceptionKind = Kind(error) };
            string fallback = "UNEXPECTED";
            Exception current = error;
            // Framework wrappers can hide a useful native failure. Bound the
            // traversal and classify only known types, never their type names.
            for (int depth = 0; current != null && depth < 8; depth++, current = current.InnerException)
            {
                // Only our concrete IOException/FileNotFoundException messages
                // carry internal codes. Custom Message getters are untrusted.
                if (result.Code == null && (current.GetType() == typeof(IOException) || current.GetType() == typeof(FileNotFoundException)))
                    result.Code = KnownCode(current.Message);
                var native = current as Win32Exception;
                if (native != null && !result.NativeErrorCode.HasValue) result.NativeErrorCode = native.NativeErrorCode;
                if (fallback == "UNEXPECTED")
                {
                    if (current is UnauthorizedAccessException || current is SecurityException) fallback = "ACCESS_DENIED";
                    else if (current is BadImageFormatException || current is DllNotFoundException || current is EntryPointNotFoundException || current is MissingMethodException) fallback = "COMPONENT_INCOMPATIBLE";
                    else if (current is FileNotFoundException || current is DirectoryNotFoundException) fallback = "COMPONENT_MISSING";
                    else if (current is ArgumentException) fallback = "OPTIONS_INVALID";
                    else if (native != null) fallback = "NATIVE_ERROR";
                }
            }
            if (result.Code == null) result.Code = fallback;
            result.DetailText = "Не удалось запустить VPN";
            result.Notice = "Не удалось подготовить VPN. Закройте приложение и откройте его снова.";
            switch (result.Code)
            {
                case "CONNECTIVITY_CHECK_FAILED":
                    result.DetailText = "Сервер не ответил";
                    result.Notice = "Сервер не ответил вовремя. Повторите подключение или выберите другой сервер.";
                    break;
                case "CORE_MISSING": case "COMPONENT_MISSING": case "COMPONENT_INCOMPATIBLE":
                    result.Notice = "Не удалось запустить компонент VPN. Установите приложение заново.";
                    break;
                case "CORE_EXITED":
                    result.Notice = "Подключение остановилось. Обновите подписку и попробуйте ещё раз.";
                    break;
                case "CORE_TIMEOUT":
                    result.Notice = "VPN не успел запуститься. Повторите подключение.";
                    break;
                case "OPTIONS_INVALID":
                    result.DetailText = "Проверьте настройки";
                    result.Notice = "Проверьте настройки подключения и попробуйте ещё раз.";
                    break;
                case "PROXY_QUERY_FAILED": case "PROXY_SET_FAILED": case "RECOVERY_PENDING":
                    result.DetailText = "Не удалось настроить подключение";
                    result.Notice = "Windows не удалось настроить подключение. Закройте приложение и откройте его снова.";
                    break;
                case "ACCESS_DENIED": case "SAVE_FLUSH_FAILED":
                    result.Notice = "Не удалось сохранить настройки VPN. Проверьте доступ к папке приложения.";
                    break;
            }
            return result;
        }

        public void WriteTo(LocalStore store)
        {
            store.Log("CONNECT_REASON_" + Code);
            store.Log("CONNECT_TYPE_" + ExceptionKind);
            if (NativeErrorCode.HasValue)
                store.Log("CONNECT_NATIVE_" + unchecked((uint)NativeErrorCode.Value).ToString("X8", CultureInfo.InvariantCulture));
        }
    }
}
