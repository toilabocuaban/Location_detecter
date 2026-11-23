using System;
using System.IO;
using System.Text.RegularExpressions;

namespace PalletDetection.Rcs.Logging
{
    /// <summary>
    /// Triển khai IRcsLogger ghi log ra file theo ngày trong thư mục Logs/
    /// Tự động ẩn token xác thực để không làm lộ thông tin nhạy cảm.
    /// </summary>
    public class RcsLogger : IRcsLogger
    {
        private static readonly object _fileLock = new object();
        private readonly string _logDirectory;
        private readonly string _configuredToken;

        /// <summary>
        /// Event bắn ra mỗi khi có một dòng log mới, hữu ích cho màn hình test hoặc UI theo dõi.
        /// </summary>
        public event Action<string> OnLogEntry;

        // Regex phát hiện trường tokenCode trong chuỗi JSON để che tự động
        private static readonly Regex TokenRegex = new Regex(
            "(\"tokenCode\"\\s*:\\s*\")([^\"]*)(\")", 
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public RcsLogger() : this(null, null)
        {
        }

        public RcsLogger(string logDirectory = null, string configuredToken = null)
        {
            if (string.IsNullOrWhiteSpace(logDirectory))
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                try
                {
                    string asmLoc = Path.GetDirectoryName(typeof(RcsLogger).Assembly.Location);
                    if (!string.IsNullOrEmpty(asmLoc))
                    {
                        baseDir = asmLoc;
                    }
                }
                catch { }

                _logDirectory = Path.Combine(baseDir, "Logs");
            }
            else
            {
                _logDirectory = logDirectory;
            }

            _configuredToken = configuredToken;

            try
            {
                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }
            }
            catch
            {
                // Bỏ qua lỗi tạo thư mục ban đầu, xử lý an toàn khi ghi file
            }
        }

        public void LogInfo(string message)
        {
            WriteEntry("INFO", message);
        }

        public void LogWarning(string message)
        {
            WriteEntry("WARN", message);
        }

        public void LogError(string message, Exception ex = null)
        {
            string detail = message;
            if (ex != null)
            {
                detail += $" | Exception: {ex.GetType().Name}: {ex.Message}\nStackTrace: {ex.StackTrace}";
            }
            WriteEntry("ERROR", detail);
        }

        /// <summary>
        /// Ẩn chuỗi token và dữ liệu nhạy cảm trong văn bản / JSON.
        /// </summary>
        public string MaskSensitiveData(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // 1. Quét bằng Regex cho trường "tokenCode": "..."
            string masked = TokenRegex.Replace(text, "$1***MASKED***$3");

            // 2. Nếu có configuredToken cụ thể dài >= 4 ký tự, che triệt để nếu token đó xuất hiện ở bất kỳ đâu
            if (!string.IsNullOrEmpty(_configuredToken) && _configuredToken.Trim().Length >= 4)
            {
                string tokenTrimmed = _configuredToken.Trim();
                masked = masked.Replace(tokenTrimmed, "***MASKED***");
            }

            return masked;
        }

        private void WriteEntry(string level, string rawMessage)
        {
            try
            {
                string safeMessage = MaskSensitiveData(rawMessage);
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string logLine = $"[{timestamp}] [{level}] {safeMessage}";

                // Bắn event
                OnLogEntry?.Invoke(logLine);

                // Ghi file
                lock (_fileLock)
                {
                    if (!Directory.Exists(_logDirectory))
                    {
                        Directory.CreateDirectory(_logDirectory);
                    }

                    string logFileName = $"rcs_api_{DateTime.Now:yyyyMMdd}.log";
                    string filePath = Path.Combine(_logDirectory, logFileName);

                    File.AppendAllText(filePath, logLine + Environment.NewLine);
                }
            }
            catch
            {
                // Không để lỗi ghi log làm crash ứng dụng
            }
        }
    }
}
