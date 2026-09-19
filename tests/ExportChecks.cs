using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media.Imaging;
using LOTRAOM_Viewport;
using TpacTool.Lib;
using TpacTool.IO;

internal static class ExportChecks
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.FirstOrDefault() == "--turntables") { TurntableChecks.Run(args.Skip(1).ToArray()); return; }
        if (args.FirstOrDefault() == "--item-positions") { ItemPositionChecks.Run(args.Skip(1).ToArray()); return; }
        if (args.FirstOrDefault() == "--inspector") { InspectorChecks.Run(); return; }
        if (args.FirstOrDefault() == "--troops") { TroopBrowserChecks.Run(); return; }
        if (args.FirstOrDefault() == "--source-xml") { SourceXmlChecks.Run(); return; }
        if (args.FirstOrDefault() == "--equipment-dropdowns") { EquipmentDropdownChecks.Run(); return; }
        var options = new AssetExportOptions { OutputDirectory = Path.GetTempPath(), Name = "armour", TextureFormat = 2, AllLods = true };
        options.Validate();
        Equal(options, JsonSerializer.Deserialize<AssetExportOptions>(JsonSerializer.Serialize(options))!);
        Reject(() => (options with { OutputDirectory = "relative" }).Validate());
        Reject(() => (options with { Name = "../escape" }).Validate());
        Reject(() => (options with { Name = "CON" }).Validate());
        Reject(() => (options with { Name = "trailing." }).Validate());
        Reject(() => (options with { Fbx = false, Textures = false }).Validate());
        Reject(() => (options with { TextureFormat = 3 }).Validate());
        var tuning = new ItemPositionTuning
        {
            Held = new ItemAttachmentTransform { X = .02f, Y = -.03f, RZ = 35 },
            Holstered = new ItemAttachmentTransform { Z = .04f, RX = -15 }
        };
        Equal(tuning, JsonSerializer.Deserialize<ItemPositionTuning>(JsonSerializer.Serialize(tuning))!);
        var validate = typeof(ItemAttachmentTransform).GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        validate.Invoke(tuning.Held, null);
        foreach (var invalid in new[] { new ItemAttachmentTransform { X = float.NaN }, new ItemAttachmentTransform { RY = float.PositiveInfinity } })
        {
            try { validate.Invoke(invalid, null); throw new Exception("Nonfinite attachment accepted."); }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
        }
        var offsets = new ItemCraftingOffsets { PieceOffset = 2, PreviousPieceOffset = -3, NextPieceOffset = .5f };
        Equal(offsets, JsonSerializer.Deserialize<ItemCraftingOffsets>(JsonSerializer.Serialize(offsets))!);
        var validateOffsets = typeof(ItemCraftingOffsets).GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        validateOffsets.Invoke(offsets, null);
        try { validateOffsets.Invoke(offsets with { NextPieceOffset = float.NaN }, null); throw new Exception("Nonfinite crafting offset accepted."); }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }

        var array = Texture(TextureFormat.DXT1, 8, 8, 2, [32, 8, 8]);
        var dds = Write(array);
        Equal(0x20534444, Int(dds, 0)); Equal(124, Int(dds, 4));
        Equal(32, Int(dds, 20)); Equal(3, Int(dds, 28));
        Equal(0x30315844, Int(dds, 84)); Equal(71, Int(dds, 128));
        Equal(0, Int(dds, 112)); Equal(0, Int(dds, 136)); Equal(2, Int(dds, 140));
        Assert((Int(dds, 8) & 0x80000) != 0, "Compressed linear-size flag missing.");
        Payload(array, dds, 148);
        var cube = Texture(TextureFormat.BC5, 4, 4, 6, [16, 16]);
        cube.SystemFlags.Add("is_cubemap");
        dds = Write(cube);
        Equal(83, Int(dds, 128)); Equal(4, Int(dds, 136)); Equal(1, Int(dds, 140));
        Assert((Int(dds, 112) & 0x200) != 0, "Cubemap caps missing."); Payload(cube, dds, 148);
        var bc7 = Texture(TextureFormat.BC7, 4, 4, 1, [16]);
        dds = Write(bc7); Equal(98, Int(dds, 128)); Payload(bc7, dds, 148);
        var bgra = Texture(TextureFormat.B8G8R8A8_UNORM, 2, 2, 1, [16]);
        byte[] pixels = [11, 22, 33, 0, 44, 55, 66, 128, 77, 88, 99, 255, 12, 13, 14, 63];
        bgra.TexturePixels.Data.RawImage[0][0] = bgra.TexturePixels.Data.PrimaryRawImage = pixels;
        dds = Write(bgra); Equal(8, Int(dds, 20)); Equal(0x00FF0000, Int(dds, 92));
        Assert((Int(dds, 8) & 8) != 0, "Uncompressed pitch flag missing."); Payload(bgra, dds, 128);
        var bitmap = (BitmapSource)typeof(MainWindow).GetMethod("CreateTextureBitmap", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [bgra])!;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream(); encoder.Save(png); png.Position = 0;
        var decoded = new PngBitmapDecoder(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var copy = new byte[16]; decoded.CopyPixels(copy, 8, 0);
        Assert(pixels.SequenceEqual(copy), "PNG changed original colour/alpha channels.");
        array.TexturePixels.Data.RawImage[1][1] = [];
        RejectData(() => Write(array));
        cube.ArrayCount = 5;
        RejectData(() => Write(cube));
        var single = Write(bgra, true);
        Payload(bgra, single, 128);
        Console.WriteLine("Export options, item transform serialization/validation, DDS headers/arrays/cubemaps/mips/BC7, truncated data and PNG colour/alpha: passed.");
    }

    private static Texture Texture(TextureFormat format, uint width, uint height, ushort arrays, int[] mipSizes)
    {
        var raw = Enumerable.Range(0, arrays).Select(a => mipSizes.Select((length, m) =>
            Enumerable.Range(0, length).Select(i => (byte)(a * 29 + m * 17 + i)).ToArray()).ToArray()).ToArray();
        return new Texture { Width = width, Height = height, Format = format, ArrayCount = arrays, MipmapCount = (byte)mipSizes.Length, SystemFlags = [],
            TexturePixels = new ExternalLoader<TexturePixelData>(new TexturePixelData { RawImage = raw, PrimaryRawImage = raw[0][0] }) };
    }
    private static byte[] Write(Texture texture, bool ignoreArray = false)
    { using var stream = new MemoryStream(); new DdsExporter { Texture = texture, IgnoreArray = ignoreArray }.Export(stream); return stream.ToArray(); }
    private static int Int(byte[] bytes, int offset) => BitConverter.ToInt32(bytes, offset);
    private static void Payload(Texture texture, byte[] dds, int offset) => Assert(
        texture.TexturePixels.Data.RawImage.SelectMany(a => a).SelectMany(m => m).SequenceEqual(dds.Skip(offset)), "DDS payload changed.");
    private static void Equal<T>(T expected, T actual) => Assert(Equals(expected, actual), $"Expected {expected}, got {actual}.");
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid options were accepted."); }
    private static void RejectData(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid texture data was accepted."); }
}
