using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System.Windows.Media.Imaging;

namespace PalletDetection.Vision
{
    /// <summary>
    /// Chuyển đổi dữ liệu OpenCV sang WPF.
    /// </summary>
    public static class ImageConverter
    {
        /// <summary>
        /// Chuyển Mat -> BitmapSource
        /// </summary>
        public static BitmapSource ToBitmapSource(Mat image)
        {
            if (image == null)
                return null;

            if (image.Empty())
                return null;

            return BitmapSourceConverter.ToBitmapSource(image);
        }
    }
}