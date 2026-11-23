using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using PalletDetection.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CvRect = OpenCvSharp.Rect;
using WpfRect = System.Windows.Rect;

namespace PalletDetection.Vision
{
    public class YoloV8Detector : IDisposable
    {
        private InferenceSession _session;
        private string _inputName;
        private int _inputWidth = 640;
        private int _inputHeight = 640;

        public bool IsLoaded => _session != null;
        public string ModelPath { get; private set; }
        public Dictionary<int, string> ClassNames { get; private set; } = new Dictionary<int, string>();

        public float ConfidenceThreshold { get; set; } = 0.40f;
        public float IouThreshold { get; set; } = 0.45f;

        public YoloV8Detector()
        {
        }

        public bool LoadModel(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                return false;
            }

            try
            {
                _session?.Dispose();
                _session = null;

                SessionOptions options = new SessionOptions();
                _session = new InferenceSession(modelPath, options);
                ModelPath = modelPath;

                // Input node information
                var inputMeta = _session.InputMetadata;
                _inputName = inputMeta.Keys.First();
                var inputDimensions = inputMeta[_inputName].Dimensions;
                if (inputDimensions.Length == 4)
                {
                    if (inputDimensions[2] > 0) _inputHeight = inputDimensions[2];
                    if (inputDimensions[3] > 0) _inputWidth = inputDimensions[3];
                }

                // Parse class names from metadata if available
                ClassNames.Clear();
                ExtractClassNames();

                return true;
            }
            catch (Exception)
            {
                _session = null;
                return false;
            }
        }

        private void ExtractClassNames()
        {
            try
            {
                var meta = _session.ModelMetadata.CustomMetadataMap;
                if (meta != null && meta.TryGetValue("names", out string namesStr) && !string.IsNullOrWhiteSpace(namesStr))
                {
                    // Pattern: 0: 'Ballet', 1: 'Box', ...
                    var matches = Regex.Matches(namesStr, @"(\d+)\s*:\s*['""]?([^'"",}]+)['""]?");
                    foreach (Match m in matches)
                    {
                        if (int.TryParse(m.Groups[1].Value, out int id))
                        {
                            ClassNames[id] = m.Groups[2].Value.Trim();
                        }
                    }
                }
            }
            catch
            {
                // Fallback to default known classes
            }

            // Defaults if metadata didn't have names
            if (ClassNames.Count == 0)
            {
                ClassNames[0] = "Ballet";
                ClassNames[1] = "Box";
                ClassNames[2] = "Box-and-Ballet";
            }
        }

        public List<DetectionResult> Detect(Mat image, WpfRect? roi = null)
        {
            if (!IsLoaded || image == null || image.Empty())
            {
                return new List<DetectionResult>();
            }

            int origW = image.Width;
            int origH = image.Height;

            // 1. Preprocessing: Letterbox resize to input size (640x640)
            float scale = Math.Min((float)_inputWidth / origW, (float)_inputHeight / origH);
            int newW = (int)Math.Round(origW * scale);
            int newH = (int)Math.Round(origH * scale);
            float padX = (_inputWidth - newW) / 2.0f;
            float padY = (_inputHeight - newH) / 2.0f;

            using (Mat rgb = new Mat())
            using (Mat resized = new Mat())
            using (Mat letterbox = new Mat(new OpenCvSharp.Size(_inputWidth, _inputHeight), MatType.CV_8UC3, new Scalar(114, 114, 114)))
            {
                Cv2.CvtColor(image, rgb, ColorConversionCodes.BGR2RGB);
                Cv2.Resize(rgb, resized, new OpenCvSharp.Size(newW, newH));

                // Paste resized image into center of letterbox canvas
                CvRect roiRect = new CvRect((int)padX, (int)padY, newW, newH);
                using (Mat subCanvas = new Mat(letterbox, roiRect))
                {
                    resized.CopyTo(subCanvas);
                }

                // 2. Build input tensor [1, 3, H, W] normalized to [0, 1]
                var tensor = new DenseTensor<float>(new[] { 1, 3, _inputHeight, _inputWidth });

                // Fast pixel transfer
                int totalPixels = _inputHeight * _inputWidth;
                byte[] rawBytes = new byte[totalPixels * 3];
                System.Runtime.InteropServices.Marshal.Copy(letterbox.Data, rawBytes, 0, rawBytes.Length);

                for (int y = 0; y < _inputHeight; y++)
                {
                    int rowOffset = y * _inputWidth * 3;
                    for (int x = 0; x < _inputWidth; x++)
                    {
                        int pixelOffset = rowOffset + x * 3;
                        tensor[0, 0, y, x] = rawBytes[pixelOffset] / 255.0f;
                        tensor[0, 1, y, x] = rawBytes[pixelOffset + 1] / 255.0f;
                        tensor[0, 2, y, x] = rawBytes[pixelOffset + 2] / 255.0f;
                    }
                }

                // 3. Inference
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, tensor)
                };

                List<DetectionResult> candidates = new List<DetectionResult>();

                using (var results = _session.Run(inputs))
                {
                    var output = results.First().AsTensor<float>();
                    var dims = output.Dimensions; // [1, channels, num_predictions] e.g. [1, 7, 8400]

                    int numChannels = dims[1];
                    int numAnchors = dims[2];
                    int numClasses = numChannels - 4;

                    for (int i = 0; i < numAnchors; i++)
                    {
                        // Find class with highest confidence
                        float maxScore = 0f;
                        int bestClass = -1;

                        for (int c = 0; c < numClasses; c++)
                        {
                            float score = output[0, 4 + c, i];
                            if (score > maxScore)
                            {
                                maxScore = score;
                                bestClass = c;
                            }
                        }

                        if (maxScore < ConfidenceThreshold)
                        {
                            continue;
                        }

                        float cx = output[0, 0, i];
                        float cy = output[0, 1, i];
                        float w = output[0, 2, i];
                        float h = output[0, 3, i];

                        // Convert coordinates back to original image space
                        float x0 = (cx - w / 2.0f - padX) / scale;
                        float y0 = (cy - h / 2.0f - padY) / scale;
                        float boxW = w / scale;
                        float boxH = h / scale;

                        // Clamp to image boundaries
                        x0 = Math.Max(0, Math.Min(x0, origW - 1));
                        y0 = Math.Max(0, Math.Min(y0, origH - 1));
                        boxW = Math.Max(1, Math.Min(boxW, origW - x0));
                        boxH = Math.Max(1, Math.Min(boxH, origH - y0));

                        string className = ClassNames.ContainsKey(bestClass)
                            ? ClassNames[bestClass]
                            : "Class_" + bestClass;

                        candidates.Add(new DetectionResult(
                            bestClass,
                            className,
                            maxScore,
                            new WpfRect(x0, y0, boxW, boxH)));
                    }
                }

                // 4. Non-Maximum Suppression (NMS)
                List<DetectionResult> nmsFiltered = ApplyNms(candidates, IouThreshold);

                // 5. Filter by ROI if specified
                if (roi.HasValue && roi.Value.Width > 0 && roi.Value.Height > 0)
                {
                    WpfRect roiRectVal = roi.Value;
                    nmsFiltered = nmsFiltered
                        .Where(d => roiRectVal.Contains(d.Center))
                        .ToList();
                }

                return nmsFiltered;
            }
        }

        private List<DetectionResult> ApplyNms(List<DetectionResult> items, float iouThreshold)
        {
            var result = new List<DetectionResult>();

            // Group by class to perform class-specific NMS
            var groups = items.GroupBy(x => x.ClassId);

            foreach (var group in groups)
            {
                var sorted = group.OrderByDescending(x => x.Confidence).ToList();

                while (sorted.Count > 0)
                {
                    var current = sorted[0];
                    result.Add(current);
                    sorted.RemoveAt(0);

                    for (int i = sorted.Count - 1; i >= 0; i--)
                    {
                        if (ComputeIou(current.Box, sorted[i].Box) >= iouThreshold)
                        {
                            sorted.RemoveAt(i);
                        }
                    }
                }
            }

            return result;
        }

        private float ComputeIou(WpfRect a, WpfRect b)
        {
            double interX1 = Math.Max(a.Left, b.Left);
            double interY1 = Math.Max(a.Top, b.Top);
            double interX2 = Math.Min(a.Right, b.Right);
            double interY2 = Math.Min(a.Bottom, b.Bottom);

            double interW = Math.Max(0, interX2 - interX1);
            double interH = Math.Max(0, interY2 - interY1);
            double interArea = interW * interH;

            double areaA = a.Width * a.Height;
            double areaB = b.Width * b.Height;
            double unionArea = areaA + areaB - interArea;

            if (unionArea <= 0) return 0f;
            return (float)(interArea / unionArea);
        }

        public Mat AnnotateImage(Mat source, List<DetectionResult> detections, WpfRect? roi = null)
        {
            Mat annotated = source.Clone();

            // Draw ROI if set
            if (roi.HasValue && roi.Value.Width > 0 && roi.Value.Height > 0)
            {
                var r = roi.Value;
                CvRect cvRoi = new CvRect(
                    (int)Math.Max(0, r.X),
                    (int)Math.Max(0, r.Y),
                    (int)Math.Min(r.Width, annotated.Width - r.X),
                    (int)Math.Min(r.Height, annotated.Height - r.Y));

                Cv2.Rectangle(annotated, cvRoi, new Scalar(0, 255, 255), 2); // Yellow ROI
                Cv2.PutText(
                    annotated,
                    "ROI",
                    new OpenCvSharp.Point(cvRoi.X + 5, Math.Max(20, cvRoi.Y - 5)),
                    HersheyFonts.HersheySimplex,
                    0.7,
                    new Scalar(0, 255, 255),
                    2);
            }

            // Color palette for classes (BGR)
            Scalar[] colors = new[]
            {
                new Scalar(0, 255, 0),     // Green for Class 0 (Ballet)
                new Scalar(255, 128, 0),   // Orange for Class 1 (Box)
                new Scalar(255, 0, 255),   // Magenta for Class 2 (Box-and-Ballet)
                new Scalar(0, 215, 255),   // Gold
                new Scalar(255, 255, 0),   // Cyan
            };

            foreach (var det in detections)
            {
                Scalar color = colors[Math.Abs(det.ClassId) % colors.Length];
                CvRect cvBox = new CvRect(
                    (int)det.Box.X,
                    (int)det.Box.Y,
                    (int)det.Box.Width,
                    (int)det.Box.Height);

                // Draw bounding box
                Cv2.Rectangle(annotated, cvBox, color, 2);

                // Draw center point
                Cv2.Circle(
                    annotated,
                    new OpenCvSharp.Point((int)det.Center.X, (int)det.Center.Y),
                    4,
                    color,
                    -1);

                // Draw label with background box
                string label = $"{det.ClassName} {det.Confidence:0.00}";
                var textSize = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, 0.6, 2, out int baseline);

                int textY = Math.Max(textSize.Height + 5, cvBox.Y);
                CvRect textBg = new CvRect(
                    cvBox.X,
                    textY - textSize.Height - 5,
                    textSize.Width + 6,
                    textSize.Height + baseline + 4);

                Cv2.Rectangle(annotated, textBg, color, -1);
                Cv2.PutText(
                    annotated,
                    label,
                    new OpenCvSharp.Point(cvBox.X + 3, textY - 2),
                    HersheyFonts.HersheySimplex,
                    0.6,
                    new Scalar(0, 0, 0), // Black text on colored background
                    2);
            }

            return annotated;
        }

        public void Dispose()
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
