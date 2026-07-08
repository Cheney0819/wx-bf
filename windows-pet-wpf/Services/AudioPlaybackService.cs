using System.Media;

namespace DesktopPet.Wpf.Services;

internal sealed class AudioPlaybackService : IDisposable
{
    private readonly object _sync = new();
    private SoundPlayer? _currentPlayer;

    public Task PlayAndDeleteAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath))
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (_sync)
                {
                    _currentPlayer?.Stop();
                    _currentPlayer?.Dispose();
                    _currentPlayer = new SoundPlayer(audioFilePath);
                }

                _currentPlayer.Load();
                _currentPlayer.PlaySync();
            }
            catch
            {
            }
            finally
            {
                lock (_sync)
                {
                    _currentPlayer?.Dispose();
                    _currentPlayer = null;
                }

                TryDelete(audioFilePath);
            }
        }, cancellationToken);
    }

    public void Stop()
    {
        lock (_sync)
        {
            _currentPlayer?.Stop();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _currentPlayer?.Stop();
            _currentPlayer?.Dispose();
            _currentPlayer = null;
        }

        GC.SuppressFinalize(this);
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
