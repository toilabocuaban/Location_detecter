using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PalletDetection.Vision
{
    /// <summary>
    /// Đại diện cho một Frame được lấy từ Camera.
    /// Đây là Model dùng chung cho toàn bộ hệ thống.
    /// </summary>
    public class CameraFrame : IDisposable
    {
        /// <summary>
        /// Ảnh OpenCV
        /// </summary>
        public Mat Image { get; set; }

        /// <summary>
        /// Thời gian lấy Frame
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Số thứ tự Frame
        /// </summary>
        public int FrameNumber { get; set; }

        /// <summary>
        /// FPS hiện tại
        /// </summary>
        public double FPS { get; set; }

        /// <summary>
        /// Tên Camera
        /// </summary>
        public string CameraName { get; set; }

        /// <summary>
        /// Chiều rộng ảnh
        /// </summary>
        public int Width
        {
            get
            {
                if (Image == null)
                    return 0;

                return Image.Width;
            }
        }

        /// <summary>
        /// Chiều cao ảnh
        /// </summary>
        public int Height
        {
            get
            {
                if (Image == null)
                    return 0;

                return Image.Height;
            }
        }

        /// <summary>
        /// Giải phóng bộ nhớ OpenCV
        /// </summary>
        public void Dispose()
        {
            if (Image != null)
            {
                Image.Dispose();
                Image = null;
            }
        }
    }
}
