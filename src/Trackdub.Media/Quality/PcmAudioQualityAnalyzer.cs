using System.Buffers;
using System.Buffers.Binary;
using Trackdub.Contracts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Quality;

public sealed class PcmAudioQualityAnalyzer : IAudioQualityAnalyzer
{
    private const double SilenceFloorDbfs = -90.0d;
    private const double ClippingThreshold = 0.999d;
    private const double NearSilenceThresholdDbfs = -50.0d;
    private const double MinimumQuietRunSeconds = 0.750d;
    private const int RequiredQuietRuns = 3;
    private const double QuietRunRelativeThresholdDb = 12.0d;
    private const int WindowMilliseconds = 100;

    public async Task<AudioQualityAnalysisResult> AnalyzeAsync(
        AudioQualityAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AudioPath);

        string path = Path.GetFullPath(request.AudioPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Audio file was not found for quality analysis.", path);
        }

        WavePcm16Info waveInfo = await WavePcm16.ReadInfoAsync(path, cancellationToken).ConfigureAwait(false);
        AudioQualityMetrics metrics = await AnalyzePcm16Async(
            path,
            waveInfo,
            request.SourceKind,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AudioQualityDefectKind> defects = DetectDefects(metrics, request.Thresholds);

        return new AudioQualityAnalysisResult(
            request.AudioPath,
            metrics,
            request.Thresholds,
            defects,
            BuildWarnings(metrics));
    }

    private static async Task<AudioQualityMetrics> AnalyzePcm16Async(
        string path,
        WavePcm16Info waveInfo,
        SpeechAudioSourceKind sourceKind,
        CancellationToken cancellationToken)
    {
        if (waveInfo.SampleFrames == 0)
        {
            return new AudioQualityMetrics(
                0d,
                SilenceFloorDbfs,
                SilenceFloorDbfs,
                SilenceFloorDbfs,
                Lufs: null,
                AudioQualityAnalysisConfidence.Low,
                sourceKind,
                0d,
                100d,
                0d,
                SilenceFloorDbfs,
                SilenceFloorDbfs,
                SilenceFloorDbfs,
                0d,
                0d,
                NoiseFloorDbfs: null,
                SnrDb: null,
                AudioSnrConfidence.Unavailable);
        }

        int sampleRate = waveInfo.SampleRate;
        double peak = 0d;
        double sumSquares = 0d;
        double sum = 0d;
        long clippedSamples = 0;

        double rumbleEnergy = 0d;
        double hissEnergy = 0d;
        double speechBandEnergy = 0d;
        double totalEnergy = 0d;

        var rumbleLowPass = new OnePoleLowPass(sampleRate, 80d);
        var hissHighPass = new OnePoleHighPass(sampleRate, 8000d);
        var speechHighPass = new OnePoleHighPass(sampleRate, 120d);
        var speechLowPass = new OnePoleLowPass(sampleRate, 6000d);

        int windowSize = Math.Max(1, sampleRate * WindowMilliseconds / 1000);
        var windowSquares = new List<double>();
        double currentWindowSquares = 0d;
        int currentWindowSamples = 0;

        int sampleStride = waveInfo.BlockAlign / waveInfo.ChannelCount;
        int framesPerRead = Math.Max(1, 8192 / Math.Max(1, waveInfo.BlockAlign));
        byte[] buffer = new byte[framesPerRead * waveInfo.BlockAlign];

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = waveInfo.DataStartPosition;

        long frameIndex = 0;
        while (frameIndex < waveInfo.SampleFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int framesRemaining = (int)Math.Min(framesPerRead, waveInfo.SampleFrames - frameIndex);
            int bytesToRead = framesRemaining * waveInfo.BlockAlign;
            int bytesRead = await ReadAtLeastAsync(stream, buffer, bytesToRead, cancellationToken).ConfigureAwait(false);
            if (bytesRead != bytesToRead)
            {
                throw new InvalidOperationException("WAV payload ended before the declared sample data was fully read.");
            }

            ReadOnlySpan<byte> span = buffer.AsSpan(0, bytesRead);
            for (int offset = 0; offset < bytesRead; offset += waveInfo.BlockAlign)
            {
                double monoSample = 0d;
                for (int channel = 0; channel < waveInfo.ChannelCount; channel++)
                {
                    int sampleOffset = offset + (channel * sampleStride);
                    short sample = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(sampleOffset, sizeof(short)));
                    monoSample += sample / 32768d;
                }

                double value = Math.Clamp(monoSample / waveInfo.ChannelCount, -1d, 1d);
                double abs = Math.Abs(value);
                peak = Math.Max(peak, abs);
                sumSquares += value * value;
                sum += value;
                totalEnergy += value * value;

                double rumble = rumbleLowPass.Process(value);
                double hiss = hissHighPass.Process(value);
                double speechBand = speechLowPass.Process(speechHighPass.Process(value));
                rumbleEnergy += rumble * rumble;
                hissEnergy += hiss * hiss;
                speechBandEnergy += speechBand * speechBand;

                if (abs >= ClippingThreshold)
                {
                    clippedSamples++;
                }

                currentWindowSquares += value * value;
                currentWindowSamples++;
                if (currentWindowSamples == windowSize)
                {
                    windowSquares.Add(currentWindowSquares / currentWindowSamples);
                    currentWindowSquares = 0d;
                    currentWindowSamples = 0;
                }

                frameIndex++;
            }
        }

        if (currentWindowSamples > 0)
        {
            windowSquares.Add(currentWindowSquares / currentWindowSamples);
        }

        double sampleCount = waveInfo.SampleFrames;
        double rms = Math.Sqrt(sumSquares / sampleCount);
        double rmsDbfs = ToDbfs(rms);
        double peakDbfs = ToDbfs(peak);
        double dcOffset = sum / sampleCount;
        double clippedPercent = clippedSamples * 100d / sampleCount;
        double durationSeconds = sampleCount / sampleRate;

        double[] windowDb = windowSquares
            .Select(value => ToDbfs(Math.Sqrt(value)))
            .ToArray();
        double nearSilencePercent = windowDb.Length == 0
            ? 100d
            : windowDb.Count(value => value <= NearSilenceThresholdDbfs) * 100d / windowDb.Length;
        double activeRms = ResolveActiveRms(windowSquares, rms);
        (double? noiseFloorDbfs, double? snrDb, AudioSnrConfidence snrConfidence) = ResolveSnr(windowDb, ToDbfs(activeRms));
        double dynamicRangeDb = ResolveDynamicRange(windowDb);

        return new AudioQualityMetrics(
            durationSeconds,
            peakDbfs,
            rmsDbfs,
            ToDbfs(activeRms),
            Lufs: null,
            ResolveAnalysisConfidence(durationSeconds, windowDb.Length),
            sourceKind,
            clippedPercent,
            nearSilencePercent,
            dcOffset,
            ToRatioDb(rumbleEnergy, speechBandEnergy),
            ToRatioDb(hissEnergy, speechBandEnergy),
            ToRatioDb(speechBandEnergy, totalEnergy),
            peakDbfs - rmsDbfs,
            dynamicRangeDb,
            noiseFloorDbfs,
            snrDb,
            snrConfidence);
    }

    private static IReadOnlyList<AudioQualityDefectKind> DetectDefects(
        AudioQualityMetrics metrics,
        AudioQualityAnalysisThresholds thresholds)
    {
        var defects = new List<AudioQualityDefectKind>();

        if (metrics.ActiveRmsDbfs < thresholds.LowVolumeActiveRmsDbfs &&
            metrics.PeakDbfs < thresholds.LowVolumePeakDbfs)
        {
            defects.Add(AudioQualityDefectKind.LowVolume);
        }

        if (metrics.ClippedSamplePercent > thresholds.ClippingPercent)
        {
            defects.Add(AudioQualityDefectKind.Clipping);
        }

        if (metrics.SnrConfidence is AudioSnrConfidence.Reliable &&
            metrics.SnrDb is double snr &&
            snr < thresholds.LowSnrDb)
        {
            defects.Add(AudioQualityDefectKind.LowSnr);
        }

        if (metrics.RumbleRatioDb > thresholds.RumbleRatioDb)
        {
            defects.Add(AudioQualityDefectKind.Rumble);
        }

        if (metrics.HissRatioDb > thresholds.HissRatioDb)
        {
            defects.Add(AudioQualityDefectKind.Hiss);
        }

        if (metrics.SpeechBandRatioDb < thresholds.PoorSpeechBandRatioDb)
        {
            defects.Add(AudioQualityDefectKind.PoorSpeechBand);
        }

        if (metrics.ActiveRmsDbfs < thresholds.NearSilenceActiveRmsDbfs)
        {
            defects.Add(AudioQualityDefectKind.NearSilence);
        }

        return defects;
    }

    private static IReadOnlyList<string> BuildWarnings(AudioQualityMetrics metrics)
    {
        var warnings = new List<string>();
        if (metrics.SnrConfidence is AudioSnrConfidence.Unavailable)
        {
            warnings.Add("SNR unavailable: no reliable quiet floor was found.");
        }

        if (metrics.DurationSeconds < 0.4d)
        {
            warnings.Add("Clip is too short for reliable audio quality analysis.");
        }

        return warnings;
    }

    private static double ResolveActiveRms(IReadOnlyList<double> windowSquares, double fallbackRms)
    {
        if (windowSquares.Count == 0)
        {
            return fallbackRms;
        }

        double[] rented = ArrayPool<double>.Shared.Rent(windowSquares.Count);
        try
        {
            for (int i = 0; i < windowSquares.Count; i++)
            {
                rented[i] = windowSquares[i];
            }

            Array.Sort(rented, 0, windowSquares.Count);
            double threshold = rented[(int)Math.Floor((windowSquares.Count - 1) * 0.60d)];

            double sum = 0d;
            int count = 0;
            for (int i = 0; i < windowSquares.Count; i++)
            {
                double value = windowSquares[i];
                if (value >= threshold && ToDbfs(Math.Sqrt(value)) > -55d)
                {
                    sum += value;
                    count++;
                }
            }

            if (count == 0)
            {
                return fallbackRms;
            }

            return Math.Sqrt(sum / count);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    private static (double? NoiseFloorDbfs, double? SnrDb, AudioSnrConfidence Confidence) ResolveSnr(
        IReadOnlyList<double> windowDb,
        double activeRmsDbfs)
    {
        if (windowDb.Count == 0)
        {
            return (null, null, AudioSnrConfidence.Unavailable);
        }

        double medianDb = Percentile(windowDb, 0.50d);
        double quietThreshold = Math.Min(-45.0d, medianDb - QuietRunRelativeThresholdDb);
        int requiredRunLength = (int)Math.Ceiling(MinimumQuietRunSeconds / (WindowMilliseconds / 1000d));
        var quietWindows = new List<double>();
        int quietRuns = 0;
        int index = 0;
        while (index < windowDb.Count)
        {
            if (windowDb[index] > quietThreshold)
            {
                index++;
                continue;
            }

            int start = index;
            while (index < windowDb.Count && windowDb[index] <= quietThreshold)
            {
                index++;
            }

            int count = index - start;
            if (count >= requiredRunLength)
            {
                quietRuns++;
                for (int quietIndex = start; quietIndex < index; quietIndex++)
                {
                    quietWindows.Add(windowDb[quietIndex]);
                }
            }
        }

        if (quietRuns < RequiredQuietRuns || quietWindows.Count == 0)
        {
            return (null, null, AudioSnrConfidence.Unavailable);
        }

        double noiseFloorDbfs = Percentile(quietWindows, 0.50d);
        return (noiseFloorDbfs, activeRmsDbfs - noiseFloorDbfs, AudioSnrConfidence.Reliable);
    }

    private static double ResolveDynamicRange(IReadOnlyList<double> windowDb)
    {
        double[] rented = ArrayPool<double>.Shared.Rent(windowDb.Count);
        try
        {
            int activeCount = 0;
            for (int i = 0; i < windowDb.Count; i++)
            {
                if (windowDb[i] > -60d)
                {
                    rented[activeCount++] = windowDb[i];
                }
            }

            if (activeCount < 2)
            {
                return 0d;
            }

            Array.Sort(rented, 0, activeCount);
            return PercentileSortedSpan(rented.AsSpan(0, activeCount), 0.90d)
                 - PercentileSortedSpan(rented.AsSpan(0, activeCount), 0.10d);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    private static AudioQualityAnalysisConfidence ResolveAnalysisConfidence(double durationSeconds, int windowCount)
    {
        if (durationSeconds >= 5d && windowCount >= 30)
        {
            return AudioQualityAnalysisConfidence.High;
        }

        return durationSeconds >= 1d && windowCount >= 5
            ? AudioQualityAnalysisConfidence.Medium
            : AudioQualityAnalysisConfidence.Low;
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        if (values is IReadOnlyList<double> list)
        {
            if (list.Count == 0)
            {
                return SilenceFloorDbfs;
            }

            double[] rented = ArrayPool<double>.Shared.Rent(list.Count);
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    rented[i] = list[i];
                }
                Array.Sort(rented, 0, list.Count);
                return PercentileSortedSpan(rented.AsSpan(0, list.Count), percentile);
            }
            finally
            {
                ArrayPool<double>.Shared.Return(rented);
            }
        }

        double[] array = values.ToArray();
        if (array.Length == 0)
        {
            return SilenceFloorDbfs;
        }
        Array.Sort(array);
        return PercentileSortedSpan(array.AsSpan(), percentile);
    }

    private static double PercentileSpan(ReadOnlySpan<double> values, double percentile)
    {
        if (values.Length == 0)
        {
            return SilenceFloorDbfs;
        }

        double[] rented = ArrayPool<double>.Shared.Rent(values.Length);
        try
        {
            values.CopyTo(rented);
            Array.Sort(rented, 0, values.Length);
            return PercentileSortedSpan(rented.AsSpan(0, values.Length), percentile);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    private static double PercentileSortedSpan(ReadOnlySpan<double> sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
        {
            return SilenceFloorDbfs;
        }

        double position = Math.Clamp(percentile, 0d, 1d) * (sortedValues.Length - 1);
        int left = (int)Math.Floor(position);
        int right = (int)Math.Ceiling(position);
        if (left == right)
        {
            return sortedValues[left];
        }

        double fraction = position - left;
        return sortedValues[left] + ((sortedValues[right] - sortedValues[left]) * fraction);
    }

    private static double ToRatioDb(double numerator, double denominator) =>
        10d * Math.Log10(Math.Max(numerator, 1e-12d) / Math.Max(denominator, 1e-12d));

    private static double ToDbfs(double amplitude) =>
        amplitude <= 1e-9d ? SilenceFloorDbfs : 20d * Math.Log10(Math.Min(1d, amplitude));

    private static async Task<int> ReadAtLeastAsync(
        FileStream stream,
        byte[] buffer,
        int bytesToRead,
        CancellationToken cancellationToken)
    {
        int totalBytesRead = 0;
        while (totalBytesRead < bytesToRead)
        {
            int bytesRead = await stream.ReadAsync(
                buffer.AsMemory(totalBytesRead, bytesToRead - totalBytesRead),
                cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
        }

        return totalBytesRead;
    }

    private sealed class OnePoleLowPass
    {
        private readonly double alpha;
        private double previous;

        public OnePoleLowPass(int sampleRate, double cutoffHz)
        {
            double dt = 1d / sampleRate;
            double rc = 1d / (2d * Math.PI * cutoffHz);
            alpha = dt / (rc + dt);
        }

        public double Process(double value)
        {
            previous += alpha * (value - previous);
            return previous;
        }
    }

    private sealed class OnePoleHighPass
    {
        private readonly double alpha;
        private double previousInput;
        private double previousOutput;

        public OnePoleHighPass(int sampleRate, double cutoffHz)
        {
            double dt = 1d / sampleRate;
            double rc = 1d / (2d * Math.PI * cutoffHz);
            alpha = rc / (rc + dt);
        }

        public double Process(double value)
        {
            double output = alpha * (previousOutput + value - previousInput);
            previousInput = value;
            previousOutput = output;
            return output;
        }
    }
}
