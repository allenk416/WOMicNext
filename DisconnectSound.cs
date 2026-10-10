using System.Media;

namespace WOMicNext;

internal sealed class DisconnectSound : IDisposable
{
    private readonly Stream stream;
    private readonly SoundPlayer player;

    public DisconnectSound()
    {
        stream = typeof(DisconnectSound).Assembly.GetManifestResourceStream("WOMicNext.Assets.disconnect.wav")
            ?? throw new InvalidOperationException("断线音效资源缺失。");
        player = new SoundPlayer(stream);
        player.Load();
    }

    // SoundPlayer.Play queues playback; neither a disconnect nor a preview blocks the UI.
    public void Play() => player.Play();

    public void Dispose()
    {
        player.Stop();
        player.Dispose();
        stream.Dispose();
    }
}
