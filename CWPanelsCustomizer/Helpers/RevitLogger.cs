using System;
using System.Diagnostics;
using System.IO;

namespace CWPanelsCustomizer.Helpers
{
    /// <summary>
    /// Статический логгер для записи в файл и Debug Output.
    /// Путь к логу: %USERPROFILE%\source\repos\CWPanelsCustomizer\logs\cwpanels.log
    /// </summary>
    internal static class RevitLogger
    {
        private static readonly object _lock = new object();
        private static readonly string _logPath;
        private static string _currentCommandName = string.Empty;

        private const long MAX_LOG_SIZE_BYTES = 5 * 1024 * 1024; // 5 МБ
        private const int MAX_BACKUPS = 3;

        static RevitLogger()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string logsDir = Path.Combine(userProfile, "source", "repos", "CWPanelsCustomizer", "logs");

            try
            {
                if (!Directory.Exists(logsDir))
                {
                    Directory.CreateDirectory(logsDir);
                }
            }
            catch
            {
                // Подавить ошибку создания директории
            }

            _logPath = Path.Combine(logsDir, "cwpanels.log");
        }

        /// <summary>
        /// Начать новую сессию команды
        /// </summary>
        public static void BeginSession(string commandName, string documentTitle = null)
        {
            _currentCommandName = commandName ?? string.Empty;

            string msg = documentTitle != null
                ? $"========== SESSION START: {commandName} (Document: {documentTitle}) =========="
                : $"========== SESSION START: {commandName} ==========";

            WriteLog("INF", msg);
        }

        /// <summary>
        /// Завершить сессию команды
        /// </summary>
        public static void EndSession(string result = null)
        {
            string msg = result != null
                ? $"========== SESSION END: {_currentCommandName} (Result: {result}) =========="
                : $"========== SESSION END: {_currentCommandName} ==========";

            WriteLog("INF", msg);
            _currentCommandName = string.Empty;
        }

        /// <summary>
        /// Отладочное сообщение
        /// </summary>
        public static void Debug(string message)
        {
            WriteLog("DBG", message);
        }

        /// <summary>
        /// Информационное сообщение
        /// </summary>
        public static void Info(string message)
        {
            WriteLog("INF", message);
        }

        /// <summary>
        /// Предупреждение
        /// </summary>
        public static void Warn(string message)
        {
            WriteLog("WRN", message);
        }

        /// <summary>
        /// Ошибка
        /// </summary>
        public static void Error(string message)
        {
            WriteLog("ERR", message);
        }

        /// <summary>
        /// Ошибка с исключением
        /// </summary>
        public static void Error(string message, Exception ex)
        {
            string fullMessage = ex != null
                ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}"
                : message;
            WriteLog("ERR", fullMessage);
        }

        /// <summary>
        /// Логирование координат точки
        /// </summary>
        public static void LogPoint(string label, double x, double y, double z,
                                    int? elementId = null, string elementCategory = null)
        {
            const double FEET_TO_MM = 304.8;

            string ftCoords = $"({x:F6}, {y:F6}, {z:F6}) ft";
            string mmCoords = $"({x * FEET_TO_MM:F1}, {y * FEET_TO_MM:F1}, {z * FEET_TO_MM:F1}) mm";

            string msg = $"{label}: {ftCoords} = {mmCoords}";

            if (elementId.HasValue)
            {
                msg += $" | ElementId={elementId.Value}";
            }

            if (!string.IsNullOrEmpty(elementCategory))
            {
                msg += $" | Category={elementCategory}";
            }

            WriteLog("XYZ", msg);
        }

        /// <summary>
        /// Логирование элемента Revit
        /// </summary>
        public static void LogElement(string label, int elementId,
                                      string familyName = null, string typeName = null, string extraInfo = null)
        {
            string msg = $"{label}: ElementId={elementId}";

            if (!string.IsNullOrEmpty(familyName))
            {
                msg += $" | Family='{familyName}'";
            }

            if (!string.IsNullOrEmpty(typeName))
            {
                msg += $" | Type='{typeName}'";
            }

            if (!string.IsNullOrEmpty(extraInfo))
            {
                msg += $" | {extraInfo}";
            }

            WriteLog("ELM", msg);
        }

        /// <summary>
        /// Логирование сводной информации (пары ключ-значение)
        /// </summary>
        public static void LogSummary(string methodTag, params (string key, object value)[] pairs)
        {
            if (pairs == null || pairs.Length == 0)
            {
                WriteLog("SUM", methodTag);
                return;
            }

            string[] parts = new string[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
            {
                parts[i] = $"{pairs[i].key}={pairs[i].value}";
            }

            string msg = $"{methodTag}: {string.Join(", ", parts)}";
            WriteLog("SUM", msg);
        }

        private static void WriteLog(string level, string message)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string commandTag = !string.IsNullOrEmpty(_currentCommandName)
                    ? $"[{_currentCommandName}] "
                    : string.Empty;

                string logLine = $"{timestamp} [{level}] {commandTag}{message}";

                // Дублировать в Debug Output IDE
                System.Diagnostics.Debug.WriteLine(logLine);

                // Записать в файл
                lock (_lock)
                {
                    // Проверить размер файла и выполнить ротацию при необходимости
                    RotateLogIfNeeded();

                    File.AppendAllText(_logPath, logLine + Environment.NewLine);
                }
            }
            catch
            {
                // Подавить любые ошибки записи в лог (не крашить Revit)
            }
        }

        private static void RotateLogIfNeeded()
        {
            try
            {
                if (!File.Exists(_logPath)) return;

                FileInfo fi = new FileInfo(_logPath);
                if (fi.Length < MAX_LOG_SIZE_BYTES) return;

                // Удалить самый старый бэкап
                string oldestBackup = _logPath + $".{MAX_BACKUPS}";
                if (File.Exists(oldestBackup))
                {
                    File.Delete(oldestBackup);
                }

                // Сдвинуть существующие бэкапы
                for (int i = MAX_BACKUPS - 1; i >= 1; i--)
                {
                    string src = _logPath + $".{i}";
                    string dst = _logPath + $".{i + 1}";

                    if (File.Exists(src))
                    {
                        File.Move(src, dst);
                    }
                }

                // Переместить текущий лог в .1
                File.Move(_logPath, _logPath + ".1");
            }
            catch
            {
                // Подавить ошибки ротации
            }
        }
    }
}
