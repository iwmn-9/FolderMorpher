using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FolderMorpher.Services
{
    /// <summary>
    /// ファイルサーバー保護型 適応並列度コントローラー (Adaptive Concurrency Controller - ADR 80)
    /// 
    /// 【設計思想】
    /// 「並列度を上げるロジック」ではなく「手遅れになる前に崖から落ちるように逃げるロジック」が本体。
    /// SMB/RPC は一度過負荷（キュー堆積・ディスクI/O飽和）になると、クライアントが検知した時には既に大渋滞を形成している。
    /// 
    /// 【制御則】
    /// - 下限 (Min): 2 (既存の安全正本を死守)
    /// - 初期値 (Default): 2
    /// - 上限 (Max): 4 (まずは4でキャップ)
    /// - ベースライン測定: 最初のサンプル（約50〜60件）で正常時の p50/p95 latency とスループットを計測。
    /// - 加算昇格 (Additive Increase): 安定・低ジッター (p95 <= baseline * 1.3) かつエラーゼロが一定期間継続した場合のみ +1。
    /// - 限界効用 (Marginal Gain) 監視: 並列度を上げた後の評価期間で Throughput が伸びない場合、即座に元に戻して上限クランプ。
    /// - 即時崖落ち降下 (Immediate Cliff Decrease): 
    ///     * Win32 ネットワークエラー / タイムアウト ➔ 即座に 2 へ崖落ち、30秒クールダウン、セッション上限を 2 にクランプ。
    ///     * p95 > baseline * 1.8 ➔ 即座に 2 へ急降下、クールダウン、セッション上限クランプ。
    ///     * 異常遅延 (latency > baseline_p95 * 3.5) ➔ 即座に 2 へ急降下。
    /// - 不可逆天井クランプ (One-Way Ceiling Clamp): 一度過負荷を検知してバックオフしたセッション中はその上限に二度と挑戦しない（脈打ち・チャタリング防止）。
    /// </summary>
    public sealed class AdaptiveConcurrencyController
    {
        public const int MinConcurrency = 2;
        public const int DefaultConcurrency = 2;
        public const int MaxConcurrency = 4;

        private int _currentConcurrency = DefaultConcurrency;
        private int _sessionMaxCeiling = MaxConcurrency;
        private int _activeSlots = 0;

        private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
        private readonly object _stateLock = new();

        // スライディングウィンドウ（直近100サンプル）
        private readonly double[] _samples = new double[100];
        private readonly long[] _sampleTimestamps = new long[100]; // Stopwatch ticks
        private int _sampleHead = 0;
        private int _sampleCount = 0;
        private long _totalSamplesReported = 0;

        // 🚨 超短期 Emergency Window (直近8サンプル): 高速LANでの急激な遅延悪化を数件で早期検知・即時崖落ち
        private readonly double[] _emergencyWindow = new double[8];
        private int _emergencyHead = 0;
        private int _emergencyCount = 0;

        // ベースライン
        private bool _baselineEstablished = false;
        private double _baselineP50 = 0;
        private double _baselineP95 = 0;
        private double _baselineThroughput = 0; // samples per second
        private const int BaselineSampleThreshold = 50; // 最初の50回でベースライン確定

        // クールダウン & 評価
        private long _cooldownUntilTicks = 0;
        private long _lastConcurrencyChangeSample = 0;
        private double _throughputBeforeIncrease = 0;
        private bool _evaluatingMarginalGain = false;
        private long _marginalGainEvalStartSample = 0;

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public int CurrentConcurrency => Volatile.Read(ref _currentConcurrency);
        public int SessionMaxCeiling => Volatile.Read(ref _sessionMaxCeiling);
        public bool BaselineEstablished => _baselineEstablished;
        public double BaselineP95 => _baselineP95;
        public long TotalSamplesReported => Volatile.Read(ref _totalSamplesReported);

        /// <summary>
        /// 実行スロットを非同期で獲得します。
        /// </summary>
        public async ValueTask<SlotLease> AcquireAsync(CancellationToken ct = default)
        {
            while (!ct.IsCancellationRequested)
            {
                int limit = Volatile.Read(ref _currentConcurrency);
                int current = Volatile.Read(ref _activeSlots);

                if (current < limit)
                {
                    if (Interlocked.CompareExchange(ref _activeSlots, current + 1, current) == current)
                    {
                        return new SlotLease(this);
                    }
                    continue; // CAS 競合時は再試行
                }

                // スロット解放シグナル待ち（短時間タイムアウトでポーリングしつつキャンセル対応）
                try
                {
                    await _signal.WaitAsync(10, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            ct.ThrowIfCancellationRequested();
            return new SlotLease(this);
        }

        /// <summary>
        /// スロットを解放し、レイテンシと成否を報告します。
        /// </summary>
        internal void ReleaseSlot(double elapsedMs, bool isNetworkError)
        {
            Interlocked.Decrement(ref _activeSlots);
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // セマフォ上限ガード
            }

            RecordSample(elapsedMs, isNetworkError);
        }

        internal void ReleaseSlot(double elapsedMs, EnumerationFailureKind failureKind)
        {
            ReleaseSlot(elapsedMs, failureKind == EnumerationFailureKind.Network);
        }

        /// <summary>
        /// サンプル（所要時間・成否）を記録し、適応制御則を評価します。
        /// </summary>
        public void RecordSample(double elapsedMs, EnumerationFailureKind failureKind)
        {
            RecordSample(elapsedMs, failureKind == EnumerationFailureKind.Network);
        }

        /// <summary>
        /// サンプル（所要時間・成否）を記録し、適応制御則を評価します。
        /// </summary>
        public void RecordSample(double elapsedMs, bool isNetworkError)
        {
            lock (_stateLock)
            {
                long nowTicks = _stopwatch.ElapsedTicks;
                long sampleIndex = ++_totalSamplesReported;

                // 1. ネットワークエラー検知時（即時崖落ち ➔ 2、天井クランプ、クールダウン）
                if (isNetworkError)
                {
                    ApplyCliffDecrease("Network/IO Error detected");
                    return;
                }

                // サンプルを長期リングバッファに格納
                _samples[_sampleHead] = elapsedMs;
                _sampleTimestamps[_sampleHead] = nowTicks;
                _sampleHead = (_sampleHead + 1) % _samples.Length;
                if (_sampleCount < _samples.Length) _sampleCount++;

                // サンプルを短期 Emergency Window に格納
                _emergencyWindow[_emergencyHead] = elapsedMs;
                _emergencyHead = (_emergencyHead + 1) % _emergencyWindow.Length;
                if (_emergencyCount < _emergencyWindow.Length) _emergencyCount++;

                // 2. ベースライン未確定フェーズ
                if (!_baselineEstablished)
                {
                    if (_sampleCount >= BaselineSampleThreshold)
                    {
                        EstablishBaseline();
                    }
                    return;
                }

                // 3. 🚨 超短期 Emergency Window 判定 (直近8件中3件以上が 2x baseline かつ baseline + 15ms を超過)
                // 高速LAN環境 (10ms) でサーバーが苦しくなり 45ms〜55ms が数回続いた場合、30件を待たずに数件で即座に逃げる
                if (_emergencyCount >= 4)
                {
                    int spikeCount = 0;
                    double spikeThreshold = Math.Max(_baselineP95 + 15.0, _baselineP95 * 2.0);
                    for (int i = 0; i < _emergencyCount; i++)
                    {
                        if (_emergencyWindow[i] > spikeThreshold)
                        {
                            spikeCount++;
                        }
                    }
                    if (spikeCount >= 3)
                    {
                        ApplyCliffDecrease($"Emergency Window ({spikeCount}/8 spikes > 2x baseline)");
                        return;
                    }
                }

                // 4. 単一サンプル異常スパイク検知 (単一サンプルが baseline_p95 * 3.0 超かつ > 60ms)
                if (elapsedMs > Math.Max(60.0, _baselineP95 * 3.0))
                {
                    ApplyCliffDecrease($"Latency spike ({elapsedMs:F1}ms > 3x baseline)");
                    return;
                }

                // 5. 定期評価（直近15サンプルごと）
                if (sampleIndex % 15 == 0)
                {
                    EvaluateConcurrency(nowTicks, sampleIndex);
                }
            }
        }

        private void EstablishBaseline()
        {
            var sorted = new double[_sampleCount];
            Array.Copy(_samples, sorted, _sampleCount);
            Array.Sort(sorted);

            _baselineP50 = sorted[(int)(_sampleCount * 0.50)];
            _baselineP95 = sorted[(int)(_sampleCount * 0.95)];

            long oldestTime = _sampleTimestamps[(_sampleHead - _sampleCount + _samples.Length) % _samples.Length];
            long latestTime = _sampleTimestamps[(_sampleHead - 1 + _samples.Length) % _samples.Length];
            double totalSec = (double)(latestTime - oldestTime) / Stopwatch.Frequency;
            _baselineThroughput = totalSec > 0.05 ? _sampleCount / totalSec : 10.0;

            _baselineEstablished = true;
            _lastConcurrencyChangeSample = _totalSamplesReported;
        }

        private void EvaluateConcurrency(long nowTicks, long sampleIndex)
        {
            if (_sampleCount < 15) return;

            // 直近サンプルの p95 を計算
            var recent = new double[_sampleCount];
            Array.Copy(_samples, recent, _sampleCount);
            Array.Sort(recent);
            double currentP95 = recent[(int)(_sampleCount * 0.95)];

            // スループット計算
            long oldestTime = _sampleTimestamps[(_sampleHead - _sampleCount + _samples.Length) % _samples.Length];
            long latestTime = _sampleTimestamps[(_sampleHead - 1 + _samples.Length) % _samples.Length];
            double elapsedSec = (double)(latestTime - oldestTime) / Stopwatch.Frequency;
            double currentThroughput = elapsedSec > 0.05 ? _sampleCount / elapsedSec : 10.0;

            // A. 過負荷検知 (p95 > baseline * 1.8) ➔ 即座に崖落ち
            if (currentP95 > Math.Max(60.0, _baselineP95 * 1.8))
            {
                ApplyCliffDecrease($"p95 degraded ({currentP95:F1}ms vs baseline {_baselineP95:F1}ms)");
                return;
            }

            // B. Marginal Gain 評価中（昇格後の効用確認）
            if (_evaluatingMarginalGain)
            {
                if (sampleIndex - _marginalGainEvalStartSample >= 30)
                {
                    _evaluatingMarginalGain = false;
                    // 並列度を増やしたのにスループット改善が +5% 未満、または p95 が +40% 以上悪化している場合
                    double throughputGain = (currentThroughput - _throughputBeforeIncrease) / Math.Max(1.0, _throughputBeforeIncrease);
                    if (throughputGain < 0.05 || currentP95 > _baselineP95 * 1.4)
                    {
                        // 効用なし・サーバー負荷増大 ➔ 1段戻して天井クランプ
                        int rolledBack = Math.Max(MinConcurrency, _currentConcurrency - 1);
                        Volatile.Write(ref _currentConcurrency, rolledBack);
                        Volatile.Write(ref _sessionMaxCeiling, rolledBack); // この天井で固定
                        _cooldownUntilTicks = nowTicks + (long)(Stopwatch.Frequency * 30); // 30秒クールダウン
                        _lastConcurrencyChangeSample = sampleIndex;
                        return;
                    }
                }
                return;
            }

            // C. 昇格判定 (Additive Increase: +1)
            // 条件:
            // 1. クールダウン中でない (現在時刻 > cooldown)
            // 2. 現在の並列度 < セッション天井 かつ < 最大並列度 (4)
            // 3. 前回の変更から 40 サンプル以上経過
            // 4. currentP95 <= baselineP95 * 1.3
            if (nowTicks > _cooldownUntilTicks &&
                _currentConcurrency < _sessionMaxCeiling &&
                _currentConcurrency < MaxConcurrency &&
                (sampleIndex - _lastConcurrencyChangeSample) >= 40 &&
                currentP95 <= _baselineP95 * 1.3)
            {
                _throughputBeforeIncrease = currentThroughput;
                int next = _currentConcurrency + 1;
                Volatile.Write(ref _currentConcurrency, next);
                _lastConcurrencyChangeSample = sampleIndex;

                // 昇格後の限界効用評価モードに入る
                _evaluatingMarginalGain = true;
                _marginalGainEvalStartSample = sampleIndex;
            }
        }

        private void ApplyCliffDecrease(string reason)
        {
            // 崖落ち: 下限は厳格に MinConcurrency (2)
            int decreased = MinConcurrency; // 4 ➔ 2, 3 ➔ 2
            Volatile.Write(ref _currentConcurrency, decreased);
            Volatile.Write(ref _sessionMaxCeiling, decreased); // 一度落ちたら二度とその走査中は上げない
            _cooldownUntilTicks = _stopwatch.ElapsedTicks + (long)(Stopwatch.Frequency * 30); // 30秒クールダウン
            _evaluatingMarginalGain = false;
            _lastConcurrencyChangeSample = _totalSamplesReported;
            _emergencyCount = 0; // Emergency Window をリセット
        }

        /// <summary>
        /// Win32エラー文字列または例外メッセージからSMBネットワーク障害・タイムアウトを検知します。
        /// </summary>
        public static bool IsNetworkOrFatalError(string? errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorMessage)) return false;
            return errorMessage.Contains("58", StringComparison.Ordinal) || // ERROR_BAD_NET_RESP
                   errorMessage.Contains("59", StringComparison.Ordinal) || // ERROR_UNEXP_NET_ERR
                   errorMessage.Contains("64", StringComparison.Ordinal) || // ERROR_NETNAME_DELETED
                   errorMessage.Contains("54", StringComparison.Ordinal) || // ERROR_NETWORK_BUSY
                   errorMessage.Contains("56", StringComparison.Ordinal) || // ERROR_TOO_MANY_CMDS
                   errorMessage.Contains("71", StringComparison.Ordinal) || // ERROR_REQ_NOT_ACCEP
                   errorMessage.Contains("121", StringComparison.Ordinal) || // ERROR_SEM_TIMEOUT
                   errorMessage.Contains("ネットワーク", StringComparison.OrdinalIgnoreCase) ||
                   errorMessage.Contains("Network", StringComparison.OrdinalIgnoreCase) ||
                   errorMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                   errorMessage.Contains("タイムアウト", StringComparison.OrdinalIgnoreCase) ||
                   errorMessage.Contains("RPC", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Adaptive Concurrency スロットの実行リース。Dispose で自動的にスロット返却と計測を行います。
    /// </summary>
    public struct SlotLease : IDisposable
    {
        private AdaptiveConcurrencyController? _controller;
        private readonly Stopwatch _sw;
        private bool _isReported;

        internal SlotLease(AdaptiveConcurrencyController controller)
        {
            _controller = controller;
            _sw = Stopwatch.StartNew();
            _isReported = false;
        }

        public void Report(double elapsedMs, bool isError = false)
        {
            if (_isReported || _controller == null) return;
            _isReported = true;
            _controller.ReleaseSlot(elapsedMs, isError);
            _controller = null;
        }

        public void Report(double elapsedMs, EnumerationFailureKind failureKind)
        {
            Report(elapsedMs, failureKind == EnumerationFailureKind.Network);
        }

        public void Dispose()
        {
            if (!_isReported && _controller != null)
            {
                _isReported = true;
                _sw.Stop();
                _controller.ReleaseSlot(_sw.Elapsed.TotalMilliseconds, false);
                _controller = null;
            }
        }
    }
}
