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
