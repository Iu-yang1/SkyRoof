using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvImageLibraryTests
  {
    [Fact]
    public void LargePicturesAreScaledBeforeEditingAndPngImportSkipsReencoding()
    {
      string root = Path.Combine(Path.GetTempPath(),
        "SkyRoofSstvPerf-" + Guid.NewGuid().ToString("N"));
      try
      {
        Directory.CreateDirectory(root);
        string original = Path.Combine(root, "big.png");
        using (var bitmap = new Bitmap(2600, 1500))
        {
          using var g = Graphics.FromImage(bitmap);
          g.Clear(Color.MediumVioletRed);
          bitmap.Save(original, ImageFormat.Png);
        }
        using var scaled = SstvImageLibrary.LoadForEditing(original, 1024);
        Assert.True(scaled.Width <= 1024 && scaled.Height <= 1024);
        Assert.Equal(PixelFormat.Format32bppArgb, scaled.PixelFormat);
        using var scaledClipboard = SstvImageLibrary.ScaleForEditing(scaled, 640);
        Assert.True(scaledClipboard.Width <= 640 && scaledClipboard.Height <= 640);
        string imported = SstvImageLibrary.Import(Path.Combine(root, "gallery"), original);
        Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(imported));
      }
      finally
      {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
      }
    }

    [Fact]
    public void ImportExportAndDeleteImageOnlyInsideGallery()
    {
      string root = Path.Combine(Path.GetTempPath(),
        "SkyRoofSstvGallery-" + Guid.NewGuid().ToString("N"));
      string gallery = Path.Combine(root, "library");
      string outside = Path.Combine(root, "outside.png");
      string export = Path.Combine(root, "export.png");
      try
      {
        Directory.CreateDirectory(root);
        using (var picture = new Bitmap(40, 25))
        {
          using (var g = Graphics.FromImage(picture))
            g.Clear(Color.CornflowerBlue);
          picture.Save(outside, ImageFormat.Png);
        }

        string imported = SstvImageLibrary.Import(gallery, outside);
        Assert.True(File.Exists(imported));
        using (var loaded = Image.FromFile(imported))
        {
          Assert.Equal(40, loaded.Width);
          Assert.Equal(25, loaded.Height);
        }
        SstvImageLibrary.Export(imported, export);
        Assert.True(File.Exists(export));
        Assert.Equal(File.ReadAllBytes(imported), File.ReadAllBytes(export));

        // Deleting an arbitrary file outside the SSTV library must fail.
        Assert.Throws<InvalidOperationException>(() =>
          SstvImageLibrary.Delete(gallery, outside));
        string nested = Path.Combine(gallery, "nested");
        Directory.CreateDirectory(nested);
        string nestedPicture = Path.Combine(nested, "not-for-deletion.png");
        File.Copy(outside, nestedPicture);
        Assert.Throws<InvalidOperationException>(() =>
          SstvImageLibrary.Delete(gallery, nestedPicture));

        string metadata = Path.ChangeExtension(imported, ".json");
        File.WriteAllText(metadata, "{\"mode\":\"Robot 36\"}");
        Assert.True(SstvImageLibrary.Delete(gallery, imported));
        Assert.False(File.Exists(imported));
        Assert.False(File.Exists(metadata));
        Assert.False(SstvImageLibrary.Delete(gallery, imported));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(nestedPicture));
      }
      finally
      {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
      }
    }
  }
}
