using Newtonsoft.Json;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PalletDetection.Models
{
    /// <summary>
    /// Cấu hình thông tin kết nối Camera Hikvision và bảo mật mật khẩu bằng Windows DPAPI.
    /// Phạm vi LocalMachine kết hợp Entropy để đảm bảo an toàn và linh hoạt cho máy trạm.
    /// </summary>
    public class CameraConfig
    {
        public const string DefaultFileName = "camera_config.json";
        private static readonly byte[] EntropyBytes = Encoding.UTF8.GetBytes("EtekLocationDetecter2026");

        [JsonProperty("ip")]
        public string IP { get; set; } = "192.168.1.64";

        [JsonProperty("port")]
        public ushort Port { get; set; } = 8000;

        [JsonProperty("username")]
        public string Username { get; set; } = "admin";

        [JsonProperty("encryptedPassword")]
        public string EncryptedPassword { get; set; } = string.Empty;

        public static string GetDefaultFilePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDir, DefaultFileName);
        }

        /// <summary>
        /// Mã hóa mật khẩu plain text bằng Windows DPAPI và lưu vào thuộc tính EncryptedPassword.
        /// </summary>
        public void SetPassword(string plainPassword)
        {
            if (string.IsNullOrEmpty(plainPassword))
            {
                EncryptedPassword = string.Empty;
                return;
            }

            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainPassword);
                byte[] cipherBytes = ProtectedData.Protect(plainBytes, EntropyBytes, DataProtectionScope.LocalMachine);
                EncryptedPassword = Convert.ToBase64String(cipherBytes);
            }
            catch
            {
                EncryptedPassword = string.Empty;
            }
        }

        /// <summary>
        /// Giải mã mật khẩu từ EncryptedPassword.
        /// Nếu DPAPI thất bại (do khác máy, khác scope hoặc dữ liệu bị lỗi), tự động fallback về mật khẩu mặc định Etek@2026 an toàn.
        /// </summary>
        public string GetDecryptedPassword()
        {
            if (string.IsNullOrWhiteSpace(EncryptedPassword))
            {
                return "Etek@2026";
            }

            try
            {
                byte[] cipherBytes = Convert.FromBase64String(EncryptedPassword);
                byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, EntropyBytes, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                // Fallback an toàn về mật khẩu camera mặc định khi DPAPI không giải mã được
                return "Etek@2026";
            }
        }

        /// <summary>
        /// Nạp cấu hình từ đĩa hoặc tạo cấu hình mặc định nếu file chưa tồn tại.
        /// Nếu file JSON bị hỏng, tạo bản sao lưu backup và khởi tạo cấu hình mới thay vì ghi đè mất dữ liệu.
        /// </summary>
        public static CameraConfig LoadOrCreateDefault(string filePath = null)
        {
            string targetPath = string.IsNullOrWhiteSpace(filePath) ? GetDefaultFilePath() : filePath;

            if (File.Exists(targetPath))
            {
                try
                {
                    string json = File.ReadAllText(targetPath, Encoding.UTF8);
                    var config = JsonConvert.DeserializeObject<CameraConfig>(json);
                    if (config != null && !string.IsNullOrWhiteSpace(config.IP))
                    {
                        return config;
                    }
                }
                catch (Exception)
                {
                    // File bị hỏng: Đổi tên backup file hỏng để bảo toàn dữ liệu gốc
                    try
                    {
                        string backupPath = targetPath + ".corrupt_bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        File.Move(targetPath, backupPath);
                    }
                    catch { }
                }
            }

            // Tạo cấu hình mặc định
            var defaultConfig = new CameraConfig
            {
                IP = "192.168.1.64",
                Port = 8000,
                Username = "admin"
            };
            defaultConfig.SetPassword("Etek@2026");
            defaultConfig.SaveToFile(targetPath);
            return defaultConfig;
        }

        /// <summary>
        /// Lưu cấu hình hiện tại xuống đĩa dưới dạng JSON định dạng đẹp.
        /// </summary>
        public void SaveToFile(string filePath = null)
        {
            string targetPath = string.IsNullOrWhiteSpace(filePath) ? GetDefaultFilePath() : filePath;
            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(targetPath, json, new UTF8Encoding(true));
        }
    }
}
