using System.IO;
using System.Media;
using System.Text;
using System.Windows;

namespace LeadjoyBlade.App;

/// <summary>界面音效：双十字热键栏切换、系统菜单呼出。音量按配置 0–100 实时缩放。</summary>
public static class SoundEffects
{
    private static readonly object Gate = new();
    private static SoundPlayer? _hotbar;
    private static SoundPlayer? _menuOpen;
    private static int _hotbarVolume = -1;
    private static int _menuOpenVolume = -1;

    public static void PlayHotbarSwitch(int volume) =>
        Play(ref _hotbar, ref _hotbarVolume, "ffxiv_hotbar.wav", volume);

    public static void PlayMenuOpen(int volume) =>
        Play(ref _menuOpen, ref _menuOpenVolume, "ffxiv_confirm.wav", volume);

    private static void Play(ref SoundPlayer? player, ref int cachedVolume, string file, int volume)
    {
        try
        {
            volume = Math.Clamp(volume, 0, 100);
            if (volume <= 0) return;

            lock (Gate)
            {
                if (player == null || cachedVolume != volume)
                {
                    player?.Dispose();
                    player = Create(file, volume);
                    cachedVolume = volume;
                }
                player.Play();
            }
        }
        catch
        {
            // 音效失败不影响映射
        }
    }

    private static SoundPlayer Create(string file, int volume)
    {
        var uri = new Uri($"pack://application:,,,/LeadjoyBlade;component/Assets/{file}", UriKind.Absolute);
        var resource = Application.GetResourceStream(uri) ?? throw new FileNotFoundException(file);
        using var source = resource.Stream;
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        var wav = buffer.ToArray();
        if (volume < 100) ScaleWav(wav, volume);
        var player = new SoundPlayer(new MemoryStream(wav));
        player.Load();
        return player;
    }

    /// <summary>把 16-bit PCM WAV 的 data 段按百分比缩放。</summary>
    private static void ScaleWav(byte[] wav, int volume)
    {
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToInt32(wav, pos + 4);
            if (size < 0 || pos + 8 + size > wav.Length) return;
            if (id == "data")
            {
                var start = pos + 8;
                var end = start + size;
                for (var i = start; i + 1 < end; i += 2)
                {
                    var sample = BitConverter.ToInt16(wav, i);
                    var scaled = sample * volume / 100;
                    if (scaled > short.MaxValue) scaled = short.MaxValue;
                    if (scaled < short.MinValue) scaled = short.MinValue;
                    wav[i] = (byte)(scaled & 0xFF);
                    wav[i + 1] = (byte)((scaled >> 8) & 0xFF);
                }
                return;
            }
            pos += 8 + size + (size & 1);
        }
    }
}
