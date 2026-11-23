using System.Windows;

namespace PalletDetection.Models
{
    public class DetectionResult
    {
        public int ClassId { get; set; }
        public string ClassName { get; set; }
        public float Confidence { get; set; }
        public Rect Box { get; set; }

        public Point Center => new Point(Box.X + Box.Width / 2.0, Box.Y + Box.Height / 2.0);

        public DetectionResult()
        {
        }

        public DetectionResult(int classId, string className, float confidence, Rect box)
        {
            ClassId = classId;
            ClassName = className;
            Confidence = confidence;
            Box = box;
        }
    }
}
