using System;
using System.IO;

namespace FolderMorpher.Models
{
    /// <summary>
    /// Immutable file version stamp for optimistic concurrency control and TOCTOU protection.
    /// Captures file length and UTC last write time to verify against external mutations.
    /// </summary>
    public readonly record struct FileVersionStamp(long Length, DateTime LastWriteTimeUtc)
    {
        public static readonly FileVersionStamp Empty = new(0, default);

        public bool IsEmpty => Length == 0 && LastWriteTimeUtc == default;

        /// <summary>
        /// Captures the current file version stamp from the specified file path. Returns Empty if the file does not exist.
        /// </summary>
        public static FileVersionStamp Capture(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return Empty;
            try
            {
                var fi = new FileInfo(filePath);
                return fi.Exists ? new FileVersionStamp(fi.Length, fi.LastWriteTimeUtc) : Empty;
            }
            catch
            {
                return Empty;
            }
        }

        /// <summary>
        /// Creates a stamp from an existing FileInfo.
        /// </summary>
        public static FileVersionStamp FromFileInfo(FileInfo fi)
        {
            if (fi == null || !fi.Exists) return Empty;
            return new FileVersionStamp(fi.Length, fi.LastWriteTimeUtc);
        }

        /// <summary>
        /// Verifies whether the specified FileInfo matches this stamp within an acceptable time tolerance.
        /// </summary>
        public bool Matches(FileInfo? currentFi, double toleranceSeconds = 2.0)
        {
            if (currentFi == null || !currentFi.Exists) return false;
            if (Length > 0 && currentFi.Length != Length) return false;
            if (LastWriteTimeUtc != default)
            {
                var diff = Math.Abs((currentFi.LastWriteTimeUtc - LastWriteTimeUtc).TotalSeconds);
                if (diff > toleranceSeconds) return false;
            }
            return true;
        }

        /// <summary>
        /// Verifies whether the specified file path matches this stamp within an acceptable time tolerance.
        /// </summary>
        public bool Matches(string filePath, double toleranceSeconds = 2.0)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            try
            {
                var fi = new FileInfo(filePath);
                return Matches(fi, toleranceSeconds);
            }
            catch
            {
                return false;
            }
        }
    }
}