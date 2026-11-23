using Newtonsoft.Json;
using System;
using System.IO;
using System.Windows;

namespace PalletDetection.Models
{
    /// <summary>
    /// C?u hnh vng ROI du?c luu xu?ng dia d? t? d?ng khi ph?c khi m? l?i ?ng d?ng.
    /// T?a d? du?c luu theo h? t?a d? g?c c?a ?nh camera.
    /// </summary>
    public class RoiConfig
    {
        public const string DefaultFileName = "roi_config.json";

        [JsonProperty("hasRoi")]
        public bool HasRoi { get; set; } = false;

        [JsonProperty("x")]
        public double X { get; set; } = 0;

        [JsonProperty("y")]
        public double Y { get; set; } = 0;

        [JsonProperty("width")]
        public double Width { get; set; } = 0;

        [JsonProperty("height")]
        public double Height { get; set; } = 0;

        public Rect? ToRect()
        {
            if (!HasRoi || Width <= 0 || Height <= 0)
                return null;

            return new Rect(X, Y, Width, Height);
        }

        public static RoiConfig FromRect(Rect? rect)
        {
            if (!rect.HasValue || rect.Value.Width <= 0 || rect.Value.Height <= 0)
            {
                return new RoiConfig { HasRoi = false };
            }

            return new RoiConfig
            {
                HasRoi = true,
                X = Math.Round(rect.Value.X, 1),
                Y = Math.Round(rect.Value.Y, 1),
                Width = Math.Round(rect.Value.Width, 1),
                Height = Math.Round(rect.Value.Height, 1)
            };
        }

        public static string GetDefaultFilePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDir, DefaultFileName);
        }

        public static RoiConfig LoadFromFile(string filePath = null)
        {
            string targetPath = string.IsNullOrWhiteSpace(filePath) ? GetDefaultFilePath() : filePath;

            try
            {
                if (File.Exists(targetPath))
                {
                    string json = File.ReadAllText(targetPath);
                    var config = JsonConvert.DeserializeObject<RoiConfig>(json);
                    if (config != null)
                        return config;
                }
            }
            catch
            {
                // B? qua l?i IO d? tr? v? c?u hnh m?c d?nh khng c ROI
            }

            return new RoiConfig { HasRoi = false };
        }

        public void SaveToFile(string filePath = null)
        {
            string targetPath = string.IsNullOrWhiteSpace(filePath) ? GetDefaultFilePath() : filePath;
            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(targetPath, json);
        }
    }
}
