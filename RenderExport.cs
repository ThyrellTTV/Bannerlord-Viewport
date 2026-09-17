using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LOTRAOM_Viewport;

public partial class MainWindow
{
    private bool _isUpdatingRenderSize;

    private void RenderExportToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyRenderSizePreset();
    }

    private void RenderSizePreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyRenderSizePreset();
    }

    private void ApplyRenderSizePreset()
    {
        var size = RenderSizePreset.SelectedIndex switch
        {
            0 => ((int)Math.Round(ModelViewport.RenderHost?.ActualWidth ?? ModelViewport.ActualWidth),
                  (int)Math.Round(ModelViewport.RenderHost?.ActualHeight ?? ModelViewport.ActualHeight)),
            1 => (2048, 2048),
            2 => (2048, 3072),
            3 => (3840, 2160),
            _ => (0, 0)
        };
        if (size.Item1 == 0 || size.Item2 == 0) return;
        _isUpdatingRenderSize = true;
        try
        {
            RenderWidthBox.Text = size.Item1.ToString(CultureInfo.InvariantCulture);
            RenderHeightBox.Text = size.Item2.ToString(CultureInfo.InvariantCulture);
        }
        finally { _isUpdatingRenderSize = false; }
    }

    private void RenderDimension_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingRenderSize) return;
        RenderSizePreset.SelectedIndex = 4;
        RenderExportStatusText.Text = "";
    }

    private void ExportRender_Click(object sender, RoutedEventArgs e)
    {
        RenderExportStatusText.Text = "";
        if (!int.TryParse(RenderWidthBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(RenderHeightBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
            width < 64 || height < 64 || width > 8192 || height > 8192 || (long)width * height > 33554432)
        {
            RenderExportStatusText.Text = "Use dimensions from 64 to 8192 pixels, up to 32 megapixels.";
            return;
        }
        var name = SelectedAssetTitle.Text;
        foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
        if (name.Length > 100) name = name[..100];
        var dialog = new SaveFileDialog
        {
            Title = "Export render", Filter = "PNG image (*.png)|*.png", DefaultExt = ".png",
            AddExtension = true, FileName = string.IsNullOrWhiteSpace(name) ? "render.png" : name + ".png"
        };
        // Freeze the selected frame while the save dialog runs its nested UI message loop.
        var resumePlayback = _posePlaybackTimer.IsEnabled;
        StopPosePlayback();
        try
        {
            if (dialog.ShowDialog(this) != true) return;
            SaveRenderButton.IsEnabled = false;
            SaveRenderPng(dialog.FileName, width, height, RenderBackgroundCombo.SelectedIndex == 0,
                RenderGridCheckBox.IsChecked == true);
            RenderExportStatusText.Text = $"Saved {width} x {height} PNG.";
        }
        catch (Exception exception)
        {
            RenderExportStatusText.Text = "Export failed: " + exception.Message;
        }
        finally
        {
            SaveRenderButton.IsEnabled = true;
            if (resumePlayback && _poseAvailable && _poseClip != null) PosePlay_Click(this, new RoutedEventArgs());
        }
    }

    private void SaveRenderPng(string path, int width, int height, bool transparent, bool includeGrid)
    {
        var bitmap = _renderer.CaptureRender(width, height, transparent, includeGrid);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
