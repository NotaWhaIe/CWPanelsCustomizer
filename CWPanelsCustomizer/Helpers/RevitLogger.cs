using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace CWPanelsCustomizer.Helpers
{
    /// <summary>
    /// Логгер для записи в файл и Debug Output.
    /// Создаёт отдельный файл для каждого запуска команды.
    /// Каждый документ Revit имеет свой экземпляр логгера (изоляция между документами).
    /// Путь к логам: %USERPROFILE%\source\repos\CWPanelsCustomizer\logs\
    /// Формат имени: YYYY-MM-DD_HHmmss_CommandName_ProjectName.log
    /// </summary>
    internal class RevitLogger
    {
        private static readonly object _globalLock = new object();
        private static readonly string _logsDir;
        private static readonly Dictionary<int, RevitLogger> _instances = new Dictionary<int, RevitLogger>();

        private readonly object _instanceLock = new object();
        private readonly string _documentName;
        private string _currentLogPath = null;
        private string _currentCommandName = string.Empty;

        private const int MAX_LOG_FILES = 30; // Хранить последние N файлов логов

        static RevitLogger()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _logsDir = Path.Combine(userProfile, "source", "repos", "CWPanelsCustomizer", "logs");

            try
            {
                if (!Directory.Exists(_logsDir))
                {
                    Directory.CreateDirectory(_logsDir);
                }
            }
            catch
            {
                // Подавить ошибку создания директории
            }
        }

        private RevitLogger(string documentName)
        {
            _documentName = documentName;
        }

        /// <summary>
        /// Получить логгер для конкретного документа Revit
        /// </summary>
        public static RevitLogger GetLogger(Document doc)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            int docHash = doc.GetHashCode();
            string rawTitle = doc.Title ?? "UnknownProject";
            // Убрать числовой префикс вида "20251216_" из начала названия документа
            string docName = Regex.Replace(rawTitle, @"^\d+_", string.Empty);

            lock (_globalLock)
            {
                if (!_instances.ContainsKey(docHash))
                {
                    _instances[docHash] = new RevitLogger(docName);
                }
                return _instances[docHash];
            }
        }

        /// <summary>
        /// Удалить экземпляр логгера для документа (вызывать при закрытии документа)
        /// </summary>
        public static void RemoveLogger(Document doc)
        {
            if (doc == null) return;

            int docHash = doc.GetHashCode();

            lock (_globalLock)
            {
                _instances.Remove(docHash);
            }
        }

        /// <summary>
        /// Начать новую сессию команды (создаёт новый файл лога)
        /// </summary>
        public void BeginSession(string commandName, string documentTitle = null)
        {
            _currentCommandName = commandName ?? "UnknownCommand";

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            string safeCommandName = MakeSafeFileName(_currentCommandName);
            string safeDocumentName = MakeSafeFileName(_documentName);
            string baseName = $"{timestamp}__{safeDocumentName}__{safeCommandName}";

            lock (_instanceLock)
            {
                _currentLogPath = Path.Combine(_logsDir, baseName + ".log");

                CleanupOldLogs();
            }

            WriteLog("INF", "=== START ===");
        }

        /// <summary>
        /// Завершить сессию команды
        /// </summary>
        public void EndSession(string result = null)
        {
            string msg = result != null
                ? $"=== END: {result} ==="
                : "=== END ===";

            WriteLog("INF", msg);

            lock (_instanceLock)
            {
                _currentLogPath = null;
                _currentCommandName = string.Empty;
            }
        }

        /// <summary>
        /// Отладочное сообщение
        /// </summary>
        public void Debug(string message)
        {
            WriteLog("DBG", message);
        }

        /// <summary>
        /// Информационное сообщение
        /// </summary>
        public void Info(string message)
        {
            WriteLog("INF", message);
        }

        /// <summary>
        /// Предупреждение
        /// </summary>
        public void Warn(string message)
        {
            WriteLog("WRN", message);
        }

        /// <summary>
        /// Ошибка
        /// </summary>
        public void Error(string message)
        {
            WriteLog("ERR", message);
        }

        /// <summary>
        /// Ошибка с исключением
        /// </summary>
        public void Error(string message, Exception ex)
        {
            string fullMessage = ex != null
                ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}"
                : message;
            WriteLog("ERR", fullMessage);
        }

        /// <summary>
        /// Логирование координат точки
        /// </summary>
        public void LogPoint(string label, double x, double y, double z,
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
        public void LogElement(string label, int elementId,
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
        public void LogSummary(string methodTag, params (string key, object value)[] pairs)
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

        private void WriteLog(string level, string message)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string logLine = $"{timestamp} [{level}] {message}";

                // Дублировать в Debug Output IDE
                System.Diagnostics.Debug.WriteLine(logLine);

                // Записать в файл
                lock (_instanceLock)
                {
                    if (_currentLogPath != null)
                    {
                        File.AppendAllText(_currentLogPath, logLine + Environment.NewLine);
                    }
                }
            }
            catch
            {
                // Подавить любые ошибки записи в лог (не крашить Revit)
            }
        }

        /// <summary>
        /// Оставить только последние MAX_LOG_FILES файлов, остальные удалить
        /// </summary>
        private void CleanupOldLogs()
        {
            try
            {
                if (!Directory.Exists(_logsDir)) return;

                var filesToDelete = Directory.GetFiles(_logsDir, "*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(fi => fi.LastWriteTime)
                    .Skip(MAX_LOG_FILES)
                    .ToList();

                foreach (var file in filesToDelete)
                {
                    try { file.Delete(); }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Сделать безопасное имя файла из строки
        /// </summary>
        private string MakeSafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Unknown";

            char[] invalidChars = Path.GetInvalidFileNameChars();
            string safe = name;

            foreach (char c in invalidChars)
            {
                safe = safe.Replace(c, '_');
            }

            // Ограничить длину
            if (safe.Length > 50)
                safe = safe.Substring(0, 50);

            return safe;
        }
    }
}
