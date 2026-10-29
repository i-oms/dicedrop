using System.Windows.Media.Imaging;

namespace Ludo.Presentation.Wpf.Views.Board;

/// <summary>The spell artwork from the sprite sheets: fire frames that cycle as they burn, the vine growth frames, the whirlpool frames and the light frames.</summary>
internal static class SpellSprites
{
    public static BitmapSource[] Flames { get; } = Load("fire_flame", 5);

    public static BitmapSource[] Fireballs { get; } = Load("fire_swirl", 4);

    public static BitmapSource[] Vines { get; } = Load("vine", 8);

    public static BitmapSource[] Whirlpools { get; } = Load("water", 16);

    public static BitmapSource[] LightCharge { get; } = Load("light_charge", 8);

    public static BitmapSource[] LightSun { get; } = Load("light_sun", 2);

    public static BitmapSource[] LightBurst { get; } = Load("light_burst", 4);

    public static BitmapSource[] LightAsh { get; } = Load("light_ash", 1);

    private static BitmapSource[] Load(string name, int count) =>
        Enumerable.Range(0, count).Select(i => Load($"{name}_{i}")).ToArray();

    private static BitmapSource Load(string name)
    {
        var image = new BitmapImage(new Uri($"pack://application:,,,/DiceDrop;component/Assets/Spells/{name}.png"));
        image.Freeze();
        return image;
    }
}
