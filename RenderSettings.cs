using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace LOTRAOM_Viewport;

public partial class MainWindow
{
    private static readonly JsonSerializerOptions RenderSettingsJsonOptions = new()
    {
        WriteIndented = true, Converters = { new RenderColourJsonConverter() }
    };
    private string _renderOutputDirectory = "";
    private RenderExportSettings? _lastRenderSettings;

    private void RenderExportPopup_Closed(object? sender, EventArgs e) => SaveSettingsIfEnabled();

    private RenderExportSettings? ReadRenderSettings()
    {
        if (!int.TryParse(RenderWidthBox.Text, out var width) || !int.TryParse(RenderHeightBox.Text, out var height) ||
            width < 64 || height < 64 || width > 8192 || height > 8192 || (long)width * height > 33554432)
            return _lastRenderSettings;
        return _lastRenderSettings = new()
        {
            SizeMode = RenderSizePreset.SelectedIndex, Width = width, Height = height,
            Transparent = RenderBackgroundCombo.SelectedIndex == 0, IncludeGrid = RenderGridCheckBox.IsChecked == true,
            OutputDirectory = _renderOutputDirectory
        };
    }

    private void RestoreRenderSettings(AppSettings settings)
    {
        if (settings.Render is { Width: >= 64 and <= 8192, Height: >= 64 and <= 8192, SizeMode: >= 0 and <= 4 } render &&
            (long)render.Width * render.Height <= 33554432)
        {
            _isUpdatingRenderSize = true;
            try
            {
                _lastRenderSettings = render;
                _renderOutputDirectory = render.OutputDirectory;
                RenderSizePreset.SelectedIndex = render.SizeMode;
                RenderWidthBox.Text = render.Width.ToString(CultureInfo.InvariantCulture);
                RenderHeightBox.Text = render.Height.ToString(CultureInfo.InvariantCulture);
                RenderBackgroundCombo.SelectedIndex = render.Transparent ? 0 : 1;
                RenderGridCheckBox.IsChecked = render.IncludeGrid;
            }
            finally { _isUpdatingRenderSize = false; }
        }
        if (settings.BatchRender is { } batch)
        {
            try { batch.Validate(); _batchRenderOptions = batch; }
            catch (ArgumentException) { }
        }
        if (settings.Turntable is { } turntable)
        {
            try { turntable.Validate(); _turntableOptions = turntable; }
            catch (ArgumentException) { }
        }
        if (settings.BatchTurntable is { } batchTurntable)
        {
            try { batchTurntable.Validate(); _batchTurntableOptions = batchTurntable; }
            catch (ArgumentException) { }
        }
        if (settings.AssetExport is { } assetExport)
        {
            try { assetExport.Validate(); _assetExportOptions = assetExport; }
            catch (ArgumentException) { }
        }
        if (settings.ItemPositions is { } positions)
            foreach (var (id, tuning) in positions)
            {
                if (string.IsNullOrWhiteSpace(id) || tuning == null) continue;
                try
                {
                    tuning.Validate();
                    _itemPositions[id] = tuning;
                }
                catch (ArgumentException) { }
            }
    }
}

public sealed record RenderExportSettings
{
    public int SizeMode { get; init; }
    public int Width { get; init; } = 2048;
    public int Height { get; init; } = 2048;
    public bool Transparent { get; init; } = true;
    public bool IncludeGrid { get; init; }
    public string OutputDirectory { get; init; } = "";
}

internal sealed class RenderColourJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (text is { Length: 9 } && text[0] == '#' &&
            uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba))
            return Color.FromArgb((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
        throw new JsonException("Invalid saved render colour.");
    }

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}");
}
