using System;
using System.IO;
using FolderMorpher.Models;

namespace FolderMorpher.Services
{
    /// <summary>
    /// Safe atomic file replacement utility for local and network (SMB/UNC) volumes.
    /// Provides atomic File.Replace with fallback to safe .unc_bak rotation,
    /// combined with pre-commit double-check optimistic locking (TOCTOU defense).
    /// </summary>
    public static class SafeFileReplace
    {
        /// <summary>
        /// Safely and atomically replaces the destination file with the contents of the temp file.
        /// </summary>
        /// <param name="tempPath">Verified new content written to a temporary file</param>
        /// <param name="destinationPath">Target existing file to replace</param>
        /// <param name="expectedStamp">Optional stamp to double-check immediately before commit</param>
        /// <param name="toleranceSeconds">Timestamp tolerance in seconds (default 2.0s for SMB/FAT)</param>
        /// <exception cref="InvalidOperationException">Thrown when file was mutated externally before commit</exception>
        public static void Replace(
            string tempPath,
            string destinationPath,
            FileVersionStamp expectedStamp = default,
            double toleranceSeconds = 2.0)
        {
            if (!File.Exists(tempPath))
            {
                throw new FileNotFoundException($"Replacement source temp file does not exist: {tempPath}");
            }

            try
            {
                // 1. Commit boundary optimistic lock (Double-Check TOCTOU defense)
                if (!expectedStamp.IsEmpty)
                {
                    var preCommitFi = new FileInfo(destinationPath);
                    if (!expectedStamp.Matches(preCommitFi, toleranceSeconds))
                    {
                        try { File.Delete(tempPath); } catch { }
                        throw new InvalidOperationException(
                            $"File was modified externally immediately before commit. Overwrite aborted for data safety: {destinationPath}");
                    }
                }

                // 2. Atomic File.Replace (same volume / local filesystem)
                bool replaced = false;
                string localBakPath = destinationPath + ".tmp_rep_" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Replace(tempPath, destinationPath, localBakPath);
                    replaced = true;
                    try { File.Delete(localBakPath); } catch { }
                }
                catch
                {
                    // Fallback for SMB/UNC or non-supported filesystems
                    replaced = false;
                }

                // 3. UNC / Non-supported filesystem safe rotation fallback
                if (!replaced)
                {
                    string uncBakPath = destinationPath + ".unc_bak_" + Guid.NewGuid().ToString("N");
                    bool movedToBak = false;
                    try
                    {
                        File.Move(destinationPath, uncBakPath);
                        movedToBak = true;
                        File.Move(tempPath, destinationPath);
                        // Successfully placed new file: safely delete backup
                        try { File.Delete(uncBakPath); } catch { }
                    }
                    catch
                    {
                        // On failure: immediately restore original file
                        if (movedToBak && File.Exists(uncBakPath) && !File.Exists(destinationPath))
                        {
                            try { File.Move(uncBakPath, destinationPath); } catch { }
                        }
                        throw;
                    }
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }
    }
}