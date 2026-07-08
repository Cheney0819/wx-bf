using System.IO;
using NAudio.Wave;

namespace DesktopPet.Wpf.Services;

internal sealed class AudioCaptureService : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<string>? _stopTcs;
    private string? _currentFilePath;

    public bool IsRecording { get; private set; }

    public void StartRecording(int maxRecordSeconds)
    {
        if (IsRecording)
            throw new InvalidOperationException("当前已经在录音");

        string audioDir = Path.Combine(PetAiPaths.GetPetAiDir(), "audio_cache");
        Directory.CreateDirectory(audioDir);

        _currentFilePath = Path.Combine(audioDir, $"record_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.wav");
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 120
        };
        _writer = new WaveFileWriter(_currentFilePath, _waveIn.WaveFormat);
        _stopTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
        IsRecording = true;

        _ = AutoStopAfterDelayAsync(Math.Max(2, maxRecordSeconds));
    }

    public async Task<string> StopRecordingAsync()
    {
        if (_stopTcs is null)
            throw new InvalidOperationException("当前没有正在进行的录音");

        if (IsRecording && _waveIn is not null)
            _waveIn.StopRecording();

        return await _stopTcs.Task;
    }

    public void Dispose()
    {
        if (IsRecording)
        {
            try
            {
                _waveIn?.StopRecording();
            }
            catch
            {
            }
        }

        CleanupWaveResources();
        GC.SuppressFinalize(this);
    }

    private async Task AutoStopAfterDelayAsync(int maxRecordSeconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(maxRecordSeconds));
        if (!IsRecording)
            return;

        try
        {
            _waveIn?.StopRecording();
        }
        catch
        {
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        _writer?.Flush();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        var stopTcs = _stopTcs;
        string filePath = _currentFilePath ?? "";
        IsRecording = false;
        CleanupWaveResources();

        if (stopTcs is null)
            return;

        if (e.Exception is not null)
        {
            stopTcs.TrySetException(e.Exception);
            return;
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            stopTcs.TrySetException(new InvalidOperationException("没有生成录音文件"));
            return;
        }

        if (new FileInfo(filePath).Length < 2048)
        {
            TryDelete(filePath);
            stopTcs.TrySetException(new InvalidOperationException("录音太短了，刚刚没有听清呢"));
            return;
        }

        stopTcs.TrySetResult(filePath);
    }

    private void CleanupWaveResources()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }

        _writer?.Dispose();
        _writer = null;
        _stopTcs = null;
        _currentFilePath = null;
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch
        {
        }
    }
}
