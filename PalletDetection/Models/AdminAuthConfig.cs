using Newtonsoft.Json;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PalletDetection.Models
{
    /// <summary>
    /// Cấu hình mật khẩu quản trị viên (Admin) cho Tab Configuration.
    /// Sử dụng Salt ngẫu nhiên và băm SHA-256 an toàn, không lưu mật khẩu dạng plain text.
    /// Mật khẩu mặc định: Etek@2026
    /// </summary>
    public class AdminAuthConfig
    {
        public const string DefaultFileName = "admin_config.json";

        [JsonProperty("salt")]
        public string Salt { get; set; } = string.Empty;

        [JsonProperty("passwordHash")]
        public string PasswordHash { get; set; } = string.Empty;

        public static string GetDefaultFilePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDir, DefaultFileName);
        }

        /// <summary>
        /// Thiết lập mật khẩu quản trị mới: tạo Salt ngẫu nhiên và băm SHA-256.
        /// </summary>
        public void SetPassword(string newPassword)
        {
            if (newPassword == null) newPassword = string.Empty;

            byte[] saltBytes = new byte[16];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(saltBytes);
            }
            Salt = Convert.ToBase64String(saltBytes);

            PasswordHash = ComputeHash(newPassword, saltBytes);
        }

        /// <summary>
        /// Kiểm tra mật khẩu người dùng nhập vào có khớp với mật khẩu quản trị hay không.
        /// </summary>
        public bool VerifyPassword(string inputPassword)
        {
            if (string.IsNullOrEmpty(Salt) || string.IsNullOrEmpty(PasswordHash))
            {
                // Chưa có cấu hình băm -> So sánh mặc định Etek@2026
                return inputPassword == "Etek@2026";
            }

            try
            {
                byte[] saltBytes = Convert.FromBase64String(Salt);
                string computedHash = ComputeHash(inputPassword ?? string.Empty, saltBytes);
                return string.Equals(PasswordHash, computedHash, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ComputeHash(string password, byte[] saltBytes)
        {
            byte[] pwdBytes = Encoding.UTF8.GetBytes(password);
            byte[] combinedBytes = new byte[saltBytes.Length + pwdBytes.Length];
            Buffer.BlockCopy(saltBytes, 0, combinedBytes, 0, saltBytes.Length);
            Buffer.BlockCopy(pwdBytes, 0, combinedBytes, saltBytes.Length, pwdBytes.Length);

            using (var sha = new SHA256Managed())
            {
                byte[] hashBytes = sha.ComputeHash(combinedBytes);
                return Convert.ToBase64String(hashBytes);
            }
        }

        /// <summary>
        /// Nạp cấu hình Admin từ đĩa hoặc khởi tạo mật khẩu mặc định Etek@2026.
        /// </summary>
        public static AdminAuthConfig LoadOrCreateDefault(string filePath = null)
        {
            string targetPath = string.IsNullOrWhiteSpace(filePath) ? GetDefaultFilePath() : filePath;

            if (File.Exists(targetPath))
            {
                try
                {
                    string json = File.ReadAllText(targetPath, Encoding.UTF8);
                    var config = JsonConvert.DeserializeObject<AdminAuthConfig>(json);
                    if (config != null && !string.IsNullOrEmpty(config.Salt) && !string.IsNullOrEmpty(config.PasswordHash))
                    {
                        return config;
                    }
                }
                catch (Exception)
                {
                    try
                    {
                        string backupPath = targetPath + ".corrupt_bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        File.Move(targetPath, backupPath);
                    }
                    catch { }
                }
            }

            var defaultConfig = new AdminAuthConfig();
            defaultConfig.SetPassword("Etek@2026");
            defaultConfig.SaveToFile(targetPath);
            return defaultConfig;
        }

        /// <summary>
        /// Lưu cấu hình Admin xuống đĩa dưới dạng JSON.
        /// </summary>
        public void SaveToFile(string filePath = null)
        {
            string targetPath = string.IsNullOrWhiteSpace(filePath) ? GetDefaultFilePath() : filePath;
            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(targetPath, json, new UTF8Encoding(true));
        }
    }
}
