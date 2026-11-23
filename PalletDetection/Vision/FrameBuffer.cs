using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using OpenCvSharp;

namespace PalletDetection.Vision
{
    /// <summary>
    /// Lưu frame mới nhất của camera.
    /// GUI và AI luôn lấy bản sao (Clone) của frame này.
    /// </summary>
    public class FrameBuffer : IDisposable
    {
        #region Fields

        private readonly object _lockObject = new object();

        private Mat _latestFrame;

        private DateTime _timestamp;

        private int _frameNumber;

        #endregion

        #region Properties

        public bool HasFrame
        {
            get
            {
                lock (_lockObject)
                {
                    return _latestFrame != null && !_latestFrame.Empty();
                }
            }
        }

        public int FrameNumber
        {
            get
            {
                lock (_lockObject)
                {
                    return _frameNumber;
                }
            }
        }

        public DateTime Timestamp
        {
            get
            {
                lock (_lockObject)
                {
                    return _timestamp;
                }
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Camera ghi frame mới nhất.
        /// </summary>
        public void Update(Mat image)
        {
            if (image == null)
                return;

            if (image.Empty())
                return;

            lock (_lockObject)
            {
                if (_latestFrame != null)
                {
                    _latestFrame.Dispose();
                }

                _latestFrame = image.Clone();

                _timestamp = DateTime.Now;

                _frameNumber++;
            }
        }

        /// <summary>
        /// Lấy bản sao của frame mới nhất.
        /// Người gọi phải Dispose() sau khi dùng xong.
        /// </summary>
        public Mat GetLatestFrame()
        {
            lock (_lockObject)
            {
                if (_latestFrame == null)
                    return null;

                return _latestFrame.Clone();
            }
        }

        public void Clear()
        {
            lock (_lockObject)
            {
                if (_latestFrame != null)
                {
                    _latestFrame.Dispose();
                    _latestFrame = null;
                }

                _frameNumber = 0;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Clear();
        }

        #endregion
    }
}