using Android.App;
using Android.Graphics;
using Android.Widget;
using RecompOne.Runtime;

namespace CrashBandicoot.AndroidRuntime;

sealed class AndroidPlatformHost(
    Activity activity,
    ImageView screen,
    TextView status,
    ProgressBar progress) : IRuntimePlatformHost
{
    Bitmap? _bitmap;
    int _framePending;

    public void Initialize(string title) => SetStatus($"{title}: primo frame in arrivo…");
    public void WaitForValidDisc() { }
    public void AttachAudio(Spu? spu) { }
    public void SetMasterVolume(float volume) { }
    public void ShowNotice(string message) => SetStatus(message);

    public void Present(Gpu? gpu)
    {
        if (gpu == null || !gpu.DisplayEnabled || Interlocked.Exchange(ref _framePending, 1) != 0)
            return;

        var width = gpu.DisplayWidth;
        var height = gpu.DisplayHeight;
        if (width <= 0 || height <= 0)
        {
            Interlocked.Exchange(ref _framePending, 0);
            return;
        }

        var pixels = new int[width * height];
        ConvertDisplay(gpu, width, height, pixels);
        activity.RunOnUiThread(() =>
        {
            try
            {
                var next = Bitmap.CreateBitmap(pixels, width, height, Bitmap.Config.Argb8888!);
                screen.SetImageBitmap(next);
                var previous = Interlocked.Exchange(ref _bitmap, next);
                previous?.Recycle();
                status.Text = $"Gioco in esecuzione • {width}×{height}";
                progress.Visibility = Android.Views.ViewStates.Gone;
            }
            finally
            {
                Interlocked.Exchange(ref _framePending, 0);
            }
        });
    }

    public void Shutdown()
    {
        RecompOne.Runtime.Hardware.Controller.SetVirtualPadState(0);
        activity.RunOnUiThread(() =>
        {
            screen.SetImageDrawable(null);
            _bitmap?.Recycle();
            _bitmap = null;
            status.Text = "Sessione terminata.";
        });
    }

    void SetStatus(string text) => activity.RunOnUiThread(() => status.Text = text);

    static void ConvertDisplay(Gpu gpu, int width, int height, int[] output)
    {
        var vram = gpu.Vram;
        var dx = gpu.DisplayX;
        var dy = gpu.DisplayY;
        var o = 0;
        if (gpu.Display24Bit)
        {
            for (var y = 0; y < height; y++)
            {
                var lineByte = ((dy + y) * Gpu.VramWidth + dx) * 2;
                for (var x = 0; x < width; x++)
                {
                    var bo = lineByte + x * 3;
                    var r = VramByte(vram, bo);
                    var g = VramByte(vram, bo + 1);
                    var b = VramByte(vram, bo + 2);
                    output[o++] = unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | b));
                }
            }
            return;
        }

        for (var y = 0; y < height; y++)
        {
            var line = ((dy + y) & (Gpu.VramHeight - 1)) * Gpu.VramWidth;
            for (var x = 0; x < width; x++)
            {
                var px = vram[line + ((dx + x) & (Gpu.VramWidth - 1))];
                var r = (px & 0x1F) << 3;
                var g = ((px >> 5) & 0x1F) << 3;
                var b = ((px >> 10) & 0x1F) << 3;
                output[o++] = unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b));
            }
        }
    }

    static byte VramByte(ushort[] vram, int byteOffset)
    {
        var halfword = (byteOffset >> 1) & (Gpu.VramWidth * Gpu.VramHeight - 1);
        var value = vram[halfword];
        return (byte)((byteOffset & 1) == 0 ? value & 0xFF : value >> 8);
    }
}
