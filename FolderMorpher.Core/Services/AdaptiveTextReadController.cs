using System;

namespace FolderMorpher.Services
{
    /// <summary>
    /// Chooses the FileStream read-ahead size from completed non-matching text files
    /// within one search. No persistent history or elevated filesystem access is used.
    /// </summary>
    public sealed class AdaptiveTextReadController
    {
        public const int DefaultBufferSize = 64 * 1024;
        private const long MinAdaptiveFileSize = 8L * 1024 * 1024;
        private const long EvaluationChars = 16L * 1024 * 1024;

        private readonly object _gate = new();
        private readonly int _maximumBufferSize;
        private int _currentBufferSize = DefaultBufferSize;
        private long _stageChars;
        private double _stageReadMs;
        private double _stageP95Ms;
        private double _previousCharsPerMs;
        private double _previousP95Ms;
        private bool _settled;

        public AdaptiveTextReadController(bool isNetworkPath)
        {
            // Keep remote in-flight read-ahead bounded under the existing 12-worker governor.
            _maximumBufferSize = isNetworkPath ? 256 * 1024 : 1024 * 1024;
        }

        public int SelectBufferSize(long knownSize)
        {
            if (knownSize < MinAdaptiveFileSize) return DefaultBufferSize;
            lock (_gate) return _currentBufferSize;
        }

        public void ReportCompletedMiss(int usedBufferSize, long knownSize, long charsRead, double readMs, double readP95Ms)
        {
            if (knownSize < MinAdaptiveFileSize || charsRead <= 0 || readMs <= 0) return;
            lock (_gate)
            {
                if (_settled || usedBufferSize != _currentBufferSize) return;

                _stageChars += charsRead;
                _stageReadMs += readMs;
                _stageP95Ms = Math.Max(_stageP95Ms, readP95Ms);
                if (_stageChars < EvaluationChars) return;

                double charsPerMs = _stageChars / _stageReadMs;
                if (_currentBufferSize == DefaultBufferSize)
                {
                    _previousCharsPerMs = charsPerMs;
                    _previousP95Ms = _stageP95Ms;
                    _currentBufferSize = Math.Min(256 * 1024, _maximumBufferSize);
                    ResetStage();
                    return;
                }

                bool latencyHealthy = _stageP95Ms <= Math.Max(_previousP95Ms * 4, _previousP95Ms + 20);
                bool faster = charsPerMs >= _previousCharsPerMs * 1.05 && latencyHealthy;
                if (faster && _currentBufferSize < _maximumBufferSize)
                {
                    _previousCharsPerMs = charsPerMs;
                    _previousP95Ms = _stageP95Ms;
                    _currentBufferSize = Math.Min(_currentBufferSize * 2, _maximumBufferSize);
                    ResetStage();
                }
                else
                {
                    if (charsPerMs < _previousCharsPerMs * 0.90 || !latencyHealthy)
                    {
                        _currentBufferSize = _currentBufferSize == 256 * 1024
                            ? DefaultBufferSize
                            : _currentBufferSize / 2;
                    }
                    _settled = true;
                }
            }
        }

        private void ResetStage()
        {
            _stageChars = 0;
            _stageReadMs = 0;
            _stageP95Ms = 0;
        }
    }
}
